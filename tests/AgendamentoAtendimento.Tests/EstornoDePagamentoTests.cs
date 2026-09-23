using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Api.Controllers;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// Cancelar uma venda exigia estornar antes — e não existia estorno. Venda que recebeu
/// qualquer centavo nunca mais podia ser cancelada. E cancelar a venda finalizada não
/// devolvia ao estoque o que a finalização tinha baixado.
/// </summary>
public class EstornoDePagamentoTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;
    private VendasController _controller = null!;

    private long _clienteId;
    private long _produtoId;
    private long _formaId;

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"estorno-{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(opcoes, _contexto);

        var cliente = new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Marina" };
        var produto = new ItemCatalogo
        {
            TenantId = 1, Nome = "Leitor", Tipo = TipoItem.Produto, Preco = 100m, Estoque = 10,
        };
        var forma = new FormaPagamento { TenantId = 1, Nome = "Pix", Codigo = "PIX" };
        _db.AddRange(cliente, produto, forma);
        await _db.SaveChangesAsync();

        _clienteId = cliente.Id;
        _produtoId = produto.Id;
        _formaId = forma.Id;

        _controller = new VendasController(
            _db, new VendaService(_db, RelogioDeTeste.Utc), RelogioDeTeste.Utc)
        {
            ControllerContext = ContextoDoController.Com("*"),
        };
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private static VendaDto Dto<T>(ActionResult<T> r) =>
        (VendaDto)((ObjectResult)r.Result!).Value!;

    /// <summary>Venda de 2 × 100 finalizada (baixa 2 do estoque) e paga em dois recebimentos.</summary>
    private async Task<(long VendaId, IReadOnlyList<long> Pagamentos)> VendaPagaAsync()
    {
        var criada = Dto(await _controller.Criar(
            new VendaRequest(_clienteId, new[] { new VendaItemRequest(_produtoId, 2m, null) }, null),
            default));
        await _controller.Finalizar(criada.VendaId, default);
        await _controller.Receber(criada.VendaId, new PagamentoRequest(_formaId, 150m), default);
        var paga = Dto(await _controller.Receber(criada.VendaId, new PagamentoRequest(_formaId, 50m), default));

        Assert.Equal(StatusVenda.Paga, paga.Status);
        return (paga.VendaId, paga.Pagamentos.Select(p => p.PagamentoId).ToList());
    }

    private async Task<int?> EstoqueAsync() =>
        (await _db.ItensCatalogo.AsNoTracking().FirstAsync(i => i.Id == _produtoId)).Estoque;

    [Fact]
    public async Task Estorno_marca_o_pagamento_e_refaz_o_saldo()
    {
        var (vendaId, pagamentos) = await VendaPagaAsync();

        var depois = Dto(await _controller.Estornar(
            vendaId, pagamentos[1], new EstornarPagamentoRequest("  cliente desistiu  "), default));

        var estornado = depois.Pagamentos.Single(p => p.PagamentoId == pagamentos[1]);
        Assert.True(estornado.Estornado);
        Assert.Equal(StatusPagamento.Estornado, estornado.Status);
        Assert.NotNull(estornado.EstornadoEm);
        Assert.Equal("cliente desistiu", estornado.MotivoEstorno);
        Assert.False(depois.Pagamentos.Single(p => p.PagamentoId == pagamentos[0]).Estornado);

        Assert.Equal(150m, depois.TotalPago);
        Assert.Equal(50m, depois.SaldoAberto);
        // A venda estava paga e deixou de estar.
        Assert.Equal(StatusVenda.AguardandoPagamento, depois.Status);
    }

    [Fact]
    public async Task Estornar_duas_vezes_e_recusado()
    {
        var (vendaId, pagamentos) = await VendaPagaAsync();
        await _controller.Estornar(vendaId, pagamentos[0], null, default);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => _controller.Estornar(vendaId, pagamentos[0], null, default));

        Assert.Equal("PAGAMENTO_JA_ESTORNADO", erro.Codigo);
        var venda = await _db.Vendas.AsNoTracking().FirstAsync(v => v.Id == vendaId);
        Assert.Equal(50m, venda.TotalPago);
    }

    [Fact]
    public async Task Pagamento_de_outra_venda_nao_e_encontrado()
    {
        var (vendaA, pagamentosA) = await VendaPagaAsync();
        var (vendaB, _) = await VendaPagaAsync();
        _ = vendaA;

        await Assert.ThrowsAsync<NaoEncontradoException>(
            () => _controller.Estornar(vendaB, pagamentosA[0], null, default));
    }

    [Fact]
    public async Task Depois_do_estorno_total_a_venda_cancela_e_devolve_o_estoque()
    {
        var (vendaId, pagamentos) = await VendaPagaAsync();
        Assert.Equal(8, await EstoqueAsync());

        // Antes do estorno, o cancelamento continua recusado.
        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => _controller.Cancelar(vendaId, default));
        Assert.Equal("VENDA_COM_PAGAMENTO", recusa.Codigo);

        foreach (var pagamento in pagamentos)
        {
            await _controller.Estornar(vendaId, pagamento, null, default);
        }

        var semPagamento = await _db.Vendas.AsNoTracking().FirstAsync(v => v.Id == vendaId);
        Assert.Equal(0m, semPagamento.TotalPago);
        Assert.Equal(200m, semPagamento.TotalEstornado);
        Assert.Equal(StatusVenda.AguardandoPagamento, semPagamento.Status);

        Assert.IsType<NoContentResult>(await _controller.Cancelar(vendaId, default));

        var cancelada = await _db.Vendas.AsNoTracking().FirstAsync(v => v.Id == vendaId);
        Assert.Equal(StatusVenda.Cancelada, cancelada.Status);
        Assert.Equal(10, await EstoqueAsync());

        // Cancelar de novo não devolve o estoque outra vez.
        await _controller.Cancelar(vendaId, default);
        Assert.Equal(10, await EstoqueAsync());
    }

    [Fact]
    public async Task Cancelar_venda_nao_finalizada_nao_mexe_no_estoque()
    {
        var criada = Dto(await _controller.Criar(
            new VendaRequest(_clienteId, new[] { new VendaItemRequest(_produtoId, 3m, null) }, null),
            default));

        await _controller.Cancelar(criada.VendaId, default);

        Assert.Equal(10, await EstoqueAsync());
    }

    [Fact]
    public async Task Pagamento_de_cobranca_tambem_se_estorna()
    {
        var criada = Dto(await _controller.Criar(
            new VendaRequest(_clienteId, new[] { new VendaItemRequest(_produtoId, 1m, null) }, null),
            default));
        var vendas = new VendaService(_db, RelogioDeTeste.Utc);
        var venda = await _db.Vendas.Include(v => v.Pagamentos).FirstAsync(v => v.Id == criada.VendaId);
        var forma = await _db.FormasPagamento.FirstAsync(f => f.Id == _formaId);
        var pagamento = vendas.MontarPagamento(venda, forma, 100m, 1, MeioDeCaptura.PixQr);
        venda.Pagamentos.Add(pagamento);
        vendas.AtualizarStatus(venda);
        await _db.SaveChangesAsync();

        var depois = Dto(await _controller.Estornar(venda.Id, pagamento.Id, null, default));

        Assert.True(depois.Pagamentos.Single().Estornado);
        Assert.Equal(0m, depois.TotalPago);
    }

    [Fact]
    public void Estornar_exige_a_permissao_de_estorno_do_financeiro()
    {
        var atributo = typeof(VendasController).GetMethod(nameof(VendasController.Estornar))!
            .GetCustomAttribute<RequerPermissaoAttribute>();

        Assert.NotNull(atributo);
        Assert.Equal(RequerPermissaoAttribute.Prefixo + "financeiro.estornar", atributo!.Policy);
    }
}

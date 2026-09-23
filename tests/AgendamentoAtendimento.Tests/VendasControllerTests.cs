using System.Security.Claims;
using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Api.Controllers;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// O ciclo da venda pelo controller: o que o pedido pode mandar e em que ordem os passos
/// acontecem. Finalizar duas vezes baixava o estoque duas vezes; finalizar uma venda
/// cancelada a ressuscitava; e valores que o banco recusava viravam 500.
/// </summary>
public class VendasControllerTests : IAsyncLifetime
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
            .UseInMemoryDatabase($"vendas-{Guid.NewGuid()}")
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

    private async Task<long> CriarVendaAsync(decimal quantidade = 2m)
    {
        var criada = await _controller.Criar(
            new VendaRequest(_clienteId, new[] { new VendaItemRequest(_produtoId, quantidade, null) }, null),
            default);
        var dto = (VendaDto)((ObjectResult)criada.Result!).Value!;
        return dto.VendaId;
    }

    private async Task<int?> EstoqueAsync() =>
        (await _db.ItensCatalogo.AsNoTracking().FirstAsync(i => i.Id == _produtoId)).Estoque;

    [Fact]
    public async Task Finalizar_duas_vezes_nao_baixa_o_estoque_duas_vezes()
    {
        var id = await CriarVendaAsync();

        await _controller.Finalizar(id, default);
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => _controller.Finalizar(id, default));

        Assert.Equal("STATUS_INVALIDO", erro.Codigo);
        Assert.Equal(8, await EstoqueAsync());
    }

    [Fact]
    public async Task Venda_cancelada_nao_volta_a_vida_ao_finalizar()
    {
        var id = await CriarVendaAsync();
        await _controller.Cancelar(id, default);

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => _controller.Finalizar(id, default));

        var venda = await _db.Vendas.AsNoTracking().FirstAsync(v => v.Id == id);
        Assert.Equal(StatusVenda.Cancelada, venda.Status);
        Assert.Equal(10, await EstoqueAsync());
    }

    [Fact]
    public async Task Desconto_ou_preco_negativo_e_recusado()
    {
        await Assert.ThrowsAsync<RegraDeNegocioException>(() => _controller.Criar(
            new VendaRequest(_clienteId,
                new[] { new VendaItemRequest(_produtoId, 1m, null, DescontoValor: -50m) }, null),
            default));

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => _controller.Criar(
            new VendaRequest(_clienteId,
                new[] { new VendaItemRequest(_produtoId, 1m, PrecoUnitario: -100m) }, null),
            default));

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => _controller.Criar(
            new VendaRequest(_clienteId,
                new[] { new VendaItemRequest(_produtoId, 1m, null) }, null, DescontoGeral: -10m),
            default));
    }

    [Fact]
    public async Task Vendedor_que_nao_existe_e_recusado()
    {
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() => _controller.Criar(
            new VendaRequest(_clienteId,
                new[] { new VendaItemRequest(_produtoId, 1m, null) }, null, VendedorId: 999_999),
            default));

        Assert.Equal("VENDEDOR_INVALIDO", erro.Codigo);
    }

    [Fact]
    public async Task Trocar_para_cliente_que_nao_existe_e_404_e_nao_500()
    {
        var id = await CriarVendaAsync();

        await Assert.ThrowsAsync<NaoEncontradoException>(() => _controller.Atualizar(id,
            new VendaRequest(999_999, new[] { new VendaItemRequest(_produtoId, 1m, null) }, null),
            default));
    }

    [Fact]
    public async Task Recebimento_zerado_ou_com_forma_inexistente_nao_vira_500()
    {
        var id = await CriarVendaAsync();

        var zerado = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => _controller.Receber(id, new PagamentoRequest(_formaId, 0m), default));
        Assert.Equal("VALOR_INVALIDO", zerado.Codigo);

        await Assert.ThrowsAsync<NaoEncontradoException>(
            () => _controller.Receber(id, new PagamentoRequest(999_999, 10m), default));
    }

    [Fact]
    public async Task Venda_com_cobranca_em_andamento_nao_e_cancelada()
    {
        var id = await CriarVendaAsync();
        _db.Cobrancas.Add(new Cobranca
        {
            TenantId = 1, VendaId = id, ChaveIdempotencia = "toque-1",
            FormaPagamentoId = _formaId, Valor = 200m, Meio = MeioDeCaptura.TerminalPresente,
            Status = StatusCobranca.EmAndamento, ExpiraEm = DateTimeOffset.UtcNow.AddMinutes(5),
        });
        await _db.SaveChangesAsync();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => _controller.Cancelar(id, default));

        Assert.Equal("VENDA_COM_COBRANCA", erro.Codigo);
    }
}

/// <summary>Um usuário autenticado com as permissões pedidas, para chamar o controller.</summary>
internal static class ContextoDoController
{
    public static ControllerContext Com(params string[] permissoes) => Com(1, permissoes);

    public static ControllerContext Com(long usuarioId, params string[] permissoes)
    {
        var claims = new List<Claim>
        {
            new(ClaimsApp.TenantId, "1"),
            new(ClaimsApp.UsuarioId, usuarioId.ToString()),
        };
        claims.AddRange(permissoes.Select(p => new Claim(ClaimsApp.Permissao, p)));

        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "teste")),
            },
        };
    }
}

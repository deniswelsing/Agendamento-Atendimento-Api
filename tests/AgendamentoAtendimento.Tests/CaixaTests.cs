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
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// O caixa pelos controllers, nos pontos em que o dinheiro ou o estoque saíam errados: o
/// recebimento que não fechava a venda (e o estoque que nunca baixava), a venda fechada que
/// ainda se editava, o desconto que vazava de uma linha para outra, a comissão que não
/// seguia o vendedor, a cobrança expirada que prendia o caixa.
/// </summary>
public class CaixaTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;
    private VendasController _vendas = null!;
    private CobrancasController _cobrancas = null!;

    private long _clienteId;
    private long _produtoId;
    private long _servicoId;
    private long _pixId;
    private long _creditoId;
    private long _caioId;
    private long _brunaId;

    public async Task InitializeAsync()
    {
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"caixa-{Guid.NewGuid()}").Options,
            _contexto);

        var perfil = new Perfil { TenantId = 1, Nome = "Atendimento" };
        _db.Perfis.Add(perfil);
        await _db.SaveChangesAsync();
        var caio = new Usuario { TenantId = 1, Nome = "Caio", Email = "caio@x.com", PerfilId = perfil.Id };
        var bruna = new Usuario { TenantId = 1, Nome = "Bruna", Email = "bruna@x.com", PerfilId = perfil.Id };
        var cliente = new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Marina", Email = "m@x.com" };
        var produto = new ItemCatalogo
        {
            TenantId = 1, Nome = "Leitor", Tipo = TipoItem.Produto, Preco = 100m, Estoque = 3,
        };
        var servico = new ItemCatalogo
        {
            TenantId = 1, Nome = "Consultoria", Tipo = TipoItem.Servico, Preco = 200m,
            ComissaoPercentual = 10m, DuracaoMinutos = 30,
        };
        var pix = new FormaPagamento { TenantId = 1, Nome = "Pix", Codigo = "PIX" };
        var credito = new FormaPagamento
        {
            TenantId = 1, Nome = "Crédito", Codigo = "CREDITO", PermiteParcelamento = true, MaximoParcelas = 6,
        };
        _db.AddRange(caio, bruna, cliente, produto, servico, pix, credito);
        await _db.SaveChangesAsync();

        (_clienteId, _produtoId, _servicoId) = (cliente.Id, produto.Id, servico.Id);
        (_pixId, _creditoId, _caioId, _brunaId) = (pix.Id, credito.Id, caio.Id, bruna.Id);

        var vendaService = new VendaService(_db, RelogioDeTeste.Utc);
        _vendas = new VendasController(_db, vendaService, RelogioDeTeste.Utc)
        {
            ControllerContext = ContextoDoController.Com("*"),
        };
        _cobrancas = new CobrancasController(_db, new CobrancaService(_db, vendaService), vendaService)
        {
            ControllerContext = ContextoDoController.Com("*"),
        };
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private static T Corpo<T>(ActionResult<T> r) => (T)((ObjectResult)r.Result!).Value!;

    private async Task<VendaDto> VenderAsync(params VendaItemRequest[] itens) =>
        Corpo(await _vendas.Criar(new VendaRequest(_clienteId, itens, null), default));

    private async Task<int?> EstoqueAsync() =>
        (await _db.ItensCatalogo.AsNoTracking().FirstAsync(i => i.Id == _produtoId)).Estoque;

    [Fact]
    public async Task Receber_numa_venda_aberta_fecha_a_venda_e_baixa_o_estoque()
    {
        var venda = await VenderAsync(new VendaItemRequest(_produtoId, 2m, null));

        var depois = Corpo(await _vendas.Receber(venda.VendaId, new PagamentoRequest(_pixId, 50m), default));

        Assert.Equal(StatusVenda.AguardandoPagamento, depois.Status);
        Assert.Equal(1, await EstoqueAsync());

        // Fechada, não fecha de novo nem baixa outra vez.
        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => _vendas.Finalizar(venda.VendaId, default));
        Assert.Equal("STATUS_INVALIDO", recusa.Codigo);
        Corpo(await _vendas.Receber(venda.VendaId, new PagamentoRequest(_pixId, 150m), default));
        Assert.Equal(1, await EstoqueAsync());
    }

    [Fact]
    public async Task Fechar_sem_estoque_e_recusado_e_cancelar_devolve_so_o_que_saiu()
    {
        var primeira = await VenderAsync(new VendaItemRequest(_produtoId, 3m, null));
        var segunda = await VenderAsync(new VendaItemRequest(_produtoId, 3m, null));

        await _vendas.Finalizar(primeira.VendaId, default);
        Assert.Equal(0, await EstoqueAsync());

        var recusa = await Assert.ThrowsAsync<RecusaDeNegocioException>(
            () => _vendas.Finalizar(segunda.VendaId, default));
        Assert.Equal("ESTOQUE", recusa.Codigo);

        await _vendas.Cancelar(segunda.VendaId, default);
        await _vendas.Cancelar(primeira.VendaId, default);
        Assert.Equal(3, await EstoqueAsync());
    }

    [Fact]
    public async Task Venda_fechada_nao_se_edita()
    {
        var venda = await VenderAsync(new VendaItemRequest(_produtoId, 1m, null));
        await _vendas.Receber(venda.VendaId, new PagamentoRequest(_pixId, 50m), default);

        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(() => _vendas.Atualizar(
            venda.VendaId,
            new VendaRequest(_clienteId, new[] { new VendaItemRequest(_produtoId, 1m, 1m) }, null, DescontoGeral: 0),
            default));
        Assert.Equal("VENDA_FECHADA", recusa.Codigo);
    }

    [Fact]
    public async Task Desconto_maior_que_a_linha_e_recusado()
    {
        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(() => VenderAsync(
            new VendaItemRequest(_servicoId, 1m, null, DescontoValor: 300m),
            new VendaItemRequest(_produtoId, 1m, null)));
        Assert.Equal("DESCONTO_INVALIDO", recusa.Codigo);

        var geral = await Assert.ThrowsAsync<RegraDeNegocioException>(() => _vendas.Criar(
            new VendaRequest(_clienteId, new[] { new VendaItemRequest(_produtoId, 1m, null) }, null, DescontoGeral: 150m),
            default));
        Assert.Equal("DESCONTO_INVALIDO", geral.Codigo);
    }

    [Fact]
    public async Task Fracao_de_centavo_nao_vira_recebimento_de_zero()
    {
        var venda = await VenderAsync(new VendaItemRequest(_produtoId, 1m, null));

        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => _vendas.Receber(venda.VendaId, new PagamentoRequest(_pixId, 0.004m), default));
        Assert.Equal("VALOR_INVALIDO", recusa.Codigo);
    }

    [Fact]
    public async Task Tres_casas_viram_duas_antes_da_conta()
    {
        var venda = await VenderAsync(new VendaItemRequest(_servicoId, 2.333m, 33.33m));

        // 2,33 × 33,33 = 77,66: o total bate com a linha gravada.
        Assert.Equal(2.33m, venda.Itens.Single().Quantidade);
        Assert.Equal(77.66m, venda.TotalLiquido);
    }

    [Fact]
    public async Task Parcelas_e_forma_inativa_sao_conferidas()
    {
        var venda = await VenderAsync(new VendaItemRequest(_servicoId, 1m, null));

        var pixParcelado = await Assert.ThrowsAsync<RecusaDeNegocioException>(
            () => _vendas.Receber(venda.VendaId, new PagamentoRequest(_pixId, 50m, Parcelas: 12), default));
        Assert.Equal("PARCELAS_INVALIDAS", pixParcelado.Codigo);

        var alemDoMaximo = await Assert.ThrowsAsync<RecusaDeNegocioException>(
            () => _vendas.Receber(venda.VendaId, new PagamentoRequest(_creditoId, 50m, Parcelas: 99), default));
        Assert.Equal("PARCELAS_INVALIDAS", alemDoMaximo.Codigo);

        var pix = await _db.FormasPagamento.FirstAsync(f => f.Id == _pixId);
        pix.Ativa = false;
        await _db.SaveChangesAsync();
        var inativa = await Assert.ThrowsAsync<RecusaDeNegocioException>(
            () => _vendas.Receber(venda.VendaId, new PagamentoRequest(_pixId, 50m), default));
        Assert.Equal("FORMA_INATIVA", inativa.Codigo);
    }

    [Fact]
    public async Task Com_cobranca_em_andamento_nao_se_recebe_de_outro_jeito()
    {
        var venda = await VenderAsync(new VendaItemRequest(_servicoId, 1m, null));
        await _cobrancas.Abrir(venda.VendaId,
            new AbrirCobrancaRequest(_creditoId, 200m, MeioDeCaptura.TerminalPresente, "toque-1"), default);

        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => _vendas.Receber(venda.VendaId, new PagamentoRequest(_pixId, 200m), default));
        Assert.Equal("VENDA_COM_COBRANCA", recusa.Codigo);
    }

    [Fact]
    public async Task Abrir_cobranca_fecha_a_venda_antes_de_cobrar()
    {
        var venda = await VenderAsync(new VendaItemRequest(_produtoId, 2m, null));

        await _cobrancas.Abrir(venda.VendaId,
            new AbrirCobrancaRequest(_pixId, 200m, MeioDeCaptura.PixQr, "toque-2"), default);

        var fechada = await _db.Vendas.AsNoTracking().FirstAsync(v => v.Id == venda.VendaId);
        Assert.Equal(StatusVenda.AguardandoPagamento, fechada.Status);
        Assert.True(fechada.EstoqueBaixado);
        Assert.Equal(1, await EstoqueAsync());
    }

    [Fact]
    public async Task Cobranca_expirada_sai_como_expirada_e_cancelar_nao_trava()
    {
        var venda = await VenderAsync(new VendaItemRequest(_servicoId, 1m, null));
        var aberta = Corpo(await _cobrancas.Abrir(venda.VendaId,
            new AbrirCobrancaRequest(_creditoId, 200m, MeioDeCaptura.TerminalPresente, "toque-3"), default));

        var cobranca = await _db.Cobrancas.FirstAsync(c => c.Id == aberta.CobrancaId);
        cobranca.ExpiraEm = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        var lida = Corpo(await _cobrancas.Obter(aberta.CobrancaId, default));
        Assert.Equal(StatusCobranca.Expirada, lida.Status);
        Assert.False(lida.EstaAberta);

        var cancelada = Corpo(await _cobrancas.Cancelar(aberta.CobrancaId, new CancelarCobrancaRequest(null), default));
        Assert.Equal(StatusCobranca.Expirada, cancelada.Status);

        // E o caixa volta a receber a venda.
        var paga = Corpo(await _vendas.Receber(venda.VendaId, new PagamentoRequest(_pixId, 200m), default));
        Assert.Equal(StatusVenda.Paga, paga.Status);
    }

    [Fact]
    public async Task Taxa_real_fora_da_faixa_volta_400_e_nao_500()
    {
        var venda = await VenderAsync(new VendaItemRequest(_servicoId, 1m, null));
        var aberta = Corpo(await _cobrancas.Abrir(venda.VendaId,
            new AbrirCobrancaRequest(_creditoId, 200m, MeioDeCaptura.TerminalPresente, "toque-4"), default));

        foreach (var taxa in new[] { -1m, 250m })
        {
            var recusa = await Assert.ThrowsAsync<RecusaDeNegocioException>(() => _cobrancas.Concluir(
                aberta.CobrancaId, new ConcluirCobrancaRequest(true, ValorTaxaReal: taxa), default));
            Assert.Equal("TAXA_INVALIDA", recusa.Codigo);
        }
    }

    [Fact]
    public async Task Trocar_o_vendedor_da_venda_muda_a_comissao_das_linhas_que_herdam()
    {
        var criada = Corpo(await _vendas.Criar(new VendaRequest(
            _clienteId, new[] { new VendaItemRequest(_servicoId, 1m, null) }, null, VendedorId: _caioId), default));

        // A linha herda: o vendedor dela volta nulo, com o nome de quem leva de fato.
        var linha = criada.Itens.Single();
        Assert.Null(linha.VendedorId);
        Assert.Equal("Caio", linha.VendedorNome);

        // O checkout reenvia a linha como veio e troca só o vendedor da venda.
        var trocada = Corpo(await _vendas.Atualizar(criada.VendaId, new VendaRequest(
            _clienteId,
            new[] { new VendaItemRequest(_servicoId, 1m, linha.PrecoUnitario, VendedorId: linha.VendedorId) },
            null, VendedorId: _brunaId), default));

        var comissao = Assert.Single(trocada.ComissoesPorVendedor);
        Assert.Equal(_brunaId, comissao.VendedorId);
        Assert.Equal(20m, comissao.Valor);
    }

    [Fact]
    public async Task Venda_cancelada_nao_tem_saldo()
    {
        var venda = await VenderAsync(new VendaItemRequest(_servicoId, 1m, null));
        await _vendas.Cancelar(venda.VendaId, default);

        var lida = Corpo(await _vendas.Obter(venda.VendaId, default));
        Assert.Equal(0m, lida.SaldoAberto);
    }

    [Fact]
    public async Task A_venda_de_um_cliente_excluido_continua_aparecendo()
    {
        var venda = await VenderAsync(new VendaItemRequest(_servicoId, 1m, null));
        // Um cadastro excluído antes de a Api recusar excluir cliente com histórico: a
        // exclusão é lógica, e é o filtro dela que escondia a venda.
        var cliente = await _db.Clientes.FirstAsync(c => c.Id == _clienteId);
        cliente.Excluido = true;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var lida = Corpo(await _vendas.Obter(venda.VendaId, default));
        Assert.Equal("Marina", lida.ClienteNome);
        var lista = Corpo(await _vendas.Listar(null, null, null, null, null, default));
        Assert.Contains(lista.Itens, v => v.VendaId == venda.VendaId);
    }

    [Fact]
    public async Task Cliente_com_historico_nao_se_exclui()
    {
        await VenderAsync(new VendaItemRequest(_servicoId, 1m, null));
        var clientes = new ClientesController(_db) { ControllerContext = ContextoDoController.Com("*") };

        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => clientes.Remover(_clienteId, default));
        Assert.Equal("CLIENTE_COM_HISTORICO", recusa.Codigo);
    }
}

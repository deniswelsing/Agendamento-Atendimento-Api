using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// A cobrança é onde o dinheiro do cliente está em jogo. O que se testa aqui é o que não
/// pode dar errado: cobrar duas vezes, perder uma cobrança aprovada de vista, ou registrar
/// um líquido que não é o que entrou na conta.
/// </summary>
public class CobrancaServiceTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;
    private CobrancaService _cobrancas = null!;
    private VendaService _vendas = null!;
    private Venda _venda = null!;

    private const long FormaCredito = 10;

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"cobranca-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        _vendas = new VendaService(_db);
        _cobrancas = new CobrancaService(_db, _vendas);

        _db.FormasPagamento.Add(new FormaPagamento
        {
            Id = FormaCredito, TenantId = 1, Nome = "Crédito", Codigo = "CREDITO",
            TaxaPercentual = 3.49m, TaxaFixa = 0m, DiasParaLiquidacao = 30,
        });

        _db.Clientes.Add(new Cliente { Id = 1, TenantId = 1, Nome = "Marina Salgado" });
        _db.ItensCatalogo.Add(new ItemCatalogo
        {
            Id = 1, TenantId = 1, Nome = "Consultoria", Tipo = TipoItem.Servico, Preco = 1000m,
        });

        _venda = new Venda { Id = 1, TenantId = 1, ClienteId = 1 };
        _venda.Itens.Add(new VendaItem
        {
            Id = 1, TenantId = 1, VendaId = 1, ItemCatalogoId = 1, Tipo = TipoItem.Servico,
            Nome = "Consultoria", Quantidade = 1m, PrecoUnitario = 1000m,
        });
        _db.Vendas.Add(_venda);

        await _db.SaveChangesAsync();
        _vendas.RecalcularTotais(_venda);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private Task<(Cobranca Cobranca, bool JaExistia)> AbrirAsync(
        string chave, decimal valor = 1000m) =>
        _cobrancas.AbrirAsync(_venda, FormaCredito, valor, MeioDeCaptura.TerminalPresente,
            chave, adquirenteChave: "stone", terminalSerie: "ABC123");

    [Fact]
    public async Task A_mesma_chave_nunca_abre_duas_cobrancas()
    {
        var (primeira, jaExistia1) = await AbrirAsync("toque-1");
        var (segunda, jaExistia2) = await AbrirAsync("toque-1");

        Assert.False(jaExistia1);
        Assert.True(jaExistia2);
        Assert.Equal(primeira.Id, segunda.Id);
        Assert.Equal(1, await _db.Cobrancas.CountAsync());
    }

    [Fact]
    public async Task Duas_cobrancas_abertas_na_mesma_venda_sao_recusadas()
    {
        await AbrirAsync("toque-1");

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AbrirAsync("toque-2"));

        Assert.Contains("cobrança em andamento", erro.Message);
    }

    [Fact]
    public async Task Cobranca_acima_do_saldo_e_recusada()
    {
        var erro = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AbrirAsync("toque-1", 1500m));

        Assert.Contains("excede o saldo", erro.Message);
    }

    [Fact]
    public async Task Aprovada_vira_pagamento_com_os_dados_da_captura()
    {
        var (cobranca, _) = await AbrirAsync("toque-1");
        await _cobrancas.MarcarEnviadaAsync(cobranca);

        await _cobrancas.ConcluirAsync(cobranca, new ResultadoDaCaptura(
            Aprovada: true, Nsu: "000123456", CodigoAutorizacao: "A1B2C3",
            Bandeira: "Visa", UltimosDigitos: "4321", ValorTaxaReal: 41.20m));

        Assert.Equal(StatusCobranca.Aprovada, cobranca.Status);

        var pagamento = await _db.Pagamentos.SingleAsync();
        Assert.Equal("000123456", pagamento.Nsu);
        Assert.Equal("Visa", pagamento.Bandeira);
        Assert.Equal("4321", pagamento.UltimosDigitos);
        Assert.Equal("stone", pagamento.AdquirenteChave);
        Assert.Equal(MeioDeCaptura.TerminalPresente, pagamento.Meio);
        Assert.Equal(cobranca.PagamentoId, pagamento.Id);
    }

    [Fact]
    public async Task A_taxa_real_da_adquirente_substitui_a_estimada()
    {
        var (cobranca, _) = await AbrirAsync("toque-1");

        // A configuração previa 3,49% de R$ 1.000 = R$ 34,90. A adquirente cobrou 41,20.
        await _cobrancas.ConcluirAsync(cobranca, new ResultadoDaCaptura(
            Aprovada: true, Nsu: "1", ValorTaxaReal: 41.20m));

        var pagamento = await _db.Pagamentos.SingleAsync();
        Assert.Equal(34.90m, pagamento.ValorTaxaEstimada);
        Assert.Equal(41.20m, pagamento.ValorTaxa);
        Assert.Equal(958.80m, pagamento.ValorLiquido);
        Assert.True(pagamento.TaxaConferida);
        Assert.Equal(6.30m, pagamento.DivergenciaDaTaxa);
    }

    [Fact]
    public async Task Sem_taxa_informada_o_liquido_segue_sendo_previsao()
    {
        var (cobranca, _) = await AbrirAsync("toque-1");
        await _cobrancas.ConcluirAsync(cobranca, new ResultadoDaCaptura(Aprovada: true, Nsu: "1"));

        var pagamento = await _db.Pagamentos.SingleAsync();
        Assert.False(pagamento.TaxaConferida);
        Assert.Equal(34.90m, pagamento.ValorTaxa);
        Assert.Equal(0m, pagamento.DivergenciaDaTaxa);
    }

    [Fact]
    public async Task Concluir_duas_vezes_nao_duplica_o_pagamento()
    {
        var (cobranca, _) = await AbrirAsync("toque-1");
        var resultado = new ResultadoDaCaptura(Aprovada: true, Nsu: "1", ValorTaxaReal: 34.90m);

        await _cobrancas.ConcluirAsync(cobranca, resultado);
        await _cobrancas.ConcluirAsync(cobranca, resultado);

        Assert.Equal(1, await _db.Pagamentos.CountAsync());
    }

    [Fact]
    public async Task Recusada_nao_vira_pagamento()
    {
        var (cobranca, _) = await AbrirAsync("toque-1");

        await _cobrancas.ConcluirAsync(cobranca, new ResultadoDaCaptura(
            Aprovada: false, MotivoRecusa: "Saldo insuficiente"));

        Assert.Equal(StatusCobranca.Recusada, cobranca.Status);
        Assert.Equal("Saldo insuficiente", cobranca.MotivoRecusa);
        Assert.Equal(0, await _db.Pagamentos.CountAsync());
    }

    [Fact]
    public async Task Depois_de_recusada_a_venda_aceita_outra_cobranca()
    {
        var (primeira, _) = await AbrirAsync("toque-1");
        await _cobrancas.ConcluirAsync(primeira, new ResultadoDaCaptura(Aprovada: false));

        var (segunda, jaExistia) = await AbrirAsync("toque-2");

        Assert.False(jaExistia);
        Assert.NotEqual(primeira.Id, segunda.Id);
    }

    [Fact]
    public async Task Cobranca_expirada_nao_pode_ser_concluida()
    {
        var (cobranca, _) = await AbrirAsync("toque-1");
        cobranca.ExpiraEm = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _cobrancas.ConcluirAsync(cobranca, new ResultadoDaCaptura(Aprovada: true)));

        Assert.Contains("expirou", erro.Message);
        Assert.Equal(0, await _db.Pagamentos.CountAsync());
    }

    [Fact]
    public async Task Expirar_vencidas_fecha_o_que_ficou_aberto()
    {
        var (cobranca, _) = await AbrirAsync("toque-1");
        cobranca.ExpiraEm = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        var fechadas = await _cobrancas.ExpirarVencidasAsync();

        Assert.Equal(1, fechadas);
        Assert.Equal(StatusCobranca.Expirada, cobranca.Status);
    }

    [Fact]
    public async Task Cobranca_aprovada_quita_a_venda()
    {
        var (cobranca, _) = await AbrirAsync("toque-1");
        await _cobrancas.ConcluirAsync(cobranca, new ResultadoDaCaptura(Aprovada: true, Nsu: "1"));

        var venda = await _db.Vendas.Include(v => v.Itens).Include(v => v.Pagamentos)
            .SingleAsync(v => v.Id == 1);

        Assert.Equal(StatusVenda.Paga, venda.Status);
        Assert.Equal(0m, venda.SaldoAberto);
        Assert.NotNull(venda.FinalizadaEm);
    }

    [Fact]
    public async Task Lancamento_manual_fica_marcado_como_manual()
    {
        var pagamento = await _vendas.RegistrarPagamentoAsync(
            _venda, FormaCredito, 500m, autorizacao: "digitado-na-mao");

        Assert.Equal(MeioDeCaptura.Manual, pagamento.Meio);
        Assert.False(pagamento.TaxaConferida);
        Assert.Null(pagamento.Nsu);
        Assert.Equal(pagamento.ValorTaxaEstimada, pagamento.ValorTaxa);
    }

    [Fact]
    public async Task Conciliar_corrige_a_taxa_de_um_lancamento_manual()
    {
        var pagamento = await _vendas.RegistrarPagamentoAsync(_venda, FormaCredito, 1000m);
        Assert.Equal(965.10m, pagamento.ValorLiquido);

        _vendas.ConciliarTaxa(pagamento, 41.20m);
        await _db.SaveChangesAsync();

        Assert.Equal(958.80m, pagamento.ValorLiquido);
        Assert.Equal(6.30m, pagamento.DivergenciaDaTaxa);
        Assert.NotNull(pagamento.ConciliadoEm);
    }

    [Fact]
    public async Task O_pix_guarda_o_copia_e_cola_e_o_id_externo()
    {
        var (cobranca, _) = await _cobrancas.AbrirAsync(
            _venda, FormaCredito, 1000m, MeioDeCaptura.PixQr, "pix-1",
            adquirenteChave: "pix-psp");

        await _cobrancas.MarcarEnviadaAsync(cobranca, "00020126...5802BR");
        await _cobrancas.ConcluirAsync(cobranca, new ResultadoDaCaptura(
            Aprovada: true, TransacaoExternaId: "E1234567", ValorTaxaReal: 4m));

        Assert.Equal("00020126...5802BR", cobranca.PixCopiaECola);
        Assert.Equal("E1234567", cobranca.TransacaoExternaId);
        Assert.Equal(MeioDeCaptura.PixQr, (await _db.Pagamentos.SingleAsync()).Meio);
    }
}

/// <summary>
/// A comissão é promessa de dinheiro para quem atendeu. O que se testa aqui é que ela
/// sai do que foi realmente vendido, e não do catálogo de hoje.
/// </summary>
public class ComissaoTests
{
    private static VendaItem Item(decimal preco, decimal qtd, decimal desconto, decimal comissao) =>
        new()
        {
            Nome = "Serviço",
            Quantidade = qtd,
            PrecoUnitario = preco,
            DescontoValor = desconto,
            ComissaoPercentual = comissao,
        };

    [Fact]
    public void A_comissao_sai_do_liquido_do_item()
    {
        var item = Item(preco: 320m, qtd: 1m, desconto: 0m, comissao: 10m);

        Assert.Equal(32.00m, item.ComissaoValor);
    }

    [Fact]
    public void Desconto_dado_reduz_a_comissao()
    {
        // Quem deu 20 de desconto não comissiona sobre os 20.
        var item = Item(preco: 320m, qtd: 1m, desconto: 20m, comissao: 10m);

        Assert.Equal(300m, item.TotalLiquido);
        Assert.Equal(30.00m, item.ComissaoValor);
    }

    [Fact]
    public void Quantidade_multiplica_a_comissao()
    {
        var item = Item(preco: 100m, qtd: 3m, desconto: 0m, comissao: 12m);

        Assert.Equal(36.00m, item.ComissaoValor);
    }

    [Fact]
    public void Item_sem_comissao_nao_gera_nada()
    {
        Assert.Equal(0m, Item(preco: 500m, qtd: 1m, desconto: 0m, comissao: 0m).ComissaoValor);
    }

    [Fact]
    public void Sem_vendedor_a_venda_nao_tem_comissao()
    {
        // Comissão sem alguém para receber é número solto.
        var venda = new Venda { ClienteId = 1, VendedorId = null };
        venda.Itens.Add(Item(preco: 320m, qtd: 1m, desconto: 0m, comissao: 10m));

        Assert.Equal(0m, venda.TotalComissao);
    }

    [Fact]
    public void Com_vendedor_a_venda_soma_os_itens()
    {
        var venda = new Venda { ClienteId = 1, VendedorId = 7 };
        venda.Itens.Add(Item(preco: 320m, qtd: 1m, desconto: 0m, comissao: 10m));
        venda.Itens.Add(Item(preco: 890m, qtd: 1m, desconto: 0m, comissao: 12m));

        Assert.Equal(138.80m, venda.TotalComissao);
    }

    [Fact]
    public void A_comissao_arredonda_para_centavos()
    {
        var item = Item(preco: 333.33m, qtd: 1m, desconto: 0m, comissao: 7.5m);

        Assert.Equal(25.00m, item.ComissaoValor);
    }
}

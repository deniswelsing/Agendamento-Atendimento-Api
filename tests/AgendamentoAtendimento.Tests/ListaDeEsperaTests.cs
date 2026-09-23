using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// A fila de quem ficou sem horário. "Não tem vaga" não pode ser o fim da conversa: quem
/// ficou de fora é o primeiro a chamar quando alguém desmarca — e sem registro ninguém
/// lembra quem era.
/// </summary>
public class ListaDeEsperaTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;
    private ListaDeEsperaService _fila = null!;

    private static readonly DateOnly Segunda = new(2026, 9, 21);
    private static readonly DateTimeOffset Agora =
        new(new DateTime(2026, 9, 18, 9, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero);

    private long AnaId { get; set; }
    private long BrunoId { get; set; }
    private long CorteId { get; set; }
    private long BarbaId { get; set; }

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"espera-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        _fila = new ListaDeEsperaService(_db, RelogioDeTeste.Utc);

        var ana = new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Ana" };
        var bruno = new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Bruno" };
        _db.Clientes.AddRange(ana, bruno);

        var corte = new ItemCatalogo
        {
            TenantId = 1, Nome = "Corte", Tipo = TipoItem.Servico, Preco = 60m, DuracaoMinutos = 30,
        };
        var barba = new ItemCatalogo
        {
            TenantId = 1, Nome = "Barba", Tipo = TipoItem.Servico, Preco = 40m, DuracaoMinutos = 30,
        };
        _db.ItensCatalogo.AddRange(corte, barba);
        await _db.SaveChangesAsync();

        AnaId = ana.Id;
        BrunoId = bruno.Id;
        CorteId = corte.Id;
        BarbaId = barba.Id;
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    /// <summary>Um agendamento de um serviço, num dia, com alguém.</summary>
    private Agendamento Marcado(long itemId, long? responsavelId, DateOnly data)
    {
        var inicio = new DateTimeOffset(data.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        var agendamento = new Agendamento
        {
            TenantId = 1, ClienteId = BrunoId, Inicio = inicio, Fim = inicio.AddMinutes(30),
            ResponsavelId = responsavelId, Status = StatusAgendamento.Cancelado,
        };
        agendamento.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = itemId, Nome = "x", Ordem = 0,
            Quantidade = 1, DuracaoMinutos = 30, PrecoUnitario = 10m,
            ResponsavelId = responsavelId,
        });
        return agendamento;
    }

    [Fact]
    public async Task Entrar_duas_vezes_devolve_o_mesmo_lugar_na_fila()
    {
        var (primeira, jaEstava1) = await _fila.EntrarAsync(
            AnaId, CorteId, Segunda, null, null, default);
        var (segunda, jaEstava2) = await _fila.EntrarAsync(
            AnaId, CorteId, Segunda, null, "de novo", default);

        Assert.False(jaEstava1);
        // Dois lugares na fila para o mesmo cliente seriam dois lugares na fila.
        Assert.True(jaEstava2);
        Assert.Equal(primeira.Id, segunda.Id);
        Assert.Single(await _db.ListaDeEspera.ToListAsync());
    }

    [Fact]
    public async Task Cancelar_devolve_quem_esperava_por_aquele_servico_naquele_dia()
    {
        await _fila.EntrarAsync(AnaId, CorteId, Segunda, null, null, default);

        var esperando = await _fila.QuemEsperavaPorAsync(Marcado(CorteId, 7, Segunda), default);

        Assert.Equal(AnaId, Assert.Single(esperando).ClienteId);
    }

    [Fact]
    public async Task Quem_espera_por_outro_servico_nao_e_chamado()
    {
        // Quem espera por um corte não quer ser chamado para uma barba.
        await _fila.EntrarAsync(AnaId, BarbaId, Segunda, null, null, default);

        var esperando = await _fila.QuemEsperavaPorAsync(Marcado(CorteId, 7, Segunda), default);

        Assert.Empty(esperando);
    }

    [Fact]
    public async Task Quem_espera_por_outro_dia_nao_e_chamado()
    {
        await _fila.EntrarAsync(AnaId, CorteId, Segunda.AddDays(7), null, null, default);

        var esperando = await _fila.QuemEsperavaPorAsync(Marcado(CorteId, 7, Segunda), default);

        Assert.Empty(esperando);
    }

    [Fact]
    public async Task Quem_aceita_qualquer_dia_e_chamado_em_qualquer_dia()
    {
        // Data nula é "quero o serviço, tanto faz quando" — e é justamente quem mais
        // aproveita uma vaga que abre de véspera.
        await _fila.EntrarAsync(AnaId, CorteId, null, null, null, default);

        var naSegunda = await _fila.QuemEsperavaPorAsync(Marcado(CorteId, 7, Segunda), default);
        var naOutraSemana = await _fila.QuemEsperavaPorAsync(
            Marcado(CorteId, 7, Segunda.AddDays(7)), default);

        Assert.Single(naSegunda);
        Assert.Single(naOutraSemana);
    }

    [Fact]
    public async Task Quem_faz_questao_de_uma_pessoa_so_e_chamado_por_ela()
    {
        await _fila.EntrarAsync(AnaId, CorteId, Segunda, responsavelId: 7, null, default);

        var daPessoaCerta = await _fila.QuemEsperavaPorAsync(Marcado(CorteId, 7, Segunda), default);
        var deOutraPessoa = await _fila.QuemEsperavaPorAsync(Marcado(CorteId, 9, Segunda), default);

        Assert.Single(daPessoaCerta);
        Assert.Empty(deOutraPessoa);
    }

    [Fact]
    public async Task A_fila_respeita_a_ordem_de_chegada()
    {
        // Qualquer outra ordem precisaria de uma justificativa que ninguém deu.
        var (primeira, _) = await _fila.EntrarAsync(AnaId, CorteId, Segunda, null, null, default);
        await Task.Delay(5);
        var (segunda, _) = await _fila.EntrarAsync(BrunoId, CorteId, Segunda, null, null, default);

        var esperando = await _fila.QuemEsperavaPorAsync(Marcado(CorteId, 7, Segunda), default);

        Assert.Equal(new[] { primeira.Id, segunda.Id }, esperando.Select(e => e.Id));
    }

    [Fact]
    public async Task Avisar_tira_da_espera_mas_mantem_na_fila()
    {
        var (entrada, _) = await _fila.EntrarAsync(AnaId, CorteId, Segunda, null, null, default);

        var quantos = await _fila.MarcarComoAvisadosAsync(new[] { entrada.Id }, Agora, default);

        var depois = await _db.ListaDeEspera.FirstAsync(e => e.Id == entrada.Id);
        Assert.Equal(1, quantos);
        Assert.Equal(StatusNaEspera.Avisado, depois.Status);
        Assert.Equal(Agora, depois.AvisadoEm);
        // Avisado ainda é fila: o cliente pode não responder, e a vaga volta a ser dele.
        Assert.True(depois.NaFila);
    }

    [Fact]
    public async Task Quem_ja_saiu_da_fila_nao_e_chamado_de_novo()
    {
        var (entrada, _) = await _fila.EntrarAsync(AnaId, CorteId, Segunda, null, null, default);
        entrada.Status = StatusNaEspera.Cancelado;
        await _db.SaveChangesAsync();

        Assert.Empty(await _fila.QuemEsperavaPorAsync(Marcado(CorteId, 7, Segunda), default));
    }

    [Fact]
    public async Task Converter_fecha_a_espera_apontando_para_o_agendamento()
    {
        var (entrada, _) = await _fila.EntrarAsync(AnaId, CorteId, Segunda, null, null, default);

        await _fila.ConverterAsync(entrada.Id, agendamentoId: 42, default);

        var depois = await _db.ListaDeEspera.FirstAsync(e => e.Id == entrada.Id);
        Assert.Equal(StatusNaEspera.Convertido, depois.Status);
        Assert.Equal(42, depois.AgendamentoId);
        Assert.False(depois.NaFila);
    }

    [Fact]
    public async Task Espera_vencida_expira_mas_nao_some()
    {
        // Expirado é o que mostra demanda que a empresa não conseguiu atender. Apagar
        // apagaria o motivo de a fila existir.
        await _fila.EntrarAsync(AnaId, CorteId, Segunda, null, null, default);

        var quantas = await _fila.ExpirarVencidasAsync(Segunda.AddDays(1), default);

        var depois = await _db.ListaDeEspera.SingleAsync();
        Assert.Equal(1, quantas);
        Assert.Equal(StatusNaEspera.Expirado, depois.Status);
    }

    [Fact]
    public async Task Quem_aceita_qualquer_dia_nunca_expira_por_data()
    {
        await _fila.EntrarAsync(AnaId, CorteId, null, null, null, default);

        var quantas = await _fila.ExpirarVencidasAsync(Segunda.AddDays(365), default);

        Assert.Equal(0, quantas);
        Assert.Equal(StatusNaEspera.Aguardando, (await _db.ListaDeEspera.SingleAsync()).Status);
    }

    [Fact]
    public async Task Agendamento_sem_item_nao_chama_ninguem()
    {
        // Sem serviço não há o que oferecer: chamar a fila inteira seria prometer o que
        // não se sabe o que é.
        await _fila.EntrarAsync(AnaId, CorteId, Segunda, null, null, default);

        var vazio = new Agendamento
        {
            TenantId = 1, ClienteId = BrunoId, ResponsavelId = 7,
            Inicio = new DateTimeOffset(Segunda.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero),
            Fim = new DateTimeOffset(Segunda.ToDateTime(new TimeOnly(10, 30)), TimeSpan.Zero),
        };

        Assert.Empty(await _fila.QuemEsperavaPorAsync(vazio, default));
    }
}

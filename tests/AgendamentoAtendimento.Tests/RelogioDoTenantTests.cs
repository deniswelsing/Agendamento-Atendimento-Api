using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Api.Controllers;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.MultiTenancy;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// A empresa fica em São Paulo, e o horário dela é de lá. "Abre às 08:00" é 11:00Z: a
/// Api tratava 08:00 como 08:00Z, e a tela — que converte o instante para o fuso do
/// aparelho — oferecia 05:00. Aqui o relógio lê o fuso do próprio tenant, pelo banco,
/// como na Api de verdade.
/// </summary>
public class RelogioDoTenantTests : IAsyncLifetime
{
    private static readonly TimeSpan Brasilia = TimeSpan.FromHours(-3);

    // 21/09/2026 é uma segunda-feira.
    private static readonly DateOnly Segunda = new(2026, 9, 21);

    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;

    private long BrunaId { get; set; }
    private long CorteId { get; set; }
    private long AnaId { get; set; }

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"fuso-{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(opcoes, _contexto);

        _db.Tenants.Add(new Tenant
        {
            Id = 1, Slug = "sp", NomeEmpresa = "Empresa de São Paulo",
            FusoHorario = "America/Sao_Paulo",
        });

        _db.HorariosFuncionamento.Add(new HorarioFuncionamento
        {
            TenantId = 1, DiaDaSemana = DayOfWeek.Monday, Aberto = true,
            Abertura = new TimeOnly(8, 0), Fechamento = new TimeOnly(12, 0),
            IntervaloSlotMinutos = 30,
        });

        var perfil = new Perfil { TenantId = 1, Nome = "Atendimento" };
        _db.Perfis.Add(perfil);
        await _db.SaveChangesAsync();

        var bruna = new Usuario
        {
            TenantId = 1, Nome = "Bruna", Email = "bruna@teste.com",
            PerfilId = perfil.Id, Atendente = true,
        };
        var corte = new ItemCatalogo
        {
            TenantId = 1, Nome = "Corte", Tipo = TipoItem.Servico,
            Preco = 60m, DuracaoMinutos = 30, VisivelOnline = true,
        };
        var ana = new Cliente
        {
            TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Ana",
            Email = "ana@teste.com", AceitaEmail = true,
        };
        _db.AddRange(bruna, corte, ana);
        _db.ConfiguracoesDeLembrete.Add(new ConfiguracaoDeLembrete
        {
            TenantId = 1, Ativo = true, HorasDeAntecedencia = 24,
            AvisarAoMarcar = true, PedirConfirmacao = false,
        });
        await _db.SaveChangesAsync();

        _db.HorariosStaff.Add(new HorarioStaff
        {
            TenantId = 1, UsuarioId = bruna.Id, DiaDaSemana = DayOfWeek.Monday,
            Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(18, 0), Trabalha = true,
        });
        await _db.SaveChangesAsync();

        BrunaId = bruna.Id;
        CorteId = corte.Id;
        AnaId = ana.Id;
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    /// <summary>O relógio como a Api o monta: fuso lido do tenant do contexto.</summary>
    private RelogioDoTenant Relogio(DateTimeOffset? agora = null) =>
        new(_db, _contexto, agora is { } a ? new TempoParado(a) : TimeProvider.System);

    private DisponibilidadeService Disponibilidade(RelogioDoTenant? relogio = null) =>
        new(_db, _contexto, relogio ?? Relogio());

    private static DateTimeOffset EmBrasilia(int mes, int dia, int hora, int minuto = 0) =>
        new(2026, mes, dia, hora, minuto, 0, Brasilia);

    private async Task<long> MarcarAsync(DateTimeOffset inicio)
    {
        var agendamento = new Agendamento
        {
            TenantId = 1, ClienteId = AnaId, Inicio = inicio.ToUniversalTime(),
            Fim = inicio.ToUniversalTime().AddMinutes(30), Status = StatusAgendamento.Agendado,
            ResponsavelId = BrunaId,
        };
        _db.Agendamentos.Add(agendamento);
        await _db.SaveChangesAsync();
        return agendamento.Id;
    }

    // ------------------------------------------------------------------ grade

    [Fact]
    public async Task Encaixes_saem_na_hora_de_parede_da_empresa()
    {
        var dia = await Disponibilidade().ObterDiaAsync(Segunda, 30, itensIds: new[] { CorteId });

        // Abre às 08:00 de São Paulo: 11:00Z. E em UTC, que é o que o Npgsql grava.
        var primeiro = dia.Livres.First();
        Assert.Equal(EmBrasilia(9, 21, 8), primeiro.Inicio);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 11, 0, 0, TimeSpan.Zero), primeiro.Inicio);
        Assert.Equal(TimeSpan.Zero, primeiro.Inicio.Offset);

        // O último cabe antes do fechamento das 12:00 de lá.
        Assert.Equal(EmBrasilia(9, 21, 11, 30), dia.Livres.Last().Inicio);
    }

    [Fact]
    public async Task Pedido_vale_pelo_instante_e_e_conferido_na_hora_da_empresa()
    {
        var servico = Disponibilidade();

        // 08:00 de lá, mandado como a tela manda: o instante do encaixe.
        Assert.NotNull(await servico.MontarAtribuicoesAsync(
            EmBrasilia(9, 21, 8), new[] { CorteId }));

        // 08:00Z é 05:00 em São Paulo — antes de abrir.
        Assert.Null(await servico.MontarAtribuicoesAsync(
            new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero), new[] { CorteId }));

        Assert.True(await servico.PodePrestarAsync(
            BrunaId, CorteId, EmBrasilia(9, 21, 11, 30), EmBrasilia(9, 21, 12)));
        Assert.False(await servico.PodePrestarAsync(
            BrunaId, CorteId, EmBrasilia(9, 21, 12), EmBrasilia(9, 21, 12, 30)));
    }

    [Fact]
    public async Task Faixa_de_horario_e_de_parede()
    {
        var dia = await Disponibilidade().ObterDiaAsync(
            Segunda, 30, itensIds: new[] { CorteId },
            horaDe: new TimeOnly(10, 0), horaAte: new TimeOnly(11, 0));

        Assert.Equal(
            new[] { EmBrasilia(9, 21, 10), EmBrasilia(9, 21, 10, 30) },
            dia.Livres.Select(s => s.Inicio));
    }

    // ------------------------------------------------------------------ hoje

    [Fact]
    public void Hoje_as_23h30_de_Sao_Paulo_ainda_e_o_dia_22()
    {
        var agora = EmBrasilia(9, 22, 23, 30);

        Assert.Equal(new DateOnly(2026, 9, 22), Relogio(agora).Hoje());
        // Em UTC já seria amanhã — era o que a Api respondia.
        Assert.Equal(new DateOnly(2026, 9, 23), RelogioDeTeste.Em("UTC", agora).Hoje());
    }

    // ------------------------------------------------------------ página pública

    [Fact]
    public async Task Antecedencia_da_pagina_publica_conta_no_relogio_da_empresa()
    {
        // 07:00 de lá, com 2h de antecedência: o primeiro horário possível é 09:00.
        var agora = EmBrasilia(9, 21, 7);
        var relogio = Relogio(agora);
        var paginas = new PaginaPublicaService(
            _db, _contexto, Disponibilidade(relogio), new AssinaturaService(_db), relogio);
        var pagina = new ConfiguracaoPaginaPublica
        {
            TenantId = 1, Ativa = true, Slug = "sp",
            AntecedenciaMinimaHoras = 2, JanelaMaximaDias = 60,
        };

        var (resultado, dia) = await paginas.DisponibilidadeAsync(
            pagina, Segunda, new[] { CorteId }, null, agora);

        Assert.True(resultado.Ok);
        Assert.Equal(EmBrasilia(9, 21, 9), dia!.Livres.First().Inicio);
        Assert.DoesNotContain(dia.Livres, s => s.Inicio < EmBrasilia(9, 21, 9));
    }

    [Fact]
    public void Janela_da_pagina_publica_usa_as_datas_da_empresa()
    {
        // 21:00 de domingo em São Paulo — já é segunda em UTC.
        var agora = EmBrasilia(9, 20, 21);
        var relogio = Relogio(agora);
        var paginas = new PaginaPublicaService(
            _db, _contexto, Disponibilidade(relogio), new AssinaturaService(_db), relogio);
        var pagina = new ConfiguracaoPaginaPublica
        {
            TenantId = 1, Ativa = true, Slug = "sp",
            AntecedenciaMinimaHoras = 2, JanelaMaximaDias = 60,
        };

        var (primeira, ultima) = paginas.JanelaPublica(pagina, agora);

        // 21:00 + 2h = 23:00 do mesmo domingo: ainda dá para marcar no dia 20.
        Assert.Equal(new DateOnly(2026, 9, 20), primeira);
        Assert.Equal(new DateOnly(2026, 9, 20).AddDays(60), ultima);
    }

    // ------------------------------------------------------------------ listagem

    [Fact]
    public async Task Listar_o_dia_inclui_o_atendimento_das_22h_da_empresa()
    {
        // 22:00 de segunda em São Paulo é 01:00Z de terça.
        var id = await MarcarAsync(EmBrasilia(9, 21, 22));

        var relogio = Relogio();
        var controller = new AgendamentosController(
            _db, Disponibilidade(relogio),
            new LembreteService(_db, new EnviadorDeTeste(), relogio),
            new ListaDeEsperaService(_db, relogio), relogio)
        {
            ControllerContext = ContextoDoController.Com("*"),
        };

        async Task<IReadOnlyList<AgendamentoDto>> ListarAsync(DateOnly dia) =>
            (IReadOnlyList<AgendamentoDto>)((OkObjectResult)(await controller.Listar(
                dia, dia, null, null, null, default)).Result!).Value!;

        Assert.Contains(await ListarAsync(Segunda), a => a.AgendamentoId == id);
        Assert.DoesNotContain(await ListarAsync(Segunda.AddDays(1)), a => a.AgendamentoId == id);
    }

    // ------------------------------------------------------------------ lembretes

    [Fact]
    public async Task Lembrete_sai_N_horas_antes_e_fala_a_hora_da_empresa()
    {
        // Segunda, 28/09, às 08:00 de lá; lembrete de 24h.
        var id = await MarcarAsync(EmBrasilia(9, 28, 8));
        var enviador = new EnviadorDeTeste();
        var relogio = Relogio();
        var lembretes = new LembreteService(_db, enviador, relogio);

        await lembretes.ReprogramarAsync(id, EmBrasilia(9, 21, 9), default);

        var lembrete = await _db.Lembretes.SingleAsync(
            l => l.AgendamentoId == id && l.Tipo == TipoDeLembrete.Lembrete);
        Assert.Equal(EmBrasilia(9, 27, 8), lembrete.QuandoEnviar);
        Assert.Equal(new DateOnly(2026, 9, 27), relogio.DataLocal(lembrete.QuandoEnviar));
        Assert.Equal(new TimeOnly(8, 0), relogio.HoraLocal(lembrete.QuandoEnviar));

        await lembretes.DespacharAsync(EmBrasilia(9, 27, 8, 5), default);

        // O texto diz a hora que o cliente combinou, e não a de Greenwich.
        var mensagem = Assert.Single(enviador.Enviadas);
        Assert.Contains("28/09 às 08:00", mensagem.Assunto);
    }

    // ------------------------------------------------------------------ o relógio

    [Fact]
    public void Sao_Paulo_nao_tem_mais_horario_de_verao()
    {
        var relogio = RelogioDeTeste.Em("America/Sao_Paulo");

        // Janeiro e julho dão a mesma conta: -03:00 o ano inteiro.
        Assert.Equal(
            new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
            relogio.Instante(new DateOnly(2026, 1, 15), new TimeOnly(9, 0)));
        Assert.Equal(
            new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero),
            relogio.Instante(new DateOnly(2026, 7, 15), new TimeOnly(9, 0)));
    }

    [Fact]
    public void Nova_York_segue_o_horario_de_verao()
    {
        var relogio = RelogioDeTeste.Em("America/New_York");

        // Inverno -05:00, verão -04:00.
        Assert.Equal(
            new DateTimeOffset(2026, 1, 15, 14, 0, 0, TimeSpan.Zero),
            relogio.Instante(new DateOnly(2026, 1, 15), new TimeOnly(9, 0)));
        Assert.Equal(
            new DateTimeOffset(2026, 7, 1, 13, 0, 0, TimeSpan.Zero),
            relogio.Instante(new DateOnly(2026, 7, 1), new TimeOnly(9, 0)));

        // 08/03/2026 o relógio pula de 02:00 para 03:00: 02:30 não existe e anda para
        // 03:30 EDT, que é 07:30Z.
        var noPulo = relogio.Instante(new DateOnly(2026, 3, 8), new TimeOnly(2, 30));
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), noPulo);
        Assert.Equal(new TimeOnly(3, 30), relogio.HoraLocal(noPulo));

        // 01/11/2026 o relógio volta de 02:00 para 01:00: 01:30 acontece duas vezes, e
        // vale a primeira (EDT, 05:30Z).
        Assert.Equal(
            new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero),
            relogio.Instante(new DateOnly(2026, 11, 1), new TimeOnly(1, 30)));

        // O dia da virada tem 23 horas; o da volta, 25.
        var marco = new DateOnly(2026, 3, 8);
        Assert.Equal(TimeSpan.FromHours(23), relogio.FimDoDia(marco) - relogio.InicioDoDia(marco));
        var novembro = new DateOnly(2026, 11, 1);
        Assert.Equal(TimeSpan.FromHours(25),
            relogio.FimDoDia(novembro) - relogio.InicioDoDia(novembro));
    }

    [Fact]
    public void Fuso_desconhecido_cai_em_Sao_Paulo()
    {
        Assert.Equal(
            RelogioDoTenant.ResolverFuso(RelogioDoTenant.FusoPadrao).Id,
            RelogioDoTenant.ResolverFuso("Nao/Existe").Id);
        Assert.Equal(
            RelogioDoTenant.ResolverFuso(RelogioDoTenant.FusoPadrao).Id,
            RelogioDoTenant.ResolverFuso(null).Id);
    }

    [Fact]
    public void Cada_tenant_tem_o_seu_fuso_no_mesmo_escopo()
    {
        _db.Tenants.Add(new Tenant
        {
            Id = 2, Slug = "ny", NomeEmpresa = "Empresa de Nova York",
            FusoHorario = "America/New_York",
        });
        _db.SaveChanges();

        // Como o job faz: o mesmo relógio, o contexto trocando de tenant.
        var agora = new DateTimeOffset(2026, 9, 22, 3, 30, 0, TimeSpan.Zero);
        var relogio = Relogio(agora);

        _contexto.AssumirTenant(1);
        Assert.Equal(new DateOnly(2026, 9, 22), relogio.Hoje());
        _contexto.AssumirTenant(2);
        Assert.Equal(new DateOnly(2026, 9, 21), relogio.Hoje());
    }
}

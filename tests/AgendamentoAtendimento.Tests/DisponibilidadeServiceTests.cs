using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// O motor de disponibilidade é a regra mais delicada da agenda: janela da empresa,
/// jornada da pessoa, pausas, ausências e o que já está marcado.
/// </summary>
public class DisponibilidadeServiceTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;

    // 21/09/2026 é uma segunda-feira; 20/09/2026, um domingo.
    private static readonly DateOnly Segunda = new(2026, 9, 21);
    private static readonly DateOnly Domingo = new(2026, 9, 20);

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"disponibilidade-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);

        _db.HorariosFuncionamento.Add(new HorarioFuncionamento
        {
            TenantId = 1,
            DiaDaSemana = DayOfWeek.Monday,
            Aberto = true,
            Abertura = new TimeOnly(8, 0),
            Fechamento = new TimeOnly(12, 0),
            PausaInicio = new TimeOnly(10, 0),
            PausaFim = new TimeOnly(10, 30),
            IntervaloSlotMinutos = 30,
        });
        _db.HorariosFuncionamento.Add(new HorarioFuncionamento
        {
            TenantId = 1, DiaDaSemana = DayOfWeek.Sunday, Aberto = false,
        });

        var perfil = new Perfil { TenantId = 1, Nome = "Atendimento" };
        _db.Perfis.Add(perfil);
        await _db.SaveChangesAsync();

        var bruna = new Usuario
        {
            TenantId = 1, Nome = "Bruna", Email = "bruna@teste.com",
            PerfilId = perfil.Id, Atendente = true,
        };
        _db.Usuarios.Add(bruna);
        _db.Clientes.Add(new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Cliente" });
        await _db.SaveChangesAsync();

        _db.HorariosStaff.Add(new HorarioStaff
        {
            TenantId = 1, UsuarioId = bruna.Id, DiaDaSemana = DayOfWeek.Monday,
            Inicio = new TimeOnly(9, 0), Fim = new TimeOnly(18, 0), Trabalha = true,
        });
        await _db.SaveChangesAsync();

        UsuarioId = bruna.Id;
    }

    private long UsuarioId { get; set; }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Dia_fechado_na_empresa_nao_oferece_encaixe()
    {
        var dia = await new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc).ObterDiaAsync(Domingo, 30);

        Assert.False(dia.Aberto);
        Assert.Empty(dia.Livres);
    }

    [Fact]
    public async Task Janela_e_a_intersecao_da_empresa_com_a_jornada()
    {
        // Empresa 08:00-12:00, Bruna 09:00-18:00, pausa 10:00-10:30, slots de 30 min.
        // Cabem: 09:00, 09:30, 10:30, 11:00, 11:30.
        var dia = await new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc).ObterDiaAsync(Segunda, 30);

        var horas = dia.Livres.Select(s => TimeOnly.FromDateTime(s.Inicio.UtcDateTime).ToString("HH:mm"));
        Assert.Equal(new[] { "09:00", "09:30", "10:30", "11:00", "11:30" }, horas);
    }

    [Fact]
    public async Task Pausa_da_empresa_bloqueia_o_encaixe_que_a_atravessa()
    {
        var dia = await new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc).ObterDiaAsync(Segunda, 30);

        Assert.DoesNotContain(dia.Livres, s =>
            TimeOnly.FromDateTime(s.Inicio.UtcDateTime) == new TimeOnly(10, 0));
    }

    [Fact]
    public async Task Servico_longo_reduz_os_encaixes()
    {
        // Com 90 min só sobra 10:30 (10:30-12:00): as janelas antes esbarram na pausa.
        var dia = await new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc).ObterDiaAsync(Segunda, 90);

        var horas = dia.Livres.Select(s => TimeOnly.FromDateTime(s.Inicio.UtcDateTime).ToString("HH:mm"));
        Assert.Equal(new[] { "10:30" }, horas);
    }

    [Fact]
    public async Task Horario_ja_agendado_some_da_lista()
    {
        var inicio = new DateTimeOffset(Segunda.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
        _db.Agendamentos.Add(new Agendamento
        {
            TenantId = 1, ClienteId = 1, ResponsavelId = UsuarioId,
            Inicio = inicio, Fim = inicio.AddMinutes(30), Status = StatusAgendamento.Agendado,
        });
        await _db.SaveChangesAsync();

        var dia = await new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc).ObterDiaAsync(Segunda, 30);

        Assert.DoesNotContain(dia.Livres, s => s.Inicio == inicio);
        Assert.Equal(1, dia.TotalAgendamentos);
    }

    [Fact]
    public async Task Agendamento_cancelado_devolve_o_horario()
    {
        var inicio = new DateTimeOffset(Segunda.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
        _db.Agendamentos.Add(new Agendamento
        {
            TenantId = 1, ClienteId = 1, ResponsavelId = UsuarioId,
            Inicio = inicio, Fim = inicio.AddMinutes(30), Status = StatusAgendamento.Cancelado,
        });
        await _db.SaveChangesAsync();

        var dia = await new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc).ObterDiaAsync(Segunda, 30);

        Assert.Contains(dia.Livres, s => s.Inicio == inicio);
    }

    [Fact]
    public async Task Ausencia_do_dia_inteiro_zera_a_agenda_da_pessoa()
    {
        _db.ExcecoesHorarioStaff.Add(new ExcecaoHorarioStaff
        {
            TenantId = 1, UsuarioId = UsuarioId, Data = Segunda, DiaInteiro = true, Motivo = "Férias",
        });
        await _db.SaveChangesAsync();

        var dia = await new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc).ObterDiaAsync(Segunda, 30);

        Assert.True(dia.Aberto);
        Assert.Empty(dia.Livres);
    }

    [Fact]
    public async Task Excecao_de_data_fecha_a_empresa()
    {
        _db.ExcecoesHorarioFuncionamento.Add(new ExcecaoHorarioFuncionamento
        {
            TenantId = 1, Data = Segunda, Fechado = true, Motivo = "Feriado municipal",
        });
        await _db.SaveChangesAsync();

        var dia = await new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc).ObterDiaAsync(Segunda, 30);

        Assert.False(dia.Aberto);
        Assert.Equal("Feriado municipal", dia.MotivoFechado);
    }

    [Fact]
    public async Task Periodo_devolve_um_resumo_por_dia()
    {
        var dias = await new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc)
            .ObterPeriodoAsync(Domingo, Segunda, 30);

        Assert.Equal(2, dias.Count);
        Assert.False(dias[0].Aberto);
        Assert.True(dias[1].Aberto);
    }

    /// <summary>
    /// Empresa que fecha perto da meia-noite. `TimeOnly.AddMinutes` dá a volta no relógio:
    /// 23:30 + 30 min vira 00:00, que é "<= 23:30", e a grade nunca parava de andar — a
    /// requisição (inclusive a da página pública, sem token) girava para sempre.
    /// </summary>
    [Fact]
    public async Task Fechamento_perto_da_meia_noite_nao_prende_a_grade_num_laco()
    {
        var terca = new DateOnly(2026, 9, 22);
        _db.HorariosFuncionamento.Add(new HorarioFuncionamento
        {
            TenantId = 1, DiaDaSemana = DayOfWeek.Tuesday, Aberto = true,
            Abertura = new TimeOnly(8, 0), Fechamento = new TimeOnly(23, 30),
            IntervaloSlotMinutos = 30,
        });
        _db.HorariosStaff.Add(new HorarioStaff
        {
            TenantId = 1, UsuarioId = UsuarioId, DiaDaSemana = DayOfWeek.Tuesday,
            Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(23, 30), Trabalha = true,
        });
        await _db.SaveChangesAsync();

        // Em outra thread e com prazo: o laço era síncrono, e uma regressão prenderia o
        // próprio teste em vez de falhar.
        var servico = new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc);
        var dia = await Task.Run(() => servico.ObterDiaAsync(terca, 30))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(31, dia.Livres.Count);
        Assert.Equal(new TimeOnly(23, 0), TimeOnly.FromDateTime(dia.Livres[^1].Inicio.UtcDateTime));
        Assert.All(dia.Livres, s => Assert.True(s.Fim > s.Inicio));
    }
}

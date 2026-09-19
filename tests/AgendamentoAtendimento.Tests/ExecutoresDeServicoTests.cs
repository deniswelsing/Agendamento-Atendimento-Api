using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// Nem todo mundo sabe fazer tudo. Oferecer encaixe com quem não presta o serviço é
/// prometer ao cliente um horário que não existe.
/// </summary>
public class ExecutoresDeServicoTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;
    private DisponibilidadeService _servico = null!;

    // 21/09/2026 é uma segunda-feira.
    private static readonly DateOnly Segunda = new(2026, 9, 21);

    private long BrunaId { get; set; }
    private long CaioId { get; set; }
    private long CorteId { get; set; }
    private long BarbaId { get; set; }

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"executores-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        _servico = new DisponibilidadeService(_db);

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
        var caio = new Usuario
        {
            TenantId = 1, Nome = "Caio", Email = "caio@teste.com",
            PerfilId = perfil.Id, Atendente = true,
        };
        _db.Usuarios.AddRange(bruna, caio);
        _db.Clientes.Add(new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Cliente" });

        var corte = new ItemCatalogo
        {
            TenantId = 1, Nome = "Corte", Tipo = TipoItem.Servico,
            Preco = 60m, DuracaoMinutos = 30,
        };
        var barba = new ItemCatalogo
        {
            TenantId = 1, Nome = "Barba", Tipo = TipoItem.Servico,
            Preco = 40m, DuracaoMinutos = 30,
        };
        _db.ItensCatalogo.AddRange(corte, barba);
        await _db.SaveChangesAsync();

        BrunaId = bruna.Id;
        CaioId = caio.Id;
        CorteId = corte.Id;
        BarbaId = barba.Id;

        foreach (var id in new[] { BrunaId, CaioId })
        {
            _db.HorariosStaff.Add(new HorarioStaff
            {
                TenantId = 1, UsuarioId = id, DiaDaSemana = DayOfWeek.Monday,
                Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(12, 0), Trabalha = true,
            });
        }
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private async Task MarcarExecutorAsync(long itemId, params long[] usuarios)
    {
        foreach (var usuarioId in usuarios)
        {
            _db.ExecutoresDeServico.Add(new ExecutorDeServico
            {
                TenantId = 1, ItemCatalogoId = itemId, UsuarioId = usuarioId,
            });
        }
        await _db.SaveChangesAsync();
    }

    private Task<DiaDaAgenda> EncaixesAsync(params long[] itens) =>
        _servico.ObterDiaAsync(Segunda, 30, null, default, itens);

    [Fact]
    public async Task Servico_sem_executor_continua_aberto_a_todos()
    {
        // É o padrão, e o que mantém agendável tudo que existia antes desta regra.
        var dia = await EncaixesAsync(CorteId);

        Assert.Contains(dia.Livres, s => s.ResponsavelId == BrunaId);
        Assert.Contains(dia.Livres, s => s.ResponsavelId == CaioId);
    }

    [Fact]
    public async Task Marcar_alguem_fecha_a_lista_para_os_outros()
    {
        await MarcarExecutorAsync(CorteId, BrunaId);

        var dia = await EncaixesAsync(CorteId);

        Assert.Contains(dia.Livres, s => s.ResponsavelId == BrunaId);
        Assert.DoesNotContain(dia.Livres, s => s.ResponsavelId == CaioId);
    }

    [Fact]
    public async Task Varias_pessoas_podem_prestar_o_mesmo_servico()
    {
        await MarcarExecutorAsync(CorteId, BrunaId, CaioId);

        var dia = await EncaixesAsync(CorteId);

        Assert.Contains(dia.Livres, s => s.ResponsavelId == BrunaId);
        Assert.Contains(dia.Livres, s => s.ResponsavelId == CaioId);
    }

    [Fact]
    public async Task Dois_servicos_exigem_quem_presta_os_dois()
    {
        // Um encaixe é atendido por uma pessoa só: ela precisa dar conta do conjunto.
        await MarcarExecutorAsync(CorteId, BrunaId, CaioId);
        await MarcarExecutorAsync(BarbaId, CaioId);

        var dia = await _servico.ObterDiaAsync(Segunda, 60, null, default, new[] { CorteId, BarbaId });

        Assert.Contains(dia.Livres, s => s.ResponsavelId == CaioId);
        Assert.DoesNotContain(dia.Livres, s => s.ResponsavelId == BrunaId);
    }

    [Fact]
    public async Task Servico_restrito_convive_com_servico_aberto()
    {
        // Só o serviço que declarou executores restringe; o outro segue aberto.
        await MarcarExecutorAsync(BarbaId, CaioId);

        var dia = await _servico.ObterDiaAsync(Segunda, 60, null, default, new[] { CorteId, BarbaId });

        Assert.All(dia.Livres, s => Assert.Equal(CaioId, s.ResponsavelId));
    }

    [Fact]
    public async Task Ninguem_marcado_para_o_servico_pedido_deixa_o_dia_sem_encaixe()
    {
        // Um serviço cujos executores todos saíram do time não tem como ser agendado —
        // melhor o dia vazio do que oferecer alguém que não sabe fazer.
        await MarcarExecutorAsync(CorteId, 999);

        var dia = await EncaixesAsync(CorteId);

        Assert.Empty(dia.Livres);
        Assert.True(dia.Aberto);
    }

    [Fact]
    public async Task Quem_esta_ocupado_nao_aparece_mesmo_sabendo_fazer()
    {
        await MarcarExecutorAsync(CorteId, BrunaId, CaioId);

        // Bruna atende das 8 às 9; nesse intervalo só Caio pode ser oferecido.
        _db.Agendamentos.Add(new Agendamento
        {
            TenantId = 1, ClienteId = 1, ResponsavelId = BrunaId,
            Inicio = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero),
            Fim = new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero),
            Status = StatusAgendamento.Confirmado,
        });
        await _db.SaveChangesAsync();

        var dia = await EncaixesAsync(CorteId);
        var oitoHoras = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

        Assert.DoesNotContain(dia.Livres, s => s.Inicio == oitoHoras && s.ResponsavelId == BrunaId);
        Assert.Contains(dia.Livres, s => s.Inicio == oitoHoras && s.ResponsavelId == CaioId);
    }

    /// <summary>
    /// A semana e o mês passam pelo período. Se ele não levasse os itens, a semana
    /// prometeria encaixes que o dia recusa — e o cliente só descobriria ao abrir o dia.
    /// </summary>
    [Fact]
    public async Task O_periodo_filtra_igual_ao_dia()
    {
        await MarcarExecutorAsync(CorteId, BrunaId);

        var dias = await _servico.ObterPeriodoAsync(
            Segunda, Segunda, 30, null, default, new[] { CorteId });
        var semana = Assert.Single(dias);

        Assert.Contains(semana.Livres, s => s.ResponsavelId == BrunaId);
        Assert.DoesNotContain(semana.Livres, s => s.ResponsavelId == CaioId);

        var dia = await EncaixesAsync(CorteId);
        Assert.Equal(dia.Livres.Count, semana.Livres.Count);
    }

    /// <summary>Sem itens, o período segue mostrando o time inteiro.</summary>
    [Fact]
    public async Task O_periodo_sem_itens_mostra_todo_mundo()
    {
        await MarcarExecutorAsync(CorteId, BrunaId);

        var dias = await _servico.ObterPeriodoAsync(Segunda, Segunda, 30, null, default);
        var semana = Assert.Single(dias);

        Assert.Contains(semana.Livres, s => s.ResponsavelId == BrunaId);
        Assert.Contains(semana.Livres, s => s.ResponsavelId == CaioId);
    }

    [Fact]
    public async Task Filtrar_por_responsavel_ainda_respeita_a_habilidade()
    {
        await MarcarExecutorAsync(CorteId, CaioId);

        // O app pediu a agenda da Bruna, mas ela não presta este serviço.
        var dia = await _servico.ObterDiaAsync(Segunda, 30, BrunaId, default, new[] { CorteId });

        Assert.Empty(dia.Livres);
    }

    [Fact]
    public async Task Agendar_com_quem_nao_presta_o_servico_e_recusado()
    {
        await MarcarExecutorAsync(CorteId, CaioId);

        var livre = await _servico.EstaLivreAsync(
            new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 21, 8, 30, 0, TimeSpan.Zero),
            BrunaId, null, default, new[] { CorteId });

        Assert.False(livre);
    }
}

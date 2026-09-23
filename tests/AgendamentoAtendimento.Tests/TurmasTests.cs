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
/// Turma é o serviço que aceita mais de uma pessoa na mesma sessão. O horário continua
/// sendo oferecido enquanto houver vaga, em vez de sumir no primeiro inscrito — que é o
/// oposto de ser turma.
///
/// Não há entidade de "sessão": ela é o conjunto de agendamentos no mesmo serviço, com a
/// mesma pessoa atendendo, na mesma janela. Inventar uma tabela duplicaria essa verdade.
/// </summary>
public class TurmasTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;
    private DisponibilidadeService _servico = null!;

    private static readonly DateOnly Segunda = new(2026, 9, 21);

    private long BrunaId { get; set; }
    private long CaioId { get; set; }
    private long PilatesId { get; set; }
    private long CorteId { get; set; }

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"turmas-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        _servico = new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc);

        _db.HorariosFuncionamento.Add(new HorarioFuncionamento
        {
            TenantId = 1, DiaDaSemana = DayOfWeek.Monday, Aberto = true,
            Abertura = new TimeOnly(8, 0), Fechamento = new TimeOnly(12, 0),
            IntervaloSlotMinutos = 60,
        });

        var perfil = new Perfil { TenantId = 1, Nome = "Atendimento" };
        _db.Perfis.Add(perfil);
        await _db.SaveChangesAsync();

        var bruna = new Usuario
        {
            TenantId = 1, Nome = "Bruna", Email = "b@t.com", PerfilId = perfil.Id, Atendente = true,
        };
        var caio = new Usuario
        {
            TenantId = 1, Nome = "Caio", Email = "c@t.com", PerfilId = perfil.Id, Atendente = true,
        };
        _db.Usuarios.AddRange(bruna, caio);

        var pilates = new ItemCatalogo
        {
            TenantId = 1, Nome = "Pilates em grupo", Tipo = TipoItem.Servico,
            Preco = 80m, DuracaoMinutos = 60, CapacidadeTurma = 3,
        };
        var corte = new ItemCatalogo
        {
            TenantId = 1, Nome = "Corte", Tipo = TipoItem.Servico,
            Preco = 60m, DuracaoMinutos = 60, CapacidadeTurma = 1,
        };
        _db.ItensCatalogo.AddRange(pilates, corte);
        await _db.SaveChangesAsync();

        BrunaId = bruna.Id;
        CaioId = caio.Id;
        PilatesId = pilates.Id;
        CorteId = corte.Id;

        foreach (var id in new[] { BrunaId, CaioId })
        {
            _db.HorariosStaff.Add(new HorarioStaff
            {
                TenantId = 1, UsuarioId = id, DiaDaSemana = DayOfWeek.Monday,
                Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(12, 0), Trabalha = true,
            });
        }

        for (var i = 0; i < 6; i++)
        {
            _db.Clientes.Add(new Cliente
            {
                TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = $"Cliente {i + 1}",
            });
        }

        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private static DateTimeOffset Em(int hora) =>
        new(new DateTime(2026, 9, 21, hora, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero);

    /// <summary>Inscreve mais uma pessoa na sessão daquele serviço, àquela hora.</summary>
    private async Task InscreverAsync(long itemId, long responsavelId, int hora, long clienteId)
    {
        var agendamento = new Agendamento
        {
            TenantId = 1, ClienteId = clienteId, Inicio = Em(hora), Fim = Em(hora + 1),
            ResponsavelId = responsavelId, Status = StatusAgendamento.Agendado,
        };
        agendamento.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = itemId, Nome = "x", Ordem = 0,
            Quantidade = 1, DuracaoMinutos = 60, PrecoUnitario = 10m,
            ResponsavelId = responsavelId,
        });
        _db.Agendamentos.Add(agendamento);
        await _db.SaveChangesAsync();
    }

    private Task<DiaDaAgenda> DiaAsync(long itemId) =>
        _servico.ObterDiaAsync(Segunda, 60, null, default, new[] { itemId });

    private static SlotDisponivel? As(DiaDaAgenda dia, int hora, long responsavelId) =>
        dia.Livres.FirstOrDefault(s => s.Inicio == Em(hora) && s.ResponsavelId == responsavelId);

    [Fact]
    public async Task Servico_individual_some_da_grade_no_primeiro_marcado()
    {
        // É a regra de sempre, e ela não pode mudar por causa das turmas.
        await InscreverAsync(CorteId, BrunaId, 9, 1);
        var dia = await DiaAsync(CorteId);

        Assert.Null(As(dia, 9, BrunaId));
    }

    [Fact]
    public async Task Turma_continua_na_grade_enquanto_houver_vaga()
    {
        await InscreverAsync(PilatesId, BrunaId, 9, 1);

        var dia = await DiaAsync(PilatesId);
        var slot = As(dia, 9, BrunaId);

        Assert.NotNull(slot);
        var atribuicao = slot!.Atribuicoes[0];
        Assert.True(atribuicao.EhTurma);
        Assert.Equal(3, atribuicao.Capacidade);
        Assert.Equal(1, atribuicao.Inscritos);
        Assert.Equal(2, atribuicao.VagasRestantes);
    }

    [Fact]
    public async Task Turma_cheia_sai_da_grade()
    {
        await InscreverAsync(PilatesId, BrunaId, 9, 1);
        await InscreverAsync(PilatesId, BrunaId, 9, 2);
        await InscreverAsync(PilatesId, BrunaId, 9, 3);

        var dia = await DiaAsync(PilatesId);

        Assert.Null(As(dia, 9, BrunaId));
    }

    [Fact]
    public async Task Sessao_e_por_pessoa_que_atende_nao_por_servico()
    {
        // A turma da Bruna às 9h está cheia; a do Caio, no mesmo horário, está vazia.
        // Juntar as duas numa só contaria seis pessoas numa sala de três.
        await InscreverAsync(PilatesId, BrunaId, 9, 1);
        await InscreverAsync(PilatesId, BrunaId, 9, 2);
        await InscreverAsync(PilatesId, BrunaId, 9, 3);

        var dia = await DiaAsync(PilatesId);

        Assert.Null(As(dia, 9, BrunaId));
        Assert.NotNull(As(dia, 9, CaioId));
    }

    [Fact]
    public async Task Entrar_na_turma_e_aceito_ate_encher()
    {
        await InscreverAsync(PilatesId, BrunaId, 9, 1);
        await InscreverAsync(PilatesId, BrunaId, 9, 2);

        // A terceira vaga existe.
        Assert.True(await _servico.PodePrestarAsync(BrunaId, PilatesId, Em(9), Em(10)));

        await InscreverAsync(PilatesId, BrunaId, 9, 3);

        // A quarta não.
        Assert.False(await _servico.PodePrestarAsync(BrunaId, PilatesId, Em(9), Em(10)));
    }

    [Fact]
    public async Task Outro_servico_por_cima_da_turma_continua_impedindo()
    {
        // O conflito não é a sessão em que se quer entrar: é um corte individual em cima
        // dela. Vaga na turma não dá direito de marcar outra coisa no mesmo horário.
        await InscreverAsync(CorteId, BrunaId, 9, 1);

        Assert.False(await _servico.PodePrestarAsync(BrunaId, PilatesId, Em(9), Em(10)));
    }

    [Fact]
    public async Task Turma_de_outro_horario_nao_abre_vaga_neste()
    {
        // Mesma turma, hora diferente: são sessões diferentes, e a das 10h não tem nada
        // a ver com a lotação da das 9h.
        await InscreverAsync(PilatesId, BrunaId, 10, 1);

        var dia = await DiaAsync(PilatesId);
        var noveHoras = As(dia, 9, BrunaId);

        Assert.NotNull(noveHoras);
        Assert.Equal(0, noveHoras!.Atribuicoes[0].Inscritos);
    }

    [Fact]
    public void Capacidade_maior_que_um_e_o_que_faz_turma()
    {
        var individual = new ItemCatalogo
        {
            TenantId = 1, Nome = "Corte", Tipo = TipoItem.Servico, CapacidadeTurma = 1,
        };
        var turma = new ItemCatalogo
        {
            TenantId = 1, Nome = "Pilates", Tipo = TipoItem.Servico, CapacidadeTurma = 8,
        };
        var produto = new ItemCatalogo
        {
            TenantId = 1, Nome = "Shampoo", Tipo = TipoItem.Produto, CapacidadeTurma = 8,
        };

        Assert.False(individual.EhTurma);
        Assert.True(turma.EhTurma);
        // Produto não se agenda, então não tem turma por mais que o número diga.
        Assert.False(produto.EhTurma);
    }
}

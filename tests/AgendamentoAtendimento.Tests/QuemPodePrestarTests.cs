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
/// Quem a tela pode oferecer ao trocar o responsável de um serviço já marcado. Oferecer
/// o time inteiro e deixar a Api recusar depois promete uma troca que não acontece — a
/// pessoa só descobre no erro, depois de escolher.
/// </summary>
public class QuemPodePrestarTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;
    private DisponibilidadeService _servico = null!;

    // 21/09/2026 é uma segunda-feira.
    private static readonly DateTimeOffset Nove =
        new(new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero);

    private long BrunaId { get; set; }
    private long CaioId { get; set; }
    private long IrisId { get; set; }
    private long CorteId { get; set; }
    private long ClienteId { get; set; }

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"quem-pode-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        _servico = new DisponibilidadeService(_db, _contexto);

        _db.HorariosFuncionamento.Add(new HorarioFuncionamento
        {
            TenantId = 1, DiaDaSemana = DayOfWeek.Monday, Aberto = true,
            Abertura = new TimeOnly(8, 0), Fechamento = new TimeOnly(18, 0),
            IntervaloSlotMinutos = 30,
        });

        var perfil = new Perfil { TenantId = 1, Nome = "Atendimento" };
        _db.Perfis.Add(perfil);
        await _db.SaveChangesAsync();

        var pessoas = new[] { "Bruna", "Caio", "Íris" }.Select(nome => new Usuario
        {
            TenantId = 1, Nome = nome, Email = $"{nome.ToLowerInvariant()}@teste.com",
            PerfilId = perfil.Id, Atendente = true,
        }).ToList();
        _db.Usuarios.AddRange(pessoas);

        var cliente = new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Cliente" };
        _db.Clientes.Add(cliente);

        var corte = new ItemCatalogo
        {
            TenantId = 1, Nome = "Corte", Tipo = TipoItem.Servico,
            Preco = 60m, DuracaoMinutos = 60,
        };
        _db.ItensCatalogo.Add(corte);
        await _db.SaveChangesAsync();

        BrunaId = pessoas[0].Id;
        CaioId = pessoas[1].Id;
        IrisId = pessoas[2].Id;
        CorteId = corte.Id;
        ClienteId = cliente.Id;

        foreach (var pessoa in pessoas)
        {
            _db.HorariosStaff.Add(new HorarioStaff
            {
                TenantId = 1, UsuarioId = pessoa.Id, DiaDaSemana = DayOfWeek.Monday,
                Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(18, 0), Trabalha = true,
            });
        }
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private async Task<Agendamento> MarcarAsync(long responsavelId, DateTimeOffset inicio)
    {
        var agendamento = new Agendamento
        {
            TenantId = 1, ClienteId = ClienteId, ResponsavelId = responsavelId,
            Inicio = inicio, Fim = inicio.AddHours(1), Status = StatusAgendamento.Agendado,
        };
        agendamento.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = CorteId, Nome = "Corte", Quantidade = 1,
            DuracaoMinutos = 60, PrecoUnitario = 60m, ResponsavelId = responsavelId,
        });
        _db.Agendamentos.Add(agendamento);
        await _db.SaveChangesAsync();
        return agendamento;
    }

    private Task<IReadOnlyList<PessoaResumo>> CandidatosAsync(long? ignorar) =>
        _servico.QuemPodePrestarAsync(CorteId, Nove, Nove.AddHours(1), ignorar);

    [Fact]
    public async Task Sem_ninguem_ocupado_o_time_inteiro_aparece()
    {
        var livres = await CandidatosAsync(null);

        Assert.Equal(
            new[] { BrunaId, CaioId, IrisId }.OrderBy(x => x),
            livres.Select(p => p.UsuarioId).OrderBy(x => x));
    }

    /// <summary>
    /// O ponto do pedido: quem está atendendo outra pessoa nessa hora não pode ser
    /// oferecido. Mostrar e deixar a Api recusar é oferecer o que não existe.
    /// </summary>
    [Fact]
    public async Task Quem_esta_atendendo_naquele_horario_some_da_lista()
    {
        await MarcarAsync(CaioId, Nove);

        var livres = await CandidatosAsync(null);

        Assert.DoesNotContain(CaioId, livres.Select(p => p.UsuarioId));
        Assert.Contains(BrunaId, livres.Select(p => p.UsuarioId));
        Assert.Contains(IrisId, livres.Select(p => p.UsuarioId));
    }

    /// <summary>
    /// Compromisso em outra hora não tira ninguém: o corte é da janela, não do dia.
    /// </summary>
    [Fact]
    public async Task Ocupado_em_outro_horario_continua_disponivel()
    {
        await MarcarAsync(CaioId, Nove.AddHours(3));

        var livres = await CandidatosAsync(null);

        Assert.Contains(CaioId, livres.Select(p => p.UsuarioId));
    }

    /// <summary>
    /// Quem já está NESTE atendimento não está ocupado para efeito da troca — estaria
    /// ocupado consigo mesmo, e sumiria da própria lista.
    /// </summary>
    [Fact]
    public async Task Quem_ja_esta_no_atendimento_continua_na_lista_dele()
    {
        var agendamento = await MarcarAsync(CaioId, Nove);

        var semIgnorar = await CandidatosAsync(null);
        var ignorandoEste = await CandidatosAsync(agendamento.Id);

        Assert.DoesNotContain(CaioId, semIgnorar.Select(p => p.UsuarioId));
        Assert.Contains(CaioId, ignorandoEste.Select(p => p.UsuarioId));
    }

    [Fact]
    public async Task Quem_nao_presta_o_servico_nao_aparece()
    {
        _db.ExecutoresDeServico.Add(new ExecutorDeServico
        {
            TenantId = 1, ItemCatalogoId = CorteId, UsuarioId = BrunaId,
        });
        await _db.SaveChangesAsync();

        var livres = await CandidatosAsync(null);

        Assert.Equal(new[] { BrunaId }, livres.Select(p => p.UsuarioId));
    }

    /// <summary>
    /// Fora da jornada não é oferta. Quem não trabalha na segunda não pode pegar o corte
    /// da segunda, por mais livre que a agenda dela pareça.
    /// </summary>
    [Fact]
    public async Task Quem_nao_trabalha_no_dia_nao_aparece()
    {
        var jornada = await _db.HorariosStaff
            .FirstAsync(h => h.UsuarioId == IrisId && h.DiaDaSemana == DayOfWeek.Monday);
        jornada.Trabalha = false;
        await _db.SaveChangesAsync();

        var livres = await CandidatosAsync(null);

        Assert.DoesNotContain(IrisId, livres.Select(p => p.UsuarioId));
    }

    /// <summary>
    /// A lista e a validação da troca têm de concordar: tudo que a lista oferece o
    /// PodePrestarAsync aceita, e o que ela esconde ele recusa. Duas regras diferentes
    /// aqui dariam uma tela que promete o que a Api nega.
    /// </summary>
    [Fact]
    public async Task A_lista_concorda_com_a_validacao_da_troca()
    {
        await MarcarAsync(CaioId, Nove);

        var livres = (await CandidatosAsync(null)).Select(p => p.UsuarioId).ToList();

        foreach (var id in new[] { BrunaId, CaioId, IrisId })
        {
            var aceita = await _servico.PodePrestarAsync(id, CorteId, Nove, Nove.AddHours(1));
            Assert.Equal(livres.Contains(id), aceita);
        }
    }
}

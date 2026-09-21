using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Pacotes;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// As propostas de agenda de um cliente dentro do pacote: uma por sessão que falta, no
/// encaixe mais perto da hora combinada, e nenhuma além do ciclo que as paga.
/// </summary>
public class PropostasDoPacoteTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;
    private PacoteAgendaService _servico = null!;

    // 23/09/2026 é uma quarta-feira.
    private static readonly DateOnly Quarta = new(2026, 9, 23);

    private long _vinculoId;
    private CicloDoCliente _ciclo = null!;

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"propostas-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        _servico = new PacoteAgendaService(_db, new DisponibilidadeService(_db, _contexto));

        foreach (var dia in Enum.GetValues<DayOfWeek>())
        {
            _db.HorariosFuncionamento.Add(new HorarioFuncionamento
            {
                TenantId = 1, DiaDaSemana = dia, Aberto = true,
                Abertura = new TimeOnly(8, 0), Fechamento = new TimeOnly(18, 0),
                IntervaloSlotMinutos = 30,
            });
        }

        var perfil = new Perfil { TenantId = 1, Nome = "Atendimento" };
        var cliente = new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Ana" };
        _db.Perfis.Add(perfil);
        _db.Clientes.Add(cliente);
        await _db.SaveChangesAsync();

        var bruna = new Usuario
        {
            TenantId = 1, Nome = "Bruna", Email = "bruna@teste.com",
            PerfilId = perfil.Id, Atendente = true,
        };
        var servico = new ItemCatalogo
        {
            TenantId = 1, Nome = "Consultoria", Tipo = TipoItem.Servico,
            Preco = 300m, DuracaoMinutos = 60, Ativo = true,
        };
        _db.Usuarios.Add(bruna);
        _db.ItensCatalogo.Add(servico);
        await _db.SaveChangesAsync();

        foreach (var dia in Enum.GetValues<DayOfWeek>())
        {
            _db.HorariosStaff.Add(new HorarioStaff
            {
                TenantId = 1, UsuarioId = bruna.Id, DiaDaSemana = dia,
                Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(18, 0), Trabalha = true,
            });
        }

        var pacote = new Pacote
        {
            TenantId = 1, Nome = "Mensal 4", QuantidadePorCliente = 4,
            PrecoPorCliente = 1200m, Recorrencia = RecorrenciaDePacote.Mensal,
            InicioDoCicloAtual = new DateOnly(2026, 9, 21),
            FimDoCicloAtual = new DateOnly(2026, 10, 20),
        };
        _db.Pacotes.Add(pacote);
        await _db.SaveChangesAsync();

        _db.PacoteItens.Add(new PacoteItem
        {
            TenantId = 1, PacoteId = pacote.Id, ItemCatalogoId = servico.Id,
        });

        var vinculo = new PacoteCliente
        {
            TenantId = 1, PacoteId = pacote.Id, ClienteId = cliente.Id, Ativo = true,
            DiaDaSemana = DayOfWeek.Wednesday, Hora = new TimeOnly(14, 0),
            ResponsavelPreferidoId = bruna.Id,
        };
        _db.PacoteClientes.Add(vinculo);
        await _db.SaveChangesAsync();

        _ciclo = new CicloDoCliente
        {
            TenantId = 1, PacoteClienteId = vinculo.Id, Ciclo = 1,
            QuantidadeContratada = 4,
            Inicio = new DateOnly(2026, 9, 21), Fim = new DateOnly(2026, 10, 20),
        };
        _db.CiclosDePacote.Add(_ciclo);
        await _db.SaveChangesAsync();

        _vinculoId = vinculo.Id;
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Uma_proposta_por_sessao_do_saldo_na_hora_combinada()
    {
        var propostas = await _servico.ProporAsync(_vinculoId, Quarta);

        Assert.Equal(4, propostas.Count);
        Assert.All(propostas, p => Assert.Equal(DayOfWeek.Wednesday, p.Data.DayOfWeek));
        Assert.All(propostas, p => Assert.Equal(new TimeOnly(14, 0), TimeOnly.FromDateTime(p.Inicio!.Value.UtcDateTime)));
    }

    /// <summary>
    /// O ciclo é o teto. Sem isto, a quarta sessão cairia depois do fim do ciclo: o saldo
    /// deste ciclo pagaria um atendimento do seguinte, e num pacote sem recorrência o
    /// estorno do que sobrou sairia com o atendimento ainda marcado lá na frente.
    /// </summary>
    [Fact]
    public async Task Nenhuma_proposta_passa_do_fim_do_ciclo()
    {
        _ciclo.Fim = new DateOnly(2026, 10, 7);
        await _db.SaveChangesAsync();

        var propostas = await _servico.ProporAsync(_vinculoId, Quarta);

        // 23/09, 30/09 e 07/10 cabem; a de 14/10 não — e ela é o que vira crédito (ou
        // estorno) quando o ciclo fecha.
        Assert.Equal(3, propostas.Count);
        Assert.All(propostas, p => Assert.True(p.Data <= new DateOnly(2026, 10, 7)));
    }

    [Fact]
    public async Task A_semana_ja_marcada_nao_volta_como_proposta()
    {
        _db.Agendamentos.Add(new Agendamento
        {
            TenantId = 1, ClienteId = 1, PacoteClienteId = _vinculoId, PacoteCiclo = 1,
            Inicio = new DateTimeOffset(2026, 9, 30, 14, 0, 0, TimeSpan.Zero),
            Fim = new DateTimeOffset(2026, 9, 30, 15, 0, 0, TimeSpan.Zero),
            Status = StatusAgendamento.Agendado,
        });
        await _db.SaveChangesAsync();

        var propostas = await _servico.ProporAsync(_vinculoId, Quarta);

        // Uma sessão já está marcada: sobram três, e a semana dela sai da lista.
        Assert.Equal(3, propostas.Count);
        Assert.DoesNotContain(propostas, p => p.Data == new DateOnly(2026, 9, 30));
    }

    [Fact]
    public async Task Sem_dia_combinado_nao_ha_o_que_propor()
    {
        var vinculo = await _db.PacoteClientes.FirstAsync(c => c.Id == _vinculoId);
        vinculo.DiaDaSemana = null;
        await _db.SaveChangesAsync();

        // O pacote vira só saldo: as datas saem de onde o time quiser, uma a uma.
        Assert.Empty(await _servico.ProporAsync(_vinculoId, Quarta));
    }
}

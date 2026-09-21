using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.MultiTenancy;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// O teto diário serve a quem prefere atender bem menos gente do que mal muita: o dia
/// fecha quando enche, em vez de a agenda continuar oferecendo horário até não caber.
///
/// São dois tetos porque são duas perguntas: a empresa pode aguentar vinte atendimentos
/// num dia em que ninguém deveria fazer mais de seis.
/// </summary>
public class LimiteDiarioTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;

    private static readonly DateOnly Segunda = new(2026, 9, 21);

    private long BrunaId { get; set; }
    private long CaioId { get; set; }
    private long CorteId { get; set; }

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"limite-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        _db.Tenants.Add(new Tenant { Id = 1, Slug = "e", NomeEmpresa = "Empresa" });

        _db.HorariosFuncionamento.Add(new HorarioFuncionamento
        {
            TenantId = 1, DiaDaSemana = DayOfWeek.Monday, Aberto = true,
            Abertura = new TimeOnly(8, 0), Fechamento = new TimeOnly(18, 0),
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

        var corte = new ItemCatalogo
        {
            TenantId = 1, Nome = "Corte", Tipo = TipoItem.Servico, Preco = 60m, DuracaoMinutos = 60,
        };
        _db.ItensCatalogo.Add(corte);
        _db.Clientes.Add(new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Cliente" });
        await _db.SaveChangesAsync();

        BrunaId = bruna.Id;
        CaioId = caio.Id;
        CorteId = corte.Id;

        foreach (var id in new[] { BrunaId, CaioId })
        {
            _db.HorariosStaff.Add(new HorarioStaff
            {
                TenantId = 1, UsuarioId = id, DiaDaSemana = DayOfWeek.Monday,
                Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(18, 0), Trabalha = true,
            });
        }
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private static DateTimeOffset Em(int hora) =>
        new(new DateTime(2026, 9, 21, hora, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero);

    private async Task MarcarAsync(long responsavelId, int hora)
    {
        var agendamento = new Agendamento
        {
            TenantId = 1, ClienteId = 1, Inicio = Em(hora), Fim = Em(hora + 1),
            ResponsavelId = responsavelId, Status = StatusAgendamento.Agendado,
        };
        agendamento.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = CorteId, Nome = "Corte", Ordem = 0,
            Quantidade = 1, DuracaoMinutos = 60, PrecoUnitario = 60m,
            ResponsavelId = responsavelId,
        });
        _db.Agendamentos.Add(agendamento);
        await _db.SaveChangesAsync();
    }

    private async Task<DiaDaAgenda> DiaAsync(int limiteDoDia = 0, int porPessoa = 0)
    {
        var tenant = await _db.Tenants.FirstAsync(t => t.Id == 1);
        tenant.LimiteDiarioDeAtendimentos = limiteDoDia;
        tenant.LimiteDiarioPorPessoa = porPessoa;
        await _db.SaveChangesAsync();

        // Serviço novo a cada leitura: a política é lida uma vez por requisição.
        return await new DisponibilidadeService(_db, _contexto)
            .ObterDiaAsync(Segunda, 60, null, default, new[] { CorteId });
    }

    [Fact]
    public async Task Sem_teto_a_agenda_nao_muda()
    {
        // Zero é sem limite, e é o padrão: empresa que nunca pediu teto não ganha um.
        await MarcarAsync(BrunaId, 9);

        var dia = await DiaAsync();

        Assert.NotEmpty(dia.Livres);
        Assert.Null(dia.MotivoSemEncaixe);
    }

    [Fact]
    public async Task Dia_cheio_fecha_a_agenda_e_diz_por_que()
    {
        await MarcarAsync(BrunaId, 9);
        await MarcarAsync(CaioId, 9);

        var dia = await DiaAsync(limiteDoDia: 2);

        Assert.Empty(dia.Livres);
        // Um dia em branco sem motivo parece empresa fechada, que é outra coisa.
        Assert.Contains("fechou a agenda do dia", dia.MotivoSemEncaixe);
        Assert.Contains("2 de 2", dia.MotivoSemEncaixe);
        Assert.True(dia.Aberto);
    }

    [Fact]
    public async Task Abaixo_do_teto_do_dia_a_agenda_segue()
    {
        await MarcarAsync(BrunaId, 9);

        var dia = await DiaAsync(limiteDoDia: 5);

        Assert.NotEmpty(dia.Livres);
    }

    [Fact]
    public async Task Quem_bateu_o_proprio_teto_sai_da_grade()
    {
        await MarcarAsync(BrunaId, 9);
        await MarcarAsync(BrunaId, 11);

        var dia = await DiaAsync(porPessoa: 2);

        // A Bruna some; o Caio, que não atendeu ninguém, continua.
        Assert.DoesNotContain(dia.Livres, s => s.ResponsavelId == BrunaId);
        Assert.Contains(dia.Livres, s => s.ResponsavelId == CaioId);
    }

    [Fact]
    public async Task Um_atendimento_com_dois_servicos_da_mesma_pessoa_conta_uma_vez()
    {
        // O teto é de atendimentos, não de serviços: contar dois faria a pessoa bater o
        // limite na metade do caminho.
        var agendamento = new Agendamento
        {
            TenantId = 1, ClienteId = 1, Inicio = Em(9), Fim = Em(11),
            ResponsavelId = BrunaId, Status = StatusAgendamento.Agendado,
        };
        for (var i = 0; i < 2; i++)
        {
            agendamento.Itens.Add(new AgendamentoItem
            {
                TenantId = 1, ItemCatalogoId = CorteId, Nome = "Corte", Ordem = i,
                Quantidade = 1, DuracaoMinutos = 60, PrecoUnitario = 60m,
                ResponsavelId = BrunaId,
            });
        }
        _db.Agendamentos.Add(agendamento);
        await _db.SaveChangesAsync();

        var dia = await DiaAsync(porPessoa: 2);

        Assert.Contains(dia.Livres, s => s.ResponsavelId == BrunaId);
    }

    [Fact]
    public async Task Com_todo_o_time_no_teto_a_tela_diz_que_foi_o_teto()
    {
        await MarcarAsync(BrunaId, 9);
        await MarcarAsync(CaioId, 9);

        var dia = await DiaAsync(porPessoa: 1);

        Assert.Empty(dia.Livres);
        // "Ninguém está livre" seria mentira: o que fechou o dia foi o teto.
        Assert.Contains("bateu o teto", dia.MotivoSemEncaixe);
    }

    [Fact]
    public async Task A_criacao_direta_respeita_o_mesmo_teto()
    {
        // Sem isto, a criação furaria o limite que a grade respeita.
        await MarcarAsync(BrunaId, 9);
        await MarcarAsync(CaioId, 9);

        var tenant = await _db.Tenants.FirstAsync(t => t.Id == 1);
        tenant.LimiteDiarioDeAtendimentos = 2;
        await _db.SaveChangesAsync();

        var servico = new DisponibilidadeService(_db, _contexto);
        Assert.False(await servico.PodePrestarAsync(BrunaId, CorteId, Em(14), Em(15)));
    }

    [Fact]
    public async Task O_teto_por_pessoa_tambem_vale_na_criacao_direta()
    {
        await MarcarAsync(BrunaId, 9);

        var tenant = await _db.Tenants.FirstAsync(t => t.Id == 1);
        tenant.LimiteDiarioPorPessoa = 1;
        await _db.SaveChangesAsync();

        var servico = new DisponibilidadeService(_db, _contexto);
        Assert.False(await servico.PodePrestarAsync(BrunaId, CorteId, Em(14), Em(15)));
        // O Caio não bateu nada: continua podendo.
        Assert.True(await servico.PodePrestarAsync(CaioId, CorteId, Em(14), Em(15)));
    }

    [Fact]
    public async Task Cancelado_nao_conta_para_o_teto()
    {
        await MarcarAsync(BrunaId, 9);
        var cancelado = await _db.Agendamentos.FirstAsync();
        cancelado.Status = StatusAgendamento.Cancelado;
        await _db.SaveChangesAsync();

        var dia = await DiaAsync(limiteDoDia: 1);

        // Quem desmarcou devolveu a vaga: contá-lo manteria o dia fechado à toa.
        Assert.NotEmpty(dia.Livres);
    }
}

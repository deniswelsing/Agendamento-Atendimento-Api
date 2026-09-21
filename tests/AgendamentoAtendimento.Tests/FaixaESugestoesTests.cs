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
/// A faixa de horário que o cliente pede, e o que a agenda responde quando o dia
/// escolhido não serve. Dia vazio não pode ser beco sem saída: quem está com o cliente no
/// telefone precisa de opções na mesma resposta, e não de um "tente outro dia".
/// </summary>
public class FaixaESugestoesTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;
    private DisponibilidadeService _servico = null!;

    // 21/09/2026 é uma segunda-feira.
    private static readonly DateOnly Segunda = new(2026, 9, 21);

    private long AnaId { get; set; }
    private long CorteId { get; set; }
    private long ClienteId { get; set; }

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"faixa-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        _servico = new DisponibilidadeService(_db, _contexto);

        // A semana inteira aberta das 9 às 18, de hora em hora.
        foreach (var dia in Enum.GetValues<DayOfWeek>())
        {
            _db.HorariosFuncionamento.Add(new HorarioFuncionamento
            {
                TenantId = 1, DiaDaSemana = dia, Aberto = true,
                Abertura = new TimeOnly(9, 0), Fechamento = new TimeOnly(18, 0),
                IntervaloSlotMinutos = 60,
            });
        }

        var perfil = new Perfil { TenantId = 1, Nome = "Atendimento" };
        _db.Perfis.Add(perfil);
        await _db.SaveChangesAsync();

        var ana = new Usuario
        {
            TenantId = 1, Nome = "Ana", Email = "ana@teste.com",
            PerfilId = perfil.Id, Atendente = true,
        };
        _db.Usuarios.Add(ana);
        var cliente = new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Cliente" };
        _db.Clientes.Add(cliente);

        var corte = new ItemCatalogo
        {
            TenantId = 1, Nome = "Corte", Tipo = TipoItem.Servico, Preco = 60m, DuracaoMinutos = 60,
        };
        _db.ItensCatalogo.Add(corte);
        await _db.SaveChangesAsync();

        AnaId = ana.Id;
        CorteId = corte.Id;
        ClienteId = cliente.Id;

        foreach (var dia in Enum.GetValues<DayOfWeek>())
        {
            _db.HorariosStaff.Add(new HorarioStaff
            {
                TenantId = 1, UsuarioId = ana.Id, DiaDaSemana = dia,
                Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(18, 0), Trabalha = true,
            });
        }
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private long[] Servicos => new[] { CorteId };

    private Task<DiaDaAgenda> DiaAsync(
        DateOnly data, TimeOnly? de = null, TimeOnly? ate = null) =>
        _servico.ObterDiaAsync(data, 0, null, default, Servicos, null, null, null, de, ate);

    private async Task LotarAsync(DateOnly data)
    {
        var inicio = new DateTimeOffset(data.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
        _db.Agendamentos.Add(new Agendamento
        {
            TenantId = 1, ClienteId = ClienteId, ResponsavelId = AnaId,
            Inicio = inicio, Fim = inicio.AddHours(9), Status = StatusAgendamento.Agendado,
        });
        await _db.SaveChangesAsync();
    }

    private static string[] Horas(DiaDaAgenda dia) => dia.Livres
        .Select(s => TimeOnly.FromDateTime(s.Inicio.UtcDateTime).ToString("HH:mm"))
        .ToArray();

    [Fact]
    public async Task A_faixa_corta_o_que_comeca_antes_e_o_que_termina_depois()
    {
        var dia = await DiaAsync(Segunda, new TimeOnly(14, 0), new TimeOnly(16, 0));

        // 14:00 e 15:00 cabem inteiros; 16:00 terminaria 17:00, fora da faixa.
        Assert.Equal(new[] { "14:00", "15:00" }, Horas(dia));
    }

    [Fact]
    public async Task O_atendimento_inteiro_tem_de_caber_na_faixa()
    {
        // Quem pede "entre 14h e 15h" não quer um corte de uma hora que acaba 15h30.
        var dia = await DiaAsync(Segunda, new TimeOnly(14, 0), new TimeOnly(14, 30));

        Assert.Empty(dia.Livres);
    }

    [Fact]
    public async Task Vazio_por_causa_da_faixa_diz_isso_em_vez_de_culpar_a_agenda()
    {
        var dia = await DiaAsync(Segunda, new TimeOnly(19, 0), new TimeOnly(20, 0));

        Assert.Empty(dia.Livres);
        Assert.Equal("Há horário neste dia, mas não entre 19:00 e 20:00.", dia.MotivoSemEncaixe);
    }

    [Fact]
    public async Task Faixa_de_um_lado_so_tambem_e_faixa()
    {
        var sodepois = await DiaAsync(Segunda, new TimeOnly(15, 0));
        var soantes = await DiaAsync(Segunda, ate: new TimeOnly(11, 0));

        Assert.Equal(new[] { "15:00", "16:00", "17:00" }, Horas(sodepois));
        Assert.Equal(new[] { "09:00", "10:00" }, Horas(soantes));
    }

    [Fact]
    public async Task Sem_encaixe_no_dia_as_sugestoes_sao_os_dias_proximos()
    {
        await LotarAsync(Segunda);
        await LotarAsync(Segunda.AddDays(1));

        var sugestoes = await _servico.SugestoesAsync(Segunda, Servicos);

        // Segunda e terça estão cheias; as sugestões começam na quarta, e vêm três dias.
        Assert.Equal(3, sugestoes.Count);
        Assert.Equal(Segunda.AddDays(2), sugestoes[0].Data);
        Assert.Equal(Segunda.AddDays(4), sugestoes[^1].Data);
        Assert.All(sugestoes, s => Assert.Equal(3, s.Slots.Count));
    }

    [Fact]
    public async Task As_sugestoes_respeitam_a_faixa_pedida()
    {
        var sugestoes = await _servico.SugestoesAsync(
            Segunda, Servicos, horaDe: new TimeOnly(16, 0), horaAte: new TimeOnly(18, 0));

        // Sugerir um horário fora da faixa mandaria de volta para o mesmo problema.
        Assert.NotEmpty(sugestoes);
        Assert.All(sugestoes, s => Assert.All(s.Slots, slot =>
        {
            Assert.True(TimeOnly.FromDateTime(slot.Inicio.UtcDateTime) >= new TimeOnly(16, 0));
            Assert.True(TimeOnly.FromDateTime(slot.Fim.UtcDateTime) <= new TimeOnly(18, 0));
        }));
    }

    [Fact]
    public async Task As_sugestoes_comecam_no_dia_seguinte_ao_pedido()
    {
        // É o "ver outros dias": o cliente viu os horários deste e não quis nenhum, então
        // repetir este dia na lista não ajudaria ninguém.
        var sugestoes = await _servico.SugestoesAsync(Segunda.AddDays(1), Servicos);

        Assert.NotEmpty(sugestoes);
        Assert.All(sugestoes, s => Assert.True(s.Data > Segunda));
    }

    [Fact]
    public async Task Sem_nenhum_dia_com_encaixe_a_lista_volta_vazia()
    {
        var ana = await _db.Usuarios.FirstAsync(u => u.Id == AnaId);
        ana.Atendente = false;
        await _db.SaveChangesAsync();

        // Vazio é resposta: a tela diz "não achei nada nos próximos dias" em vez de
        // mostrar uma sugestão inventada.
        Assert.Empty(await _servico.SugestoesAsync(Segunda, Servicos));
    }
}

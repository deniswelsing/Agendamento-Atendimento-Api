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
/// Um atendimento passa por mais de uma pessoa: a Ana faz a manicure e o Bruno, em
/// seguida, a hidratação. Quando a tela já escolheu quem faz o quê, a grade tem de
/// responder por ESSAS pessoas — oferecer um horário em que o Bruno está ocupado só
/// porque a Carla poderia pegar a hidratação promete o que a Api depois recusa.
/// </summary>
public class EscolhaPorServicoTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;
    private DisponibilidadeService _servico = null!;

    // 21/09/2026 é uma segunda-feira.
    private static readonly DateOnly Segunda = new(2026, 9, 21);

    private long AnaId { get; set; }
    private long BrunoId { get; set; }
    private long CarlaId { get; set; }
    private long ManicureId { get; set; }
    private long HidratacaoId { get; set; }
    private long ClienteId { get; set; }

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"escolha-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        _servico = new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc);

        // 09:00-12:00 em passos de uma hora: com dois serviços de 30 min, os encaixes
        // possíveis são 09:00, 10:00 e 11:00.
        _db.HorariosFuncionamento.Add(new HorarioFuncionamento
        {
            TenantId = 1, DiaDaSemana = DayOfWeek.Monday, Aberto = true,
            Abertura = new TimeOnly(9, 0), Fechamento = new TimeOnly(12, 0),
            IntervaloSlotMinutos = 60,
        });

        var perfil = new Perfil { TenantId = 1, Nome = "Atendimento" };
        _db.Perfis.Add(perfil);
        await _db.SaveChangesAsync();

        var pessoas = new[] { "Ana", "Bruno", "Carla" }.Select(nome => new Usuario
        {
            TenantId = 1, Nome = nome, Email = $"{nome.ToLowerInvariant()}@teste.com",
            PerfilId = perfil.Id, Atendente = true,
        }).ToList();
        _db.Usuarios.AddRange(pessoas);

        var cliente = new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Cliente" };
        _db.Clientes.Add(cliente);

        var manicure = new ItemCatalogo
        {
            TenantId = 1, Nome = "Manicure", Tipo = TipoItem.Servico,
            Preco = 70m, DuracaoMinutos = 30,
        };
        var hidratacao = new ItemCatalogo
        {
            TenantId = 1, Nome = "Hidratação profunda", Tipo = TipoItem.Servico,
            Preco = 90m, DuracaoMinutos = 30,
        };
        _db.ItensCatalogo.AddRange(manicure, hidratacao);
        await _db.SaveChangesAsync();

        AnaId = pessoas[0].Id;
        BrunoId = pessoas[1].Id;
        CarlaId = pessoas[2].Id;
        ManicureId = manicure.Id;
        HidratacaoId = hidratacao.Id;
        ClienteId = cliente.Id;

        foreach (var pessoa in pessoas)
        {
            _db.HorariosStaff.Add(new HorarioStaff
            {
                TenantId = 1, UsuarioId = pessoa.Id, DiaDaSemana = DayOfWeek.Monday,
                Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(18, 0), Trabalha = true,
            });
        }

        // Ana faz manicure, Bruno faz hidratação, Carla faz as duas.
        _db.ExecutoresDeServico.AddRange(
            new ExecutorDeServico { TenantId = 1, ItemCatalogoId = ManicureId, UsuarioId = AnaId },
            new ExecutorDeServico { TenantId = 1, ItemCatalogoId = ManicureId, UsuarioId = CarlaId },
            new ExecutorDeServico { TenantId = 1, ItemCatalogoId = HidratacaoId, UsuarioId = BrunoId },
            new ExecutorDeServico { TenantId = 1, ItemCatalogoId = HidratacaoId, UsuarioId = CarlaId });
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private long[] Servicos => new[] { ManicureId, HidratacaoId };

    private Task<DiaDaAgenda> DiaAsync(IReadOnlyList<long?>? escolhas = null) =>
        _servico.ObterDiaAsync(Segunda, 0, null, default, Servicos, null, escolhas);

    private async Task OcuparAsync(long usuarioId, TimeOnly de, TimeOnly ate)
    {
        var inicio = new DateTimeOffset(Segunda.ToDateTime(de), TimeSpan.Zero);
        _db.Agendamentos.Add(new Agendamento
        {
            TenantId = 1, ClienteId = ClienteId, ResponsavelId = usuarioId,
            Inicio = inicio,
            Fim = new DateTimeOffset(Segunda.ToDateTime(ate), TimeSpan.Zero),
            Status = StatusAgendamento.Agendado,
        });
        await _db.SaveChangesAsync();
    }

    private static string[] Horas(DiaDaAgenda dia) => dia.Livres
        .Select(s => TimeOnly.FromDateTime(s.Inicio.UtcDateTime).ToString("HH:mm"))
        .ToArray();

    [Fact]
    public async Task Sem_escolha_a_grade_e_a_do_time()
    {
        var dia = await DiaAsync();

        Assert.Equal(new[] { "09:00", "10:00", "11:00" }, Horas(dia));
    }

    [Fact]
    public async Task Com_escolha_cada_servico_sai_com_quem_foi_escolhido()
    {
        var dia = await DiaAsync(new long?[] { AnaId, BrunoId });

        Assert.NotEmpty(dia.Livres);
        Assert.All(dia.Livres, slot =>
        {
            Assert.Equal(AnaId, slot.Atribuicoes[0].ResponsavelId);
            Assert.Equal(BrunoId, slot.Atribuicoes[1].ResponsavelId);
        });
    }

    /// <summary>
    /// O caso do pedido. O Bruno está ocupado das 10:30 às 11:00, que é justamente a
    /// janela da hidratação do encaixe das 10:00. A Carla poderia pegá-la — e por isso a
    /// grade sem escolha continua oferecendo as 10:00 —, mas quem foi escolhido é o Bruno.
    /// </summary>
    [Fact]
    public async Task Horario_em_que_o_escolhido_do_segundo_servico_esta_ocupado_nao_e_oferecido()
    {
        await OcuparAsync(BrunoId, new TimeOnly(10, 30), new TimeOnly(11, 0));

        Assert.Contains("10:00", Horas(await DiaAsync()));
        Assert.DoesNotContain("10:00", Horas(await DiaAsync(new long?[] { AnaId, BrunoId })));
    }

    [Fact]
    public async Task Escolher_so_um_dos_servicos_deixa_o_outro_com_quem_estiver_livre()
    {
        await OcuparAsync(AnaId, new TimeOnly(10, 0), new TimeOnly(10, 30));

        // Sem ninguém pedido para a hidratação, a Carla resolve o encaixe das 10:00 — mas
        // a manicure é da Ana, e ela está ocupada exatamente nessa meia hora.
        var dia = await DiaAsync(new long?[] { AnaId, null });

        Assert.DoesNotContain("10:00", Horas(dia));
        Assert.Contains("11:00", Horas(dia));
    }

    [Fact]
    public async Task Quem_nao_presta_o_servico_e_dito_pelo_nome()
    {
        var dia = await DiaAsync(new long?[] { BrunoId, null });

        // "Ninguém do time presta esse serviço" seria mentira: a Ana e a Carla prestam.
        Assert.Empty(dia.Livres);
        Assert.Equal("Bruno não presta Manicure.", dia.MotivoSemEncaixe);
    }

    [Fact]
    public async Task Dia_cheio_para_os_escolhidos_nao_culpa_o_time()
    {
        await OcuparAsync(BrunoId, new TimeOnly(9, 0), new TimeOnly(12, 0));

        var dia = await DiaAsync(new long?[] { AnaId, BrunoId });

        Assert.Empty(dia.Livres);
        Assert.Equal("Quem você escolheu não tem horário livre neste dia.", dia.MotivoSemEncaixe);
    }

    [Fact]
    public async Task A_semana_conta_o_que_o_dia_mostra()
    {
        await OcuparAsync(BrunoId, new TimeOnly(9, 0), new TimeOnly(12, 0));

        var semana = await _servico.ObterPeriodoAsync(
            Segunda, Segunda, 0, null, default, Servicos, new long?[] { AnaId, BrunoId });

        // Sem as escolhas aqui, o calendário prometeria encaixe num dia que a tela abre
        // vazio — e quem marca perderia o clique descobrindo isso.
        Assert.Empty(semana[0].Livres);
    }
}

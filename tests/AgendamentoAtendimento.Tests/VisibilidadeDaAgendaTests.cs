using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// Quem vê quais atendimentos. Sem `agenda.ver-todos` a pessoa enxerga só o que ela
/// mesma presta — e é o servidor que corta, porque mandar os dados e pedir para a tela
/// não olhar não é restringir nada.
/// </summary>
public class VisibilidadeDaAgendaTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;

    private const long Bruna = 10;
    private const long Caio = 20;
    private const long Iris = 30;

    private static readonly DateTimeOffset Base =
        new(new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"visibilidade-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        _db.Clientes.Add(new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Cliente" });
        await _db.SaveChangesAsync();

        // Só da Bruna.
        Marcar(1, Bruna, (Bruna, "Corte"));
        // Só do Caio.
        Marcar(2, Caio, (Caio, "Barba"));
        // Bruna responde, mas quem presta o segundo serviço é a Íris: os dois veem.
        Marcar(3, Bruna, (Bruna, "Corte"), (Iris, "Coloração"));
        // Sem ninguém: existe na agenda e não é de pessoa nenhuma.
        Marcar(4, null);
        await _db.SaveChangesAsync();
    }

    private void Marcar(long id, long? responsavel, params (long Quem, string Nome)[] itens)
    {
        var agendamento = new Agendamento
        {
            Id = id, TenantId = 1, ClienteId = 1,
            Inicio = Base.AddHours(id), Fim = Base.AddHours(id).AddMinutes(30),
            ResponsavelId = responsavel, Status = StatusAgendamento.Agendado,
        };

        var ordem = 0;
        foreach (var (quem, nome) in itens)
        {
            agendamento.Itens.Add(new AgendamentoItem
            {
                TenantId = 1, ItemCatalogoId = 1, Nome = nome, Ordem = ordem++,
                Quantidade = 1, DuracaoMinutos = 30, PrecoUnitario = 10m, ResponsavelId = quem,
            });
        }

        _db.Agendamentos.Add(agendamento);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private Task<List<long>> VistosPorAsync(long usuarioId, params string[] permissoes) =>
        VisibilidadeDaAgenda.Aplicar(_db.Agendamentos.AsNoTracking(), permissoes, usuarioId)
            .Select(a => a.Id).OrderBy(id => id).ToListAsync();

    [Fact]
    public async Task Sem_a_permissao_a_pessoa_ve_so_o_que_ela_presta()
    {
        Assert.Equal(new long[] { 1, 3 }, await VistosPorAsync(Bruna, "agenda.ver"));
        Assert.Equal(new long[] { 2 }, await VistosPorAsync(Caio, "agenda.ver"));
    }

    [Fact]
    public async Task Quem_presta_um_servico_do_meio_tambem_ve_o_atendimento()
    {
        // Íris não responde pelo atendimento 3 — ela só presta o segundo serviço. Sob a
        // regra antiga (só o responsável) ela não veria o que tem de atender.
        Assert.Equal(new long[] { 3 }, await VistosPorAsync(Iris, "agenda.ver"));
    }

    [Fact]
    public async Task Com_a_permissao_a_pessoa_ve_a_agenda_inteira()
    {
        Assert.Equal(
            new long[] { 1, 2, 3, 4 },
            await VistosPorAsync(Caio, "agenda.ver", "agenda.ver-todos"));
    }

    [Fact]
    public async Task Admin_ve_tudo_pelo_coringa()
    {
        // O perfil administrador carrega `*`: vê tudo e faz tudo, sem lista de chaves.
        Assert.Equal(new long[] { 1, 2, 3, 4 }, await VistosPorAsync(Caio, Permissoes.Coringa));
    }

    [Fact]
    public async Task Agendamento_sem_responsavel_nao_aparece_para_quem_e_restrito()
    {
        // Ele existe e não é de ninguém: quem só vê os seus não vê este. Aparecer para
        // todo mundo seria transformar "sem dono" em "de todos".
        var deTodos = await VistosPorAsync(Bruna, "agenda.ver");
        Assert.DoesNotContain(4L, deTodos);
    }

    [Fact]
    public void A_permissao_esta_no_catalogo_que_a_tela_de_perfis_desenha()
    {
        // O app não conhece permissão por conta própria: se não estiver aqui, o admin
        // não tem onde ligar ou desligar isso.
        var agenda = Permissoes.Modulos.Single(m => m.Chave == "agenda");

        Assert.Contains(agenda.Acoes, a => a.Chave == "ver-todos");
        Assert.True(Permissoes.Existe(VisibilidadeDaAgenda.VerTodos));
    }

    [Fact]
    public void Ver_todos_implica_ver_a_tela()
    {
        // Ligar só "ver todos" daria um perfil que enxerga tudo e não abre a agenda.
        var completas = Permissoes.ComTelasImplicadas(new[] { VisibilidadeDaAgenda.VerTodos });

        Assert.Contains("agenda.ver", completas);
    }
}

using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Persistencia.Seed;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// Os perfis com que um tenant nasce. Eles decidem quem enxerga a agenda do time e quem
/// fecha a venda — as duas linhas que separam quem atende de quem cobra. Um perfil que
/// nasce diferente do que a migração deu a quem já existia é a mesma empresa com duas
/// regras, dependendo do dia em que assinou.
/// </summary>
public class PerfisSemeadosTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new();
    private AppDbContext _db = null!;

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"perfis-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        await DadosIniciais.GarantirTenantDemoAsync(_db, _contexto, "Senha@2026");
        _contexto.IgnorarFiltroDeTenant = true;
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private async Task<IReadOnlyList<string>> PermissoesDe(string nome)
    {
        var perfil = await _db.Perfis.Include(p => p.Permissoes)
            .FirstOrDefaultAsync(p => p.Nome == nome);
        Assert.NotNull(perfil);
        return perfil!.Permissoes.Select(p => p.Permissao).ToList();
    }

    [Fact]
    public async Task O_tenant_nasce_com_os_dois_atendimentos()
    {
        var nomes = await _db.Perfis.Select(p => p.Nome).ToListAsync();

        Assert.Contains("Atendimento", nomes);
        Assert.Contains("Atendimento (só o seu)", nomes);
    }

    /// <summary>
    /// A migração `VerAgendaDeTodoOTime` deu `agenda.ver-todos` a quem já tinha
    /// `agenda.ver`. Se o seed não acompanhar, o tenant novo nasce com a agenda estreita
    /// e o antigo fica com a larga — sob o mesmo nome de perfil.
    /// </summary>
    [Fact]
    public async Task Atendimento_ve_a_agenda_do_time_como_a_migracao_deu_a_quem_ja_existia()
    {
        var permissoes = await PermissoesDe("Atendimento");

        Assert.Contains("agenda.ver", permissoes);
        Assert.Contains("agenda.ver-todos", permissoes);
    }

    /// <summary>Quem enxerga a agenda do time é quem fecha a venda do atendimento.</summary>
    [Fact]
    public async Task Quem_ve_todos_os_atendimentos_fecha_a_venda()
    {
        var permissoes = await PermissoesDe("Atendimento");

        Assert.Contains("vendas.finalizar", permissoes);
    }

    [Fact]
    public async Task Quem_so_ve_o_proprio_inicia_e_conclui_e_mais_nada()
    {
        var permissoes = await PermissoesDe("Atendimento (só o seu)");

        Assert.Contains("agenda.ver", permissoes);
        Assert.Contains("agenda.concluir", permissoes);
        // O ponto do perfil: a agenda é só dele, e a venda fica para outro fechar.
        Assert.DoesNotContain("agenda.ver-todos", permissoes);
        Assert.DoesNotContain("vendas.finalizar", permissoes);
        Assert.DoesNotContain("vendas.criar", permissoes);
        Assert.DoesNotContain("financeiro.receber", permissoes);
    }

    /// <summary>
    /// A regra que o protótipo demonstra, escrita como invariante: fechar venda anda com
    /// enxergar a agenda do time. Um perfil que fecha sem enxergar fecharia no escuro.
    /// </summary>
    [Fact]
    public async Task Nenhum_perfil_semeado_fecha_venda_sem_ver_a_agenda_do_time()
    {
        var perfis = await _db.Perfis.Include(p => p.Permissoes).ToListAsync();

        foreach (var perfil in perfis.Where(p => !p.Administrador))
        {
            var chaves = perfil.Permissoes.Select(x => x.Permissao).ToList();
            if (!chaves.Contains("vendas.finalizar") || !chaves.Contains("agenda.ver"))
            {
                // Financeiro fecha venda e nem abre a agenda: não é quem atende, e a
                // regra vale para quem está na agenda.
                continue;
            }

            Assert.True(
                chaves.Contains("agenda.ver-todos"),
                $"{perfil.Nome} fecha venda e vê a agenda, mas só a própria.");
        }
    }
}

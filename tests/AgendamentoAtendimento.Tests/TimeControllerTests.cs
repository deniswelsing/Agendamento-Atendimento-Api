using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Api.Controllers;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// `time.editar` é para cuidar do time, não para virar administrador. Sem a checagem,
/// quem tinha só essa permissão trocava o próprio perfil pelo de administrador e ganhava
/// a permissão coringa.
/// </summary>
public class TimeControllerTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1 };
    private AppDbContext _db = null!;

    private Perfil _admin = null!;
    private Perfil _gerente = null!;
    private Usuario _dono = null!;
    private Usuario _gerenteUsuario = null!;

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"time-{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(opcoes, _contexto);

        _admin = new Perfil { TenantId = 1, Nome = "Administrador", Administrador = true };
        _gerente = new Perfil { TenantId = 1, Nome = "Gerente" };
        _db.Perfis.AddRange(_admin, _gerente);
        await _db.SaveChangesAsync();

        _dono = new Usuario { TenantId = 1, Nome = "Dono", Email = "dono@x.com", PerfilId = _admin.Id };
        _gerenteUsuario = new Usuario
        {
            TenantId = 1, Nome = "Gerente", Email = "gerente@x.com", PerfilId = _gerente.Id,
        };
        _db.Usuarios.AddRange(_dono, _gerenteUsuario);
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private TimeController Controller(long usuarioId, params string[] permissoes) =>
        new(_db, new AssinaturaService(_db))
        {
            ControllerContext = ContextoDoController.Com(usuarioId, permissoes),
        };

    [Fact]
    public async Task Quem_so_edita_o_time_nao_se_promove_a_administrador()
    {
        var controller = Controller(_gerenteUsuario.Id, "time.ver", "time.editar");

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() => controller.Atualizar(
            _gerenteUsuario.Id, new AtualizarMembroRequest(null, _admin.Id, null, null), default));

        Assert.Equal("SO_ADMINISTRADOR", erro.Codigo);
        var depois = await _db.Usuarios.AsNoTracking().FirstAsync(u => u.Id == _gerenteUsuario.Id);
        Assert.Equal(_gerente.Id, depois.PerfilId);
    }

    [Fact]
    public async Task Quem_so_edita_o_time_nao_mexe_no_administrador()
    {
        var controller = Controller(_gerenteUsuario.Id, "time.ver", "time.editar");

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => controller.Atualizar(
            _dono.Id, new AtualizarMembroRequest(null, _gerente.Id, false, null), default));
    }

    [Fact]
    public async Task Administrador_concede_o_perfil_de_administrador()
    {
        var controller = Controller(_dono.Id, "*");

        await controller.Atualizar(
            _gerenteUsuario.Id, new AtualizarMembroRequest(null, _admin.Id, null, null), default);

        var depois = await _db.Usuarios.AsNoTracking().FirstAsync(u => u.Id == _gerenteUsuario.Id);
        Assert.Equal(_admin.Id, depois.PerfilId);
    }
}

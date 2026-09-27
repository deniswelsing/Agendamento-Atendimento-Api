using System.Security.Claims;
using System.Text.Json;
using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Api.Controllers;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// As permissões viajam no access token, que vale uma hora. Sem a conferência por
/// requisição, quem era rebaixado continuava com o perfil antigo até o token vencer — um
/// administrador rebaixado devolvia a si mesmo o perfil de administrador com o token velho —
/// e quem era desativado ou removido seguia lendo e gravando pela mesma hora.
/// </summary>
public class AcessoAtualTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1 };
    private AppDbContext _db = null!;
    private readonly CacheDeAcesso _cache = new();

    private Perfil _admin = null!;
    private Perfil _financeiro = null!;
    private Usuario _dono = null!;
    private Usuario _outroAdmin = null!;

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"acesso-{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(opcoes, _contexto);

        _admin = new Perfil { TenantId = 1, Nome = "Administrador", Administrador = true };
        _financeiro = new Perfil { TenantId = 1, Nome = "Financeiro" };
        _financeiro.Permissoes.Add(new PerfilPermissao { Permissao = "financeiro.ver" });
        _db.Perfis.AddRange(_admin, _financeiro);
        await _db.SaveChangesAsync();

        _dono = new Usuario { TenantId = 1, Nome = "Dono", Email = "dono@x.com", PerfilId = _admin.Id };
        _outroAdmin = new Usuario { TenantId = 1, Nome = "Outro", Email = "outro@x.com", PerfilId = _admin.Id };
        _db.Usuarios.AddRange(_dono, _outroAdmin);
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    /// <summary>Roda o middleware com o token de <paramref name="usuarioId"/>.</summary>
    private async Task<(HttpContext Http, bool Seguiu)> ChamarAsync(
        long usuarioId, string caminho = "/api/clientes", params string[] permissoesDoToken)
    {
        _contexto.TenantId = 1;
        _contexto.UsuarioId = usuarioId;
        var http = new DefaultHttpContext
        {
            User = ContextoDoController.Com(usuarioId, permissoesDoToken).HttpContext.User,
        };
        http.Request.Path = caminho;
        http.Response.Body = new MemoryStream();

        var seguiu = false;
        var middleware = new AcessoAtualMiddleware(_ => { seguiu = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(http, _contexto, _db, _cache);
        return (http, seguiu);
    }

    private static string CodigoDoErro(HttpContext http)
    {
        http.Response.Body.Position = 0;
        using var json = JsonDocument.Parse(http.Response.Body);
        return json.RootElement.GetProperty("code").GetString()!;
    }

    private TimeController Time(long usuarioId, params string[] permissoes) =>
        new(_db, new AssinaturaService(_db), acessos: _cache)
        {
            ControllerContext = ContextoDoController.Com(usuarioId, permissoes),
        };

    [Fact]
    public async Task Rebaixado_perde_a_permissao_do_token_na_hora()
    {
        // O token do "outro" foi emitido quando ele era administrador.
        await ChamarAsync(_outroAdmin.Id, "/api/clientes", "*");

        await Time(_dono.Id, "*").Atualizar(
            _outroAdmin.Id, new AtualizarMembroRequest(null, _financeiro.Id, null, null), default);

        var (http, seguiu) = await ChamarAsync(_outroAdmin.Id, "/api/clientes", "*");
        Assert.True(seguiu);
        Assert.Equal(new[] { "financeiro.ver" }, http.User.Permissoes());
    }

    [Fact]
    public async Task Rebaixado_nao_se_promove_de_volta_com_o_token_velho()
    {
        await Time(_dono.Id, "*").Atualizar(
            _outroAdmin.Id, new AtualizarMembroRequest(null, _financeiro.Id, null, null), default);

        // O token ainda diz "*"; o middleware troca pelas permissões de agora, e é com elas
        // que o controller decide.
        var (http, _) = await ChamarAsync(_outroAdmin.Id, "/api/time/membros", "*");
        var controller = new TimeController(_db, new AssinaturaService(_db), acessos: _cache)
        {
            ControllerContext = new() { HttpContext = http },
        };

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() => controller.Atualizar(
            _outroAdmin.Id, new AtualizarMembroRequest(null, _admin.Id, null, null), default));
        Assert.Equal("SO_ADMINISTRADOR", erro.Codigo);
    }

    [Fact]
    public async Task Desativado_para_na_hora_mesmo_com_token_valido()
    {
        await ChamarAsync(_outroAdmin.Id, "/api/clientes", "*"); // já no cache, ativo

        await Time(_dono.Id, "*").Atualizar(
            _outroAdmin.Id, new AtualizarMembroRequest(null, null, false, null), default);

        var (http, seguiu) = await ChamarAsync(_outroAdmin.Id, "/api/clientes", "*");
        Assert.False(seguiu);
        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.Equal("ACESSO_REVOGADO", CodigoDoErro(http));
    }

    [Fact]
    public async Task Removido_para_na_hora_mesmo_com_token_valido()
    {
        await ChamarAsync(_outroAdmin.Id, "/api/clientes", "*");

        await Time(_dono.Id, "*").Remover(_outroAdmin.Id, default);

        var (http, seguiu) = await ChamarAsync(_outroAdmin.Id, "/api/bootstrap", "*");
        Assert.False(seguiu);
        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
    }

    [Fact]
    public async Task Permissao_nova_do_perfil_vale_para_quem_ja_tem_token()
    {
        var usuario = new Usuario { TenantId = 1, Nome = "Rita", Email = "rita@x.com", PerfilId = _financeiro.Id };
        _db.Usuarios.Add(usuario);
        await _db.SaveChangesAsync();
        await ChamarAsync(usuario.Id, "/api/vendas", "financeiro.ver");

        var perfis = new PerfisController(_db, _cache)
        {
            ControllerContext = ContextoDoController.Com(_dono.Id, "*"),
        };
        await perfis.Atualizar(_financeiro.Id,
            new PerfilRequest("Financeiro", null, new[] { "financeiro.ver", "financeiro.receber" }), default);

        var (http, _) = await ChamarAsync(usuario.Id, "/api/vendas", "financeiro.ver");
        Assert.Contains("financeiro.receber", http.User.Permissoes());
    }

    [Fact]
    public async Task Rotas_de_entrar_nao_passam_pela_conferencia()
    {
        await Time(_dono.Id, "*").Atualizar(
            _outroAdmin.Id, new AtualizarMembroRequest(null, null, false, null), default);

        // O refresh e o login respondem por conta própria (e recusam o desativado lá).
        var (_, seguiu) = await ChamarAsync(_outroAdmin.Id, "/api/auth/refresh", "*");
        Assert.True(seguiu);
    }

    [Fact]
    public async Task Ultimo_administrador_ativo_nao_e_desativado()
    {
        _outroAdmin.Ativo = false;
        var terceiro = new Usuario { TenantId = 1, Nome = "Terceiro", Email = "t@x.com", PerfilId = _admin.Id };
        _db.Usuarios.Add(terceiro);
        await _db.SaveChangesAsync();

        // Sobram dois administradores ativos (dono e terceiro): desativar um pode.
        await Time(_dono.Id, "*").Atualizar(
            terceiro.Id, new AtualizarMembroRequest(null, null, false, null), default);

        // Agora o terceiro, com um token ainda válido de antes, tentaria tirar o dono.
        _contexto.UsuarioId = terceiro.Id;
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() => Time(terceiro.Id, "*").Atualizar(
            _dono.Id, new AtualizarMembroRequest(null, null, false, null), default));
        Assert.Equal("ULTIMO_ADMIN", erro.Codigo);
    }
}

/// <summary>
/// A rotação do refresh token lia, conferia e revogava sem trava: dois pedidos com o mesmo
/// token ao mesmo tempo (duas abas, um retry do celular) passavam os dois pela leitura e
/// saíam com dois pares válidos a partir de um token só.
/// </summary>
public class RotacaoDoRefreshTests
{
    [Fact]
    public async Task O_segundo_a_girar_o_mesmo_token_nao_grava_nada()
    {
        var banco = $"rotacao-{Guid.NewGuid()}";
        AppDbContext Novo() => new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(banco).Options,
            new ContextoAtual { TenantId = 1 });

        await using (var db = Novo())
        {
            var perfil = new Perfil { TenantId = 1, Nome = "Administrador", Administrador = true };
            db.Perfis.Add(perfil);
            await db.SaveChangesAsync();
            var usuario = new Usuario { TenantId = 1, Nome = "Ana", Email = "ana@x.com", PerfilId = perfil.Id };
            db.Usuarios.Add(usuario);
            await db.SaveChangesAsync();
            db.RefreshTokens.Add(new RefreshToken
            {
                TenantId = 1, UsuarioId = usuario.Id, TokenHash = "h", ProdutoOrigem = "x",
                ExpiraEm = DateTimeOffset.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
        }

        // As duas requisições leem o token ainda ativo...
        await using var primeira = Novo();
        await using var segunda = Novo();
        var daPrimeira = await primeira.RefreshTokens.FirstAsync(t => t.TokenHash == "h");
        var daSegunda = await segunda.RefreshTokens.FirstAsync(t => t.TokenHash == "h");
        Assert.True(daPrimeira.Ativo && daSegunda.Ativo);

        // ...a primeira gira...
        daPrimeira.RevogadoEm = DateTimeOffset.UtcNow;
        await primeira.SaveChangesAsync();

        // ...e a segunda não consegue gravar a revogação dela (nem o par novo que viria junto).
        daSegunda.RevogadoEm = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => segunda.SaveChangesAsync());
    }
}

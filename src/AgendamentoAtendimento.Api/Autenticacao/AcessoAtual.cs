using System.Collections.Concurrent;
using System.Security.Claims;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Controllers;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Autenticacao;

/// <summary>O acesso de alguém como está agora no banco: se ainda entra, e com o quê.</summary>
public sealed record AcessoDoUsuario(bool Ativo, IReadOnlyList<string> Permissoes)
{
    public static readonly AcessoDoUsuario Revogado = new(false, Array.Empty<string>());
}

/// <summary>
/// Cache curto, por usuário, do <see cref="AcessoDoUsuario"/>. A conferência roda em toda
/// requisição autenticada; sem cache seria uma consulta a mais em cada uma.
///
/// Quem muda o acesso pela Api (perfil, ativo, remoção, permissões do perfil) chama
/// <see cref="Invalidar"/> ou <see cref="InvalidarEmpresa"/>, e a mudança vale na hora. A
/// validade curta cobre o que muda por fora deste processo — outra instância da Api, ou
/// alguém mexendo direto no banco. É o mesmo desenho do cache da assinatura.
/// </summary>
public sealed class CacheDeAcesso
{
    public static readonly TimeSpan Validade = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<(long Tenant, long Usuario), (AcessoDoUsuario Acesso, DateTimeOffset Expira)> _porUsuario = new();
    private readonly TimeProvider _relogio;

    public CacheDeAcesso(TimeProvider? relogio = null) => _relogio = relogio ?? TimeProvider.System;

    public bool TentarObter(long tenantId, long usuarioId, out AcessoDoUsuario acesso)
    {
        if (_porUsuario.TryGetValue((tenantId, usuarioId), out var guardado)
            && guardado.Expira > _relogio.GetUtcNow())
        {
            acesso = guardado.Acesso;
            return true;
        }

        acesso = AcessoDoUsuario.Revogado;
        return false;
    }

    public void Guardar(long tenantId, long usuarioId, AcessoDoUsuario acesso) =>
        _porUsuario[(tenantId, usuarioId)] = (acesso, _relogio.GetUtcNow().Add(Validade));

    public void Invalidar(long tenantId, long usuarioId) => _porUsuario.TryRemove((tenantId, usuarioId), out _);

    /// <summary>As permissões de um perfil mudaram: vale para todos que o usam.</summary>
    public void InvalidarEmpresa(long tenantId)
    {
        foreach (var chave in _porUsuario.Keys.Where(k => k.Tenant == tenantId))
        {
            _porUsuario.TryRemove(chave, out _);
        }
    }
}

/// <summary>
/// Confere, em toda requisição autenticada, o acesso de quem chama como ele está AGORA — e
/// não como estava quando o token foi emitido.
///
/// As permissões viajam no access token, que vale 60 minutos. Sem esta conferência, um
/// administrador rebaixado continuava administrador até o token vencer — e, com a permissão
/// coringa ainda no token, devolvia a si mesmo o perfil de administrador; e quem era
/// desativado ou removido seguia lendo e gravando pela mesma hora.
///
/// Usuário desativado ou removido responde 401 `ACESSO_REVOGADO` (a renovação também é
/// recusada, e o app volta ao login). Usuário ativo segue com as permissões de agora no
/// lugar das do token: é com elas que as policies e os controllers decidem.
/// </summary>
/// <remarks>
/// Fica entre o <see cref="ContextoMiddleware"/> (que lê empresa e usuário do token) e a
/// autorização. As rotas de entrar e a página pública não passam por aqui: quem entra
/// ainda não tem acesso para conferir, e a página pública não depende de conta.
/// </remarks>
public class AcessoAtualMiddleware
{
    private static readonly string[] RotasLiberadas = { "/api/auth", "/api/publico" };

    private readonly RequestDelegate _proximo;

    public AcessoAtualMiddleware(RequestDelegate proximo) => _proximo = proximo;

    public async Task InvokeAsync(
        HttpContext http, ContextoAtual contexto, AppDbContext db, CacheDeAcesso cache)
    {
        if (contexto.TenantId is not { } tenantId
            || contexto.UsuarioId is not { } usuarioId
            || RotasLiberadas.Any(r => http.Request.Path.StartsWithSegments(r, StringComparison.OrdinalIgnoreCase)))
        {
            await _proximo(http);
            return;
        }

        if (!cache.TentarObter(tenantId, usuarioId, out var acesso))
        {
            acesso = await CarregarAsync(db, usuarioId, http.RequestAborted);
            cache.Guardar(tenantId, usuarioId, acesso);
        }

        if (!acesso.Ativo)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await http.Response.WriteAsJsonAsync(
                new ErroApi(
                    "Seu acesso a esta empresa foi desativado ou removido. Fale com o administrador.",
                    "ACESSO_REVOGADO"),
                OpcoesJson.Padrao, http.RequestAborted);
            return;
        }

        http.User = ComPermissoes(http.User, acesso.Permissoes);
        await _proximo(http);
    }

    /// <summary>O filtro de empresa e de exclusão já vale aqui: removido não é achado.</summary>
    public static async Task<AcessoDoUsuario> CarregarAsync(
        AppDbContext db, long usuarioId, CancellationToken ct)
    {
        var usuario = await db.Usuarios.AsNoTracking()
            .Include(u => u.Perfil!).ThenInclude(p => p.Permissoes)
            .FirstOrDefaultAsync(u => u.Id == usuarioId, ct);

        return usuario is { Ativo: true }
            ? new AcessoDoUsuario(true, AuthController.PermissoesDe(usuario))
            : AcessoDoUsuario.Revogado;
    }

    /// <summary>O mesmo usuário, com as permissões trocadas pelas de agora.</summary>
    public static ClaimsPrincipal ComPermissoes(ClaimsPrincipal usuario, IReadOnlyList<string> permissoes)
    {
        var doToken = usuario.FindAll(ClaimsApp.Permissao).Select(c => c.Value);
        if (doToken.ToHashSet(StringComparer.Ordinal).SetEquals(permissoes))
        {
            return usuario;
        }

        var original = usuario.Identity as ClaimsIdentity;
        var identidade = new ClaimsIdentity(
            usuario.Claims.Where(c => c.Type != ClaimsApp.Permissao)
                .Select(c => new Claim(c.Type, c.Value, c.ValueType, c.Issuer, c.OriginalIssuer))
                .Concat(permissoes.Select(p => new Claim(ClaimsApp.Permissao, p))),
            original?.AuthenticationType,
            original?.NameClaimType ?? ClaimsIdentity.DefaultNameClaimType,
            original?.RoleClaimType ?? ClaimsIdentity.DefaultRoleClaimType);

        return new ClaimsPrincipal(identidade);
    }
}

using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.MultiTenancy;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Seguranca;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>
/// Autenticação da suíte. O refresh token vale para todos os produtos: é ele que o app do
/// PetShop.Route entrega para este app entrar sem pedir a senha de novo.
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ContextoAtual _contexto;
    private readonly ServicoDeToken _token;
    private readonly OpcoesJwt _opcoes;

    public AuthController(
        AppDbContext db, ContextoAtual contexto, ServicoDeToken token, IOptions<OpcoesJwt> opcoes)
    {
        _db = db;
        _contexto = contexto;
        _token = token;
        _opcoes = opcoes.Value;
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Login) || string.IsNullOrWhiteSpace(req.Senha))
        {
            return Unauthorized(new ErroApi("Credenciais inválidas.", "CREDENCIAIS"));
        }

        var produto = ProdutoDaRequisicao(req.Produto);

        // O e-mail é gravado em minúsculas (é assim que o convite o normaliza): comparar
        // com o que foi digitado, do jeito que veio, recusava "Ana@Empresa.com".
        var email = req.Login.Trim().ToLowerInvariant();

        _contexto.IgnorarFiltroDeTenant = true;
        var consulta = _db.Usuarios
            .Include(u => u.Perfil!).ThenInclude(p => p.Permissoes)
            .Where(u => u.Email.ToLower() == email && u.Ativo && !u.Excluido);

        if (!string.IsNullOrWhiteSpace(req.TenantSlug))
        {
            var slug = req.TenantSlug.Trim();
            var tenantDoSlug = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);
            if (tenantDoSlug is null)
            {
                return Unauthorized(new ErroApi("Credenciais inválidas.", "CREDENCIAIS"));
            }
            consulta = consulta.Where(u => u.TenantId == tenantDoSlug.Id);
        }

        var usuarios = await consulta.ToListAsync(ct);
        // Mensagem única para não revelar se o e-mail existe.
        var usuario = usuarios.FirstOrDefault(u => HashSenha.Confere(req.Senha, u.SenhaHash));
        if (usuario is null)
        {
            return Unauthorized(new ErroApi("Credenciais inválidas.", "CREDENCIAIS"));
        }

        var tenant = await _db.Tenants.FirstAsync(t => t.Id == usuario.TenantId, ct);
        _contexto.AssumirTenant(tenant.Id, tenant.Slug);
        _contexto.UsuarioId = usuario.Id;
        _contexto.IgnorarFiltroDeTenant = false;

        usuario.UltimoLoginEm = DateTimeOffset.UtcNow;
        usuario.UltimoLoginIp = _contexto.Ip;

        var refresh = await EmitirRefreshAsync(usuario, produto, ct);
        return Ok(MontarResposta(usuario, tenant, produto, refresh));
    }

    /// <summary>
    /// Troca um refresh token por um access token com o escopo do produto que está pedindo.
    /// É o caminho do "continuar com a sessão do PetShop.Route".
    /// </summary>
    [HttpPost("refresh")]
    public async Task<ActionResult<LoginResponse>> Refresh(RefreshRequest req, CancellationToken ct)
    {
        var produto = ProdutoDaRequisicao(req.Produto);
        var hash = HashSenha.HashDeToken(req.RefreshToken ?? string.Empty);

        _contexto.IgnorarFiltroDeTenant = true;
        var token = await _db.RefreshTokens
            .Include(t => t.Usuario!).ThenInclude(u => u.Perfil!).ThenInclude(p => p.Permissoes)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token?.Usuario is null || !token.Ativo || !token.Usuario.Ativo)
        {
            return Unauthorized(new ErroApi("Sessão expirada. Entre novamente.", "REFRESH_INVALIDO"));
        }

        var tenant = await _db.Tenants.FirstAsync(t => t.Id == token.TenantId, ct);
        _contexto.AssumirTenant(tenant.Id, tenant.Slug);
        _contexto.UsuarioId = token.UsuarioId;
        _contexto.IgnorarFiltroDeTenant = false;

        // Rotação: o token usado é revogado e um novo é emitido.
        token.RevogadoEm = DateTimeOffset.UtcNow;
        var novo = await EmitirRefreshAsync(token.Usuario, produto, ct);

        return Ok(MontarResposta(token.Usuario, tenant, produto, novo));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var usuarioId = User.ObterLong(ClaimsApp.UsuarioId);
        if (usuarioId is not null)
        {
            await _db.RefreshTokens
                .Where(t => t.UsuarioId == usuarioId && t.RevogadoEm == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevogadoEm, DateTimeOffset.UtcNow), ct);
        }
        return NoContent();
    }

    private static string ProdutoDaRequisicao(string? produto) =>
        Produtos.Conhecido(produto) ? produto!.ToLowerInvariant() : Produtos.Agendamento;

    private async Task<string> EmitirRefreshAsync(Usuario usuario, string produto, CancellationToken ct)
    {
        var valor = HashSenha.NovoTokenAleatorio();
        _db.RefreshTokens.Add(new RefreshToken
        {
            TenantId = usuario.TenantId,
            UsuarioId = usuario.Id,
            TokenHash = HashSenha.HashDeToken(valor),
            ExpiraEm = DateTimeOffset.UtcNow.AddDays(_opcoes.DiasDoRefreshToken),
            ProdutoOrigem = produto,
            CriadoPorIp = _contexto.Ip,
        });
        await _db.SaveChangesAsync(ct);
        return valor;
    }

    private LoginResponse MontarResposta(Usuario usuario, Tenant tenant, string produto, string refresh)
    {
        var permissoes = PermissoesDe(usuario);
        var emitido = _token.Emitir(usuario, tenant, permissoes, produto);

        return new LoginResponse(
            emitido.AccessToken,
            refresh,
            emitido.ExpiraEmSegundos,
            new UsuarioDto(
                usuario.Id, usuario.Nome, usuario.Email, usuario.Perfil?.Nome, usuario.PerfilId,
                permissoes, Permissoes.TelasVisiveis(permissoes), usuario.FotoUrl,
                usuario.Perfil?.Administrador ?? false, usuario.Atendente),
            new TenantDto(
                tenant.Id, tenant.Slug, tenant.NomeEmpresa, tenant.Moeda,
                tenant.FusoHorario, tenant.IdiomaPadrao));
    }

    internal static IReadOnlyList<string> PermissoesDe(Usuario usuario) =>
        usuario.Perfil?.Administrador == true
            ? new[] { Permissoes.Coringa }
            : usuario.Perfil?.Permissoes.Select(p => p.Permissao).Distinct().ToArray()
              ?? Array.Empty<string>();
}

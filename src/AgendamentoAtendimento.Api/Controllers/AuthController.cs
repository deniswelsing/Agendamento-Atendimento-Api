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
        var conferem = usuarios.Where(u => HashSenha.Confere(req.Senha, u.SenhaHash)).ToList();
        if (conferem.Count == 0)
        {
            return Unauthorized(new ErroApi("Credenciais inválidas.", "CREDENCIAIS"));
        }

        // O mesmo e-mail e a mesma senha em duas empresas: sem a empresa, entrar na
        // primeira que o banco devolvesse punha a pessoa no lugar errado sem avisar.
        if (conferem.Select(u => u.TenantId).Distinct().Count() > 1)
        {
            return BadRequest(new ErroApi(
                "Este acesso existe em mais de uma empresa. Informe a empresa para entrar.",
                "EMPRESA_OBRIGATORIA"));
        }

        var usuario = conferem[0];

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

    /// <summary>
    /// Abre uma sessão própria para o app irmão a partir do refresh token que ele recebeu
    /// pelo compartilhamento de sessão (ContentProvider do Android). O token apresentado é
    /// só CONFERIDO — não é revogado nem rotacionado —, e sai um par novo e independente
    /// para o produto que pediu.
    ///
    /// Existe porque o refresh roda o token a cada uso: com os dois apps dividindo o mesmo,
    /// quem renovava primeiro revogava o do outro e o derrubava da sessão. Cada app chama
    /// isto uma vez, fica com o seu par, e dali em diante renova só o seu.
    /// </summary>
    [HttpPost("sessao-irma")]
    public async Task<ActionResult<LoginResponse>> SessaoIrma(SessaoIrmaRequest req, CancellationToken ct)
    {
        var produto = ProdutoDaRequisicao(
            req.Produto ?? Request.Headers[ContextoMiddleware.CabecalhoProduto].FirstOrDefault());
        var hash = HashSenha.HashDeToken(req.RefreshToken ?? string.Empty);

        _contexto.IgnorarFiltroDeTenant = true;
        var token = await _db.RefreshTokens.AsNoTracking()
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

        // Nenhuma escrita no token apresentado: ele continua valendo para o app que o tem.
        var novo = await EmitirRefreshAsync(token.Usuario, produto, ct);
        return Ok(MontarResposta(token.Usuario, tenant, produto, novo));
    }

    /// <summary>
    /// O convite por trás do link: quem convidou, para qual empresa, até quando vale. É o
    /// que a tela de aceite mostra antes de pedir a senha. Token desconhecido, vencido ou
    /// já usado respondem igual: 404.
    /// </summary>
    [HttpGet("convite/{token}")]
    public async Task<ActionResult<ConviteDto>> Convite(string token, CancellationToken ct)
    {
        var convidado = await ConvitePendenteAsync(token, ct);
        if (convidado is null)
        {
            return ConviteInvalido();
        }

        var empresa = await _db.Tenants.AsNoTracking()
            .Where(t => t.Id == convidado.TenantId).Select(t => t.NomeEmpresa).FirstAsync(ct);

        return Ok(new ConviteDto(convidado.Nome, convidado.Email, empresa, convidado.ConviteExpiraEm));
    }

    /// <summary>
    /// Aceita o convite: grava a senha, gasta o token (uso único) e já entra — a resposta é
    /// a mesma do login.
    /// </summary>
    [HttpPost("convite/aceitar")]
    public async Task<ActionResult<LoginResponse>> AceitarConvite(AceitarConviteRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Senha) || req.Senha.Length < TamanhoMinimoDaSenha)
        {
            return BadRequest(new ErroApi(
                $"A senha precisa ter ao menos {TamanhoMinimoDaSenha} caracteres.", "SENHA_FRACA"));
        }

        var usuario = await ConvitePendenteAsync(req.Token, ct);
        if (usuario is null)
        {
            return ConviteInvalido();
        }

        var produto = ProdutoDaRequisicao(
            req.Produto ?? Request.Headers[ContextoMiddleware.CabecalhoProduto].FirstOrDefault());

        var tenant = await _db.Tenants.FirstAsync(t => t.Id == usuario.TenantId, ct);
        _contexto.AssumirTenant(tenant.Id, tenant.Slug);
        _contexto.UsuarioId = usuario.Id;

        usuario.SenhaHash = HashSenha.Gerar(req.Senha);
        usuario.TokenConvite = null;
        usuario.ConviteExpiraEm = null;
        usuario.ConvitePendente = false;
        usuario.Ativo = true;
        usuario.UltimoLoginEm = DateTimeOffset.UtcNow;
        usuario.UltimoLoginIp = _contexto.Ip;

        // Grava a senha e o refresh juntos: EmitirRefreshAsync salva tudo que está pendente.
        var refresh = await EmitirRefreshAsync(usuario, produto, ct);
        return Ok(MontarResposta(usuario, tenant, produto, refresh));
    }

    /// <summary>Tamanho mínimo da senha definida no aceite do convite.</summary>
    public const int TamanhoMinimoDaSenha = 8;

    /// <summary>
    /// O usuário dono de um convite ainda válido. O banco guarda o SHA-256 do token, e a
    /// busca é pelo hash — igualdade exata, sem comparar o segredo em si. Convites gerados
    /// antes do hash (token em claro na coluna) ainda são achados pelo valor exato, com
    /// diferença de maiúsculas e minúsculas.
    /// </summary>
    private async Task<Usuario?> ConvitePendenteAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
        {
            return null;
        }

        var hash = HashSenha.HashDeToken(token);
        var agora = DateTimeOffset.UtcNow;

        // O link não diz a empresa: a busca é em todas, e o filtro volta logo depois.
        _contexto.IgnorarFiltroDeTenant = true;
        try
        {
            return await _db.Usuarios
                .Include(u => u.Perfil!).ThenInclude(p => p.Permissoes)
                .Where(u => u.ConvitePendente && u.Ativo && u.ConviteExpiraEm > agora)
                .Where(u => u.TokenConvite == hash || u.TokenConvite == token)
                .FirstOrDefaultAsync(ct);
        }
        finally
        {
            _contexto.IgnorarFiltroDeTenant = false;
        }
    }

    private NotFoundObjectResult ConviteInvalido() =>
        NotFound(new ErroApi(
            "Convite inválido, vencido ou já usado. Peça um novo a quem convidou você.",
            "CONVITE_INVALIDO"));

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

using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Seguranca;
using AgendamentoAtendimento.Infrastructure.Servicos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>
/// Time do tenant. Cada usuário ativo ocupa um assento da assinatura — é aqui que o
/// limite de assentos vira o 402 que o app usa para oferecer a compra.
/// </summary>
[Route("api/time")]
public class TimeController : ControllerBaseApi
{
    /// <summary>Quanto tempo o link de convite vale.</summary>
    public static readonly TimeSpan ValidadeDoConvite = TimeSpan.FromDays(7);

    private readonly AppDbContext _db;
    private readonly AssinaturaService _assinaturas;
    private readonly IConfiguration? _config;

    public TimeController(AppDbContext db, AssinaturaService assinaturas, IConfiguration? config = null)
    {
        _db = db;
        _assinaturas = assinaturas;
        _config = config;
    }

    [HttpGet("membros")]
    [RequerPermissao("time.ver")]
    public async Task<ActionResult<IReadOnlyList<MembroTimeDto>>> Listar(
        [FromQuery] bool incluirInativos = false, CancellationToken ct = default)
    {
        var membros = await _db.Usuarios
            .AsNoTracking()
            .Include(u => u.Perfil)
            .Where(u => incluirInativos || u.Ativo)
            .OrderBy(u => u.Nome)
            .ToListAsync(ct);

        return Ok(membros.Select(m => m.ParaDto()).ToList());
    }

    [HttpGet("membros/{id:long}")]
    [RequerPermissao("time.ver")]
    public async Task<ActionResult<MembroTimeDto>> Obter(long id, CancellationToken ct)
    {
        var membro = NaoNulo(
            await _db.Usuarios.AsNoTracking().Include(u => u.Perfil)
                .FirstOrDefaultAsync(u => u.Id == id, ct),
            "Usuário não encontrado.");

        return Ok(membro.ParaDto());
    }

    /// <summary>
    /// Convida um usuário. Responde 402 quando o time já ocupa todos os assentos
    /// contratados: o app abre a tela de assinatura a partir desse status.
    /// </summary>
    [HttpPost("membros")]
    [RequerPermissao("time.convidar")]
    [RequerRecurso(CatalogoRecursos.MultiUsuario)]
    public async Task<ActionResult<MembroTimeDto>> Convidar(ConvidarMembroRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Nome))
        {
            throw new RegraDeNegocioException("Informe nome e e-mail.", "DADOS_OBRIGATORIOS");
        }

        var email = req.Email.Trim().ToLowerInvariant();
        if (await _db.Usuarios.AnyAsync(u => u.Email == email, ct))
        {
            throw new RegraDeNegocioException($"{email} já faz parte do time.", "EMAIL_DUPLICADO");
        }

        var perfil = NaoNulo(
            await _db.Perfis.FirstOrDefaultAsync(p => p.Id == req.PerfilId, ct),
            "Perfil não encontrado.");
        ExigirAdministradorPara(perfil.Administrador);

        var podeAdicionar = await _assinaturas.PodeAdicionarUsuarioAsync(ct);
        if (!podeAdicionar.Ok)
        {
            throw new AssinaturaExigidaException(
                podeAdicionar.Mensagem ?? "Sem assento disponível.", podeAdicionar.Motivo);
        }

        var usuario = new Usuario
        {
            Nome = req.Nome.Trim(),
            Email = email,
            PerfilId = perfil.Id,
            Atendente = req.Atendente,
            ConvitePendente = true,
        };
        var token = NovoConvite(usuario);

        _db.Usuarios.Add(usuario);
        await _db.SaveChangesAsync(ct);

        usuario.Perfil = perfil;
        return CreatedAtAction(nameof(Obter), new { id = usuario.Id }, ComLink(usuario, token));
    }

    /// <summary>
    /// Gera um convite novo para quem ainda não aceitou: o link anterior deixa de valer e a
    /// validade recomeça. É o "reenviar convite" — e o jeito de recuperar o link, que não
    /// fica guardado em claro.
    /// </summary>
    [HttpPost("membros/{id:long}/convite")]
    [RequerPermissao("time.convidar")]
    public async Task<ActionResult<MembroTimeDto>> RenovarConvite(long id, CancellationToken ct)
    {
        var usuario = NaoNulo(
            await _db.Usuarios.Include(u => u.Perfil).FirstOrDefaultAsync(u => u.Id == id, ct),
            "Usuário não encontrado.");

        // O link entra na conta sem senha nenhuma: gerar o de um administrador é o mesmo
        // que conceder o perfil, e isso só um administrador faz.
        ExigirAdministradorPara(usuario.Perfil?.Administrador == true);

        if (!usuario.ConvitePendente || !usuario.Ativo)
        {
            throw new RegraDeNegocioException(
                "Só um convite pendente, de alguém ativo no time, pode ser renovado.",
                "CONVITE_NAO_PENDENTE");
        }

        var token = NovoConvite(usuario);
        await _db.SaveChangesAsync(ct);
        return Ok(ComLink(usuario, token));
    }

    /// <summary>
    /// Gera o token do convite. No banco fica só o SHA-256 dele (como o refresh token):
    /// quem lê a tabela não aceita o convite de ninguém. O valor em claro existe apenas
    /// no link devolvido a quem convidou.
    /// </summary>
    private static string NovoConvite(Usuario usuario)
    {
        var token = HashSenha.NovoTokenAleatorio();
        usuario.TokenConvite = HashSenha.HashDeToken(token);
        usuario.ConviteExpiraEm = DateTimeOffset.UtcNow.Add(ValidadeDoConvite);
        return token;
    }

    private MembroTimeDto ComLink(Usuario usuario, string token) =>
        usuario.ParaDto() with
        {
            UrlConvite = $"{BaseWeb()}/convite/{Uri.EscapeDataString(token)}",
            ConviteExpiraEm = usuario.ConviteExpiraEm,
        };

    /// <summary>
    /// Onde o painel web está publicado: `Web:BaseUrl`, senão o da página pública (é o
    /// mesmo painel), senão o endereço desta requisição.
    /// </summary>
    private string BaseWeb()
    {
        var baseUrl = _config?["Web:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = _config?["PaginaPublica:BaseUrl"];
        }
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = $"{Request.Scheme}://{Request.Host}";
        }
        return baseUrl.TrimEnd('/');
    }

    /// <summary>Atribui perfil, renomeia ou ativa/desativa um usuário.</summary>
    [HttpPut("membros/{id:long}")]
    [RequerPermissao("time.editar")]
    public async Task<ActionResult<MembroTimeDto>> Atualizar(
        long id, AtualizarMembroRequest req, CancellationToken ct)
    {
        var usuario = NaoNulo(
            await _db.Usuarios.Include(u => u.Perfil).FirstOrDefaultAsync(u => u.Id == id, ct),
            "Usuário não encontrado.");
        ExigirAdministradorPara(usuario.Perfil?.Administrador == true);

        if (!string.IsNullOrWhiteSpace(req.Nome))
        {
            usuario.Nome = req.Nome.Trim();
        }

        if (req.PerfilId is { } perfilId && perfilId != usuario.PerfilId)
        {
            var perfil = NaoNulo(
                await _db.Perfis.FirstOrDefaultAsync(p => p.Id == perfilId, ct),
                "Perfil não encontrado.");

            ExigirAdministradorPara(perfil.Administrador);
            await GarantirQueSobraAdminAsync(usuario, perfil, ct);
            usuario.PerfilId = perfil.Id;
            usuario.Perfil = perfil;
        }

        if (req.Atendente is { } atendente)
        {
            usuario.Atendente = atendente;
        }

        if (req.IsAtivo is { } ativo && ativo != usuario.Ativo)
        {
            if (ativo)
            {
                // Reativar consome assento de novo.
                var pode = await _assinaturas.PodeAdicionarUsuarioAsync(ct);
                if (!pode.Ok)
                {
                    throw new AssinaturaExigidaException(
                        pode.Mensagem ?? "Sem assento disponível.", pode.Motivo);
                }
            }
            else if (usuario.Id == UsuarioId)
            {
                throw new RegraDeNegocioException(
                    "Você não pode desativar o próprio acesso.", "AUTO_DESATIVACAO");
            }

            usuario.Ativo = ativo;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(usuario.ParaDto());
    }

    [HttpDelete("membros/{id:long}")]
    [RequerPermissao("time.remover")]
    public async Task<IActionResult> Remover(long id, CancellationToken ct)
    {
        var usuario = NaoNulo(
            await _db.Usuarios.Include(u => u.Perfil).FirstOrDefaultAsync(u => u.Id == id, ct),
            "Usuário não encontrado.");

        if (usuario.Id == UsuarioId)
        {
            throw new RegraDeNegocioException(
                "Você não pode remover o próprio acesso.", "AUTO_REMOCAO");
        }

        ExigirAdministradorPara(usuario.Perfil?.Administrador == true);

        await GarantirQueSobraAdminAsync(usuario, null, ct);

        // Exclusão lógica: a trilha de auditoria e os agendamentos antigos continuam válidos.
        _db.Usuarios.Remove(usuario);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Só um administrador dá o perfil de administrador, ou mexe em quem já o tem.
    /// Sem isto, quem tem apenas `time.editar` se promovia a administrador (a permissão
    /// coringa) trocando o próprio perfil — ou rebaixava e desativava os administradores.
    /// </summary>
    private void ExigirAdministradorPara(bool envolveAdministrador)
    {
        if (envolveAdministrador && !Permissoes.Permite(PermissoesDoUsuario, Permissoes.Coringa))
        {
            throw new RegraDeNegocioException(
                "Só um administrador pode conceder o perfil de administrador ou alterar um administrador.",
                "SO_ADMINISTRADOR");
        }
    }

    /// <summary>Um tenant nunca pode ficar sem administrador ativo.</summary>
    private async Task GarantirQueSobraAdminAsync(Usuario usuario, Perfil? novoPerfil, CancellationToken ct)
    {
        var eraAdmin = usuario.Perfil?.Administrador == true;
        var continuaAdmin = novoPerfil?.Administrador == true;
        if (!eraAdmin || continuaAdmin)
        {
            return;
        }

        var outrosAdmins = await _db.Usuarios
            .CountAsync(u => u.Id != usuario.Id && u.Ativo && u.Perfil!.Administrador, ct);

        if (outrosAdmins == 0)
        {
            throw new RegraDeNegocioException(
                "Este é o último administrador da empresa.", "ULTIMO_ADMIN");
        }
    }
}

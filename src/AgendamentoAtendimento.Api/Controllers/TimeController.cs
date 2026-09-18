using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
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
    private readonly AppDbContext _db;
    private readonly AssinaturaService _assinaturas;

    public TimeController(AppDbContext db, AssinaturaService assinaturas)
    {
        _db = db;
        _assinaturas = assinaturas;
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
            TokenConvite = HashSenha.NovoTokenAleatorio(),
            ConviteExpiraEm = DateTimeOffset.UtcNow.AddDays(7),
        };

        _db.Usuarios.Add(usuario);
        await _db.SaveChangesAsync(ct);

        usuario.Perfil = perfil;
        return CreatedAtAction(nameof(Obter), new { id = usuario.Id }, usuario.ParaDto());
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

        if (!string.IsNullOrWhiteSpace(req.Nome))
        {
            usuario.Nome = req.Nome.Trim();
        }

        if (req.PerfilId is { } perfilId && perfilId != usuario.PerfilId)
        {
            var perfil = NaoNulo(
                await _db.Perfis.FirstOrDefaultAsync(p => p.Id == perfilId, ct),
                "Perfil não encontrado.");

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

        await GarantirQueSobraAdminAsync(usuario, null, ct);

        // Exclusão lógica: a trilha de auditoria e os agendamentos antigos continuam válidos.
        _db.Usuarios.Remove(usuario);
        await _db.SaveChangesAsync(ct);
        return NoContent();
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

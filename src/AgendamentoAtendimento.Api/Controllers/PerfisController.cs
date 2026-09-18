using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>
/// Perfis de acesso. É aqui que o admin escolhe quais telas cada perfil enxerga e o que
/// pode fazer dentro delas.
/// </summary>
[Route("api/perfis")]
public class PerfisController : ControllerBaseApi
{
    private readonly AppDbContext _db;

    public PerfisController(AppDbContext db) => _db = db;

    /// <summary>
    /// Catálogo de telas e ações. O app monta a tela de permissões a partir desta resposta —
    /// não existe lista de permissões escrita no aplicativo.
    /// </summary>
    [HttpGet("catalogo")]
    [Authorize]
    public ActionResult<IReadOnlyList<ModuloPermissaoDto>> Catalogo() =>
        Ok(Permissoes.Modulos.Select(m => m.ParaDto()).ToList());

    [HttpGet]
    [RequerPermissao("perfis.ver")]
    public async Task<ActionResult<IReadOnlyList<PerfilDto>>> Listar(CancellationToken ct)
    {
        var perfis = await _db.Perfis
            .AsNoTracking()
            .Include(p => p.Permissoes)
            .Include(p => p.Usuarios)
            .OrderByDescending(p => p.Administrador)
            .ThenBy(p => p.Nome)
            .ToListAsync(ct);

        return Ok(perfis.Select(p => p.ParaDto()).ToList());
    }

    [HttpGet("{id:long}")]
    [RequerPermissao("perfis.ver")]
    public async Task<ActionResult<PerfilDto>> Obter(long id, CancellationToken ct)
    {
        var perfil = NaoNulo(
            await _db.Perfis.AsNoTracking()
                .Include(p => p.Permissoes).Include(p => p.Usuarios)
                .FirstOrDefaultAsync(p => p.Id == id, ct),
            "Perfil não encontrado.");

        return Ok(perfil.ParaDto());
    }

    [HttpPost]
    [RequerPermissao("perfis.criar")]
    [RequerRecurso(CatalogoRecursos.PermissoesAvancadas)]
    public async Task<ActionResult<PerfilDto>> Criar(PerfilRequest req, CancellationToken ct)
    {
        await GarantirNomeLivreAsync(req.Nome, null, ct);

        var perfil = new Perfil { Nome = req.Nome.Trim(), Descricao = req.Descricao };
        AplicarPermissoes(perfil, req.Permissoes);

        _db.Perfis.Add(perfil);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Obter), new { id = perfil.Id }, perfil.ParaDto());
    }

    [HttpPut("{id:long}")]
    [RequerPermissao("perfis.editar")]
    [RequerRecurso(CatalogoRecursos.PermissoesAvancadas)]
    public async Task<ActionResult<PerfilDto>> Atualizar(long id, PerfilRequest req, CancellationToken ct)
    {
        var perfil = NaoNulo(
            await _db.Perfis.Include(p => p.Permissoes).Include(p => p.Usuarios)
                .FirstOrDefaultAsync(p => p.Id == id, ct),
            "Perfil não encontrado.");

        await GarantirNomeLivreAsync(req.Nome, id, ct);

        perfil.Nome = req.Nome.Trim();
        perfil.Descricao = req.Descricao;

        // O perfil administrador carrega a permissão coringa e não é editável por lista.
        if (!perfil.Administrador)
        {
            AplicarPermissoes(perfil, req.Permissoes);
        }

        await _db.SaveChangesAsync(ct);
        return Ok(perfil.ParaDto());
    }

    [HttpDelete("{id:long}")]
    [RequerPermissao("perfis.excluir")]
    [RequerRecurso(CatalogoRecursos.PermissoesAvancadas)]
    public async Task<IActionResult> Remover(long id, CancellationToken ct)
    {
        var perfil = NaoNulo(
            await _db.Perfis.Include(p => p.Usuarios).FirstOrDefaultAsync(p => p.Id == id, ct),
            "Perfil não encontrado.");

        if (perfil.Administrador || perfil.DeSistema)
        {
            throw new RegraDeNegocioException(
                "Perfis de sistema não podem ser removidos.", "PERFIL_DE_SISTEMA");
        }

        if (perfil.Usuarios.Count > 0)
        {
            throw new RegraDeNegocioException(
                $"O perfil {perfil.Nome} ainda tem {perfil.Usuarios.Count} usuário(s). " +
                "Mova essas pessoas para outro perfil antes de remover.", "PERFIL_EM_USO");
        }

        _db.Perfis.Remove(perfil);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private void AplicarPermissoes(Perfil perfil, IReadOnlyList<string> permissoes)
    {
        // Sanitiza contra o catálogo e garante que a tela da ação também esteja liberada.
        var validas = Permissoes.ComTelasImplicadas(Permissoes.Sanitizar(permissoes))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Só mexe no que mudou: assim a trilha de auditoria mostra a diferença real,
        // e nenhuma linha é apagada e recriada com a mesma chave.
        foreach (var atual in perfil.Permissoes.Where(p => !validas.Contains(p.Permissao)).ToList())
        {
            perfil.Permissoes.Remove(atual);
            _db.PerfilPermissoes.Remove(atual);
        }

        var jaTem = perfil.Permissoes.Select(p => p.Permissao).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var permissao in validas.Where(p => !jaTem.Contains(p)))
        {
            perfil.Permissoes.Add(new PerfilPermissao { Permissao = permissao });
        }
    }

    private async Task GarantirNomeLivreAsync(string nome, long? ignorarId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(nome))
        {
            throw new RegraDeNegocioException("Informe o nome do perfil.", "NOME_OBRIGATORIO");
        }

        var alvo = nome.Trim();
        var existe = await _db.Perfis
            .AnyAsync(p => p.Nome == alvo && (ignorarId == null || p.Id != ignorarId), ct);

        if (existe)
        {
            throw new RegraDeNegocioException($"Já existe um perfil chamado {alvo}.", "NOME_DUPLICADO");
        }
    }
}

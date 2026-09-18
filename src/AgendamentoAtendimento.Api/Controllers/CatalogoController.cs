using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>Catálogo unificado de produtos e serviços.</summary>
[Route("api/catalogo")]
public class CatalogoController : ControllerBaseApi
{
    private readonly AppDbContext _db;

    public CatalogoController(AppDbContext db) => _db = db;

    [HttpGet("itens")]
    [RequerPermissao("catalogo.ver")]
    public async Task<ActionResult<PaginaDto<ItemCatalogoDto>>> Listar(
        [FromQuery] TipoItem? tipo,
        [FromQuery] string? busca,
        [FromQuery] bool somenteAtivos = true,
        [FromQuery] ParametrosDePagina? pagina = null,
        CancellationToken ct = default)
    {
        var p = pagina ?? new ParametrosDePagina { TamanhoPagina = 50 };
        var consulta = _db.ItensCatalogo.AsNoTracking().AsQueryable();

        if (tipo is { } t)
        {
            consulta = consulta.Where(i => i.Tipo == t);
        }

        if (somenteAtivos)
        {
            consulta = consulta.Where(i => i.Ativo);
        }

        if (!string.IsNullOrWhiteSpace(busca))
        {
            var termo = $"%{busca.Trim()}%";
            consulta = consulta.Where(i =>
                EF.Functions.ILike(i.Nome, termo) ||
                EF.Functions.ILike(i.Categoria ?? string.Empty, termo) ||
                EF.Functions.ILike(i.CodigoDeBarras ?? string.Empty, termo));
        }

        var total = await consulta.CountAsync(ct);
        var itens = await consulta
            .OrderBy(i => i.Tipo).ThenBy(i => i.Nome)
            .Skip(p.Pular).Take(p.TamanhoSeguro)
            .ToListAsync(ct);

        return Ok(new PaginaDto<ItemCatalogoDto>(
            itens.Select(i => i.ParaDto()).ToList(), p.PaginaSegura, p.TamanhoSeguro, total));
    }

    [HttpGet("itens/{id:long}")]
    [RequerPermissao("catalogo.ver")]
    public async Task<ActionResult<ItemCatalogoDto>> Obter(long id, CancellationToken ct)
    {
        var item = NaoNulo(
            await _db.ItensCatalogo.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct),
            "Item não encontrado.");

        return Ok(item.ParaDto());
    }

    [HttpPost("itens")]
    [RequerPermissao("catalogo.criar")]
    public async Task<ActionResult<ItemCatalogoDto>> Criar(ItemCatalogoRequest req, CancellationToken ct)
    {
        Validar(req);

        var item = new ItemCatalogo { Nome = req.Nome };
        item.Aplicar(req);

        _db.ItensCatalogo.Add(item);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Obter), new { id = item.Id }, item.ParaDto());
    }

    [HttpPut("itens/{id:long}")]
    [RequerPermissao("catalogo.editar")]
    public async Task<ActionResult<ItemCatalogoDto>> Atualizar(
        long id, ItemCatalogoRequest req, CancellationToken ct)
    {
        Validar(req);

        var item = NaoNulo(
            await _db.ItensCatalogo.FirstOrDefaultAsync(i => i.Id == id, ct),
            "Item não encontrado.");

        item.Aplicar(req);
        await _db.SaveChangesAsync(ct);

        return Ok(item.ParaDto());
    }

    [HttpDelete("itens/{id:long}")]
    [RequerPermissao("catalogo.excluir")]
    public async Task<IActionResult> Remover(long id, CancellationToken ct)
    {
        var item = NaoNulo(
            await _db.ItensCatalogo.FirstOrDefaultAsync(i => i.Id == id, ct),
            "Item não encontrado.");

        _db.ItensCatalogo.Remove(item);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static void Validar(ItemCatalogoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nome))
        {
            throw new RegraDeNegocioException("Informe o nome do item.", "NOME");
        }

        if (req.Preco < 0)
        {
            throw new RegraDeNegocioException("O preço não pode ser negativo.", "PRECO");
        }

        // Sem duração o serviço não entra no motor de disponibilidade.
        if (req.Tipo == TipoItem.Servico && (req.DuracaoMinutos is null or <= 0))
        {
            throw new RegraDeNegocioException(
                "Informe a duração do serviço em minutos.", "DURACAO");
        }
    }
}

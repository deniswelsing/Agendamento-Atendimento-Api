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
            // Com `%` e `_` escapados: buscar "50%" não pode virar curinga.
            var termo = BuscaTextual.PadraoContem(busca);
            const string escape = BuscaTextual.Escape;
            consulta = consulta.Where(i =>
                EF.Functions.ILike(i.Nome, termo, escape) ||
                EF.Functions.ILike(i.Categoria ?? string.Empty, termo, escape) ||
                EF.Functions.ILike(i.CodigoDeBarras ?? string.Empty, termo, escape));
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

    /// <summary>
    /// Quem pode prestar este serviço. Lista vazia quer dizer que qualquer atendente pode —
    /// é o padrão, e é o que mantém agendável o que existia antes desta regra.
    /// </summary>
    [HttpGet("itens/{id:long}/executores")]
    [RequerPermissao("catalogo.ver")]
    public async Task<ActionResult<ExecutoresDoServicoDto>> Executores(
        long id, CancellationToken ct)
    {
        var item = NaoNulo(
            await _db.ItensCatalogo.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct),
            "Item não encontrado.");

        var ids = await _db.ExecutoresDeServico.AsNoTracking()
            .Where(e => e.ItemCatalogoId == id).Select(e => e.UsuarioId).ToListAsync(ct);

        var pessoas = await _db.Usuarios.AsNoTracking().Include(u => u.Perfil)
            .Where(u => ids.Contains(u.Id)).OrderBy(u => u.Nome).ToListAsync(ct);

        return Ok(new ExecutoresDoServicoDto(
            item.Id, item.Nome, pessoas.Select(u => u.ParaDto()).ToList(), ids.Count == 0));
    }

    /// <summary>
    /// Quem presta cada um de vários serviços, numa pergunta só.
    ///
    /// A tela de novo agendamento precisa disto para oferecer, ao lado de cada serviço
    /// marcado, apenas quem sabe prestá-lo — e para marcar de uma vez tudo o que uma
    /// pessoa presta. Perguntar item a item seria uma requisição por linha do catálogo.
    ///
    /// Sem <c>itensIds</c> responde por todos os serviços ativos, que é o que essa tela
    /// carrega de qualquer forma. Id que não existe simplesmente não volta: a tela usa a
    /// resposta para montar opções, e um erro por causa de um item apagado enquanto ela
    /// estava aberta não ajudaria ninguém.
    /// </summary>
    [HttpGet("executores")]
    [RequerPermissao("catalogo.ver")]
    public async Task<ActionResult<IReadOnlyList<ExecutoresDoServicoDto>>> ExecutoresEmLote(
        [FromQuery] long[]? itensIds, CancellationToken ct)
    {
        var pedidos = (itensIds ?? Array.Empty<long>()).Distinct().ToList();

        var itens = await _db.ItensCatalogo.AsNoTracking()
            .Where(i => i.Tipo == TipoItem.Servico)
            .Where(i => pedidos.Count > 0 ? pedidos.Contains(i.Id) : i.Ativo)
            .OrderBy(i => i.Nome)
            .Select(i => new { i.Id, i.Nome })
            .ToListAsync(ct);

        var ids = itens.Select(i => i.Id).ToList();

        var vinculos = await _db.ExecutoresDeServico.AsNoTracking()
            .Where(e => ids.Contains(e.ItemCatalogoId))
            .Select(e => new { e.ItemCatalogoId, e.UsuarioId })
            .ToListAsync(ct);

        var usuariosIds = vinculos.Select(v => v.UsuarioId).Distinct().ToList();
        var pessoas = await _db.Usuarios.AsNoTracking().Include(u => u.Perfil)
            .Where(u => usuariosIds.Contains(u.Id)).OrderBy(u => u.Nome).ToListAsync(ct);

        var porItem = vinculos.GroupBy(v => v.ItemCatalogoId)
            .ToDictionary(g => g.Key, g => g.Select(v => v.UsuarioId).ToHashSet());
        var vazio = new HashSet<long>();

        return Ok(itens.Select(item =>
        {
            var doItem = porItem.GetValueOrDefault(item.Id, vazio);
            return new ExecutoresDoServicoDto(
                item.Id,
                item.Nome,
                pessoas.Where(u => doItem.Contains(u.Id)).Select(u => u.ParaDto()).ToList(),
                doItem.Count == 0);
        }).ToList());
    }

    /// <summary>
    /// Define quem presta o serviço. Mandar lista vazia reabre para todo o time.
    ///
    /// A gravação é por diferença: quem já estava e continua não é tocado, para não
    /// ressuscitar chave única de linha excluída logicamente.
    /// </summary>
    [HttpPut("itens/{id:long}/executores")]
    [RequerPermissao("catalogo.editar")]
    public async Task<ActionResult<ExecutoresDoServicoDto>> DefinirExecutores(
        long id, DefinirExecutoresRequest req, CancellationToken ct)
    {
        var item = NaoNulo(
            await _db.ItensCatalogo.FirstOrDefaultAsync(i => i.Id == id, ct),
            "Item não encontrado.");

        if (item.Tipo != TipoItem.Servico)
        {
            throw new RegraDeNegocioException(
                "Só serviço tem quem presta.", "SO_SERVICO");
        }

        var pedidos = (req.UsuariosIds ?? Array.Empty<long>()).Distinct().ToList();

        // Quem não atende não presta serviço: aceitar seria prometer encaixe que a agenda
        // nunca vai oferecer.
        var validos = await _db.Usuarios.AsNoTracking()
            .Where(u => pedidos.Contains(u.Id) && u.Ativo && u.Atendente)
            .Select(u => u.Id)
            .ToListAsync(ct);

        var invalidos = pedidos.Except(validos).ToList();
        if (invalidos.Count > 0)
        {
            throw new RegraDeNegocioException(
                "Só quem atende pode prestar serviço.", "NAO_ATENDENTE");
        }

        var atuais = await _db.ExecutoresDeServico
            .Where(e => e.ItemCatalogoId == id).ToListAsync(ct);

        foreach (var sobrando in atuais.Where(a => !validos.Contains(a.UsuarioId)))
        {
            _db.ExecutoresDeServico.Remove(sobrando);
        }

        foreach (var novoId in validos.Where(v => atuais.All(a => a.UsuarioId != v)))
        {
            _db.ExecutoresDeServico.Add(new ExecutorDeServico
            {
                ItemCatalogoId = id,
                UsuarioId = novoId,
            });
        }

        await _db.SaveChangesAsync(ct);
        return await Executores(id, ct);
    }
}

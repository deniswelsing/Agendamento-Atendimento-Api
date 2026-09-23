using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>
/// O lado de dentro da página pública: quem configura o endereço, escolhe o que aparece
/// e aprova o que o cliente pediu.
/// </summary>
[Route("api/pagina-online")]
public class PaginaOnlineController : ControllerBaseApi
{
    private readonly AppDbContext _db;
    private readonly PaginaPublicaService _paginas;
    private readonly IConfiguration _config;

    public PaginaOnlineController(
        AppDbContext db, PaginaPublicaService paginas, IConfiguration config)
    {
        _db = db;
        _paginas = paginas;
        _config = config;
    }

    [HttpGet]
    [RequerPermissao("pagina-online.ver")]
    public async Task<ActionResult<PaginaPublicaDto>> Obter(CancellationToken ct)
    {
        var pagina = await _db.PaginasPublicas.AsNoTracking().FirstOrDefaultAsync(ct);
        return Ok(await ParaDtoAsync(pagina, ct));
    }

    /// <summary>
    /// Grava a configuração. Cria a página na primeira vez — o tenant nasce sem ela, e
    /// ligar pela primeira vez é o que a traz à existência.
    /// </summary>
    [HttpPut]
    [RequerPermissao("pagina-online.editar")]
    [RequerRecurso(CatalogoRecursos.PaginaOnline)]
    public async Task<ActionResult<PaginaPublicaDto>> Salvar(
        PaginaPublicaRequest req, CancellationToken ct)
    {
        var slug = PaginaPublicaService.NormalizarSlug(req.Slug);
        if (slug.Length < 3)
        {
            throw new RegraDeNegocioException(
                "O endereço precisa de ao menos 3 letras ou números.", "SLUG_CURTO");
        }

        if (!await _paginas.SlugLivreAsync(slug, TenantId, ct))
        {
            throw new RegraDeNegocioException(
                $"O endereço \"{slug}\" já está em uso por outra empresa.", "SLUG_EM_USO");
        }

        if (req.AntecedenciaMinimaHoras < 0 || req.AntecedenciaMinimaHoras > 24 * 30)
        {
            throw new RegraDeNegocioException(
                "A antecedência mínima precisa ficar entre 0 e 720 horas.", "ANTECEDENCIA");
        }

        if (req.JanelaMaximaDias < 1 || req.JanelaMaximaDias > 365)
        {
            throw new RegraDeNegocioException(
                "A janela da agenda online precisa ficar entre 1 e 365 dias.", "JANELA");
        }

        var pagina = await _db.PaginasPublicas.FirstOrDefaultAsync(ct);
        if (pagina is null)
        {
            pagina = new ConfiguracaoPaginaPublica { Slug = slug };
            _db.PaginasPublicas.Add(pagina);
        }

        pagina.Ativa = req.Ativa;
        pagina.Slug = slug;
        pagina.TituloPublico = Limpar(req.TituloPublico);
        pagina.Mensagem = Limpar(req.Mensagem);
        pagina.Endereco = Limpar(req.Endereco);
        pagina.TelefoneContato = Limpar(req.TelefoneContato);
        pagina.AntecedenciaMinimaHoras = req.AntecedenciaMinimaHoras;
        pagina.JanelaMaximaDias = req.JanelaMaximaDias;
        pagina.ExigeAprovacao = req.ExigeAprovacao;
        pagina.PermiteEscolherProfissional = req.PermiteEscolherProfissional;
        pagina.ExigeTelefone = req.ExigeTelefone;
        pagina.LimiteDiarioPorCliente = Math.Max(0, req.LimiteDiarioPorCliente);

        await _db.SaveChangesAsync(ct);
        return Ok(await ParaDtoAsync(pagina, ct));
    }

    /// <summary>Confere o endereço antes de salvar, para a tela avisar enquanto se digita.</summary>
    [HttpGet("slug-disponivel")]
    [RequerPermissao("pagina-online.ver")]
    public async Task<ActionResult<SlugDisponivelDto>> SlugDisponivel(
        [FromQuery] string slug, CancellationToken ct)
    {
        var normalizado = PaginaPublicaService.NormalizarSlug(slug);

        if (normalizado.Length < 3)
        {
            return Ok(new SlugDisponivelDto(normalizado, false,
                "Use ao menos 3 letras ou números."));
        }

        var livre = await _paginas.SlugLivreAsync(normalizado, TenantId, ct);
        return Ok(new SlugDisponivelDto(normalizado, livre,
            livre ? null : "Este endereço já é de outra empresa."));
    }

    /// <summary>Pedidos do cliente esperando aprovação, do mais próximo para o mais distante.</summary>
    [HttpGet("pendentes")]
    [RequerPermissao("pagina-online.ver")]
    public async Task<ActionResult<IReadOnlyList<AgendamentoDto>>> Pendentes(CancellationToken ct)
    {
        var pendentes = await _db.Agendamentos.AsNoTracking()
            .Include(a => a.Cliente).Include(a => a.Responsavel).Include(a => a.Itens)
            .Where(a => a.Status == StatusAgendamento.PendenteAprovacao)
            .OrderBy(a => a.Inicio)
            .ToListAsync(ct);

        return Ok(pendentes.Select(a => a.ParaDto()).ToList());
    }

    /// <summary>
    /// Aprova o pedido: vira compromisso confirmado. O horário já estava preso desde o
    /// pedido, então aprovar não pode esbarrar em conflito nenhum.
    /// </summary>
    [HttpPost("pendentes/{id:long}/aprovar")]
    [RequerPermissao("pagina-online.aprovar")]
    public Task<ActionResult<AgendamentoDto>> Aprovar(long id, CancellationToken ct) =>
        ResolverAsync(id, StatusAgendamento.Confirmado, null, ct);

    /// <summary>Recusa o pedido e solta o horário para quem vier depois.</summary>
    [HttpPost("pendentes/{id:long}/recusar")]
    [RequerPermissao("pagina-online.aprovar")]
    public Task<ActionResult<AgendamentoDto>> Recusar(
        long id, [FromBody] AlterarStatusRequest? req, CancellationToken ct) =>
        ResolverAsync(id, StatusAgendamento.Cancelado,
            req?.Motivo ?? "Pedido recusado pela empresa.", ct);

    /// <summary>Liga ou desliga um serviço na página, sem passar pelo formulário do item.</summary>
    [HttpPut("servicos/{itemId:long}")]
    [RequerPermissao("pagina-online.editar")]
    public async Task<ActionResult<ItemCatalogoDto>> AlternarServico(
        long itemId, [FromQuery] bool visivel, CancellationToken ct)
    {
        var item = NaoNulo(
            await _db.ItensCatalogo.FirstOrDefaultAsync(i => i.Id == itemId, ct),
            "Item não encontrado.");

        if (item.Tipo != TipoItem.Servico)
        {
            throw new RegraDeNegocioException(
                "Só serviço vai para a página: ela agenda, não vende.", "SO_SERVICO");
        }

        item.VisivelOnline = visivel;
        await _db.SaveChangesAsync(ct);
        return Ok(item.ParaDto());
    }

    private async Task<ActionResult<AgendamentoDto>> ResolverAsync(
        long id, StatusAgendamento destino, string? motivo, CancellationToken ct)
    {
        // O pedido já segura o horário, mas resolver sob a trava da agenda impede que ele
        // seja aprovado no meio de uma remarcação ou de outra gravação do mesmo horário.
        await using var trava = await _db.TravarAgendaAsync(ct);

        var agendamento = NaoNulo(
            await _db.Agendamentos
                .Include(a => a.Cliente).Include(a => a.Responsavel).Include(a => a.Itens)
                .FirstOrDefaultAsync(a => a.Id == id, ct),
            "Agendamento não encontrado.");

        if (agendamento.Status != StatusAgendamento.PendenteAprovacao)
        {
            throw new RegraDeNegocioException(
                "Este agendamento não está esperando aprovação.", "NAO_PENDENTE");
        }

        agendamento.Status = destino;
        agendamento.MotivoCancelamento = destino == StatusAgendamento.Cancelado ? motivo : null;
        await _db.SaveChangesAsync(ct);
        await trava.ConfirmarAsync(ct);

        return Ok(agendamento.ParaDto());
    }

    private async Task<PaginaPublicaDto> ParaDtoAsync(
        ConfiguracaoPaginaPublica? pagina, CancellationToken ct)
    {
        // Sem página ainda, a tela precisa de um rascunho para desenhar: sugere o slug do
        // tenant, que é o que a empresa já reconhece.
        var empresa = await _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == TenantId, ct);

        var slug = pagina?.Slug ?? empresa?.Slug ?? string.Empty;

        var publicados = await _db.ItensCatalogo.AsNoTracking()
            .CountAsync(i => i.Tipo == TipoItem.Servico && i.Ativo && i.VisivelOnline
                             && i.DuracaoMinutos != null && i.DuracaoMinutos > 0, ct);

        var pendentes = await _db.Agendamentos.AsNoTracking()
            .CountAsync(a => a.Status == StatusAgendamento.PendenteAprovacao, ct);

        return new PaginaPublicaDto(
            pagina?.Ativa ?? false,
            slug,
            pagina?.TituloPublico,
            pagina?.Mensagem,
            pagina?.Endereco,
            pagina?.TelefoneContato,
            pagina?.AntecedenciaMinimaHoras ?? 2,
            pagina?.JanelaMaximaDias ?? 60,
            pagina?.ExigeAprovacao ?? false,
            pagina?.PermiteEscolherProfissional ?? true,
            pagina?.ExigeTelefone ?? true,
            pagina?.LimiteDiarioPorCliente ?? 5,
            publicados,
            pendentes,
            MontarUrl(slug));
    }

    /// <summary>
    /// O endereço que o dono compartilha. Sai do servidor e não do app: quem sabe onde a
    /// página está publicada é quem a hospeda.
    /// </summary>
    private string MontarUrl(string slug)
    {
        var baseUrl = _config["PaginaPublica:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = $"{Request.Scheme}://{Request.Host}";
        }

        // A página mora na rota `/p/:slug` do painel web. Sem o `/p` o link compartilhado
        // caía na tela de login (ou num 404), e não na página de agendamento.
        return string.IsNullOrWhiteSpace(slug) ? baseUrl : $"{baseUrl}/p/{Uri.EscapeDataString(slug)}";
    }

    private static string? Limpar(string? texto) =>
        string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
}

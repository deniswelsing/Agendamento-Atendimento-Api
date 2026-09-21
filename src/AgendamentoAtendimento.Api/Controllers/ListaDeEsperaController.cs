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
/// A fila de quem ficou sem horário. Existe porque "não tem vaga" não pode ser o fim da
/// conversa — e sem registro ninguém lembra quem ficou de fora.
/// </summary>
[Route("api/lista-de-espera")]
public class ListaDeEsperaController : ControllerBaseApi
{
    private readonly AppDbContext _db;
    private readonly ListaDeEsperaService _fila;

    public ListaDeEsperaController(AppDbContext db, ListaDeEsperaService fila)
    {
        _db = db;
        _fila = fila;
    }

    private static DateTimeOffset Agora => DateTimeOffset.UtcNow;

    [HttpGet]
    [RequerPermissao("lista-de-espera.ver")]
    public async Task<ActionResult<IReadOnlyList<EsperaDto>>> Listar(
        [FromQuery] StatusNaEspera? status, CancellationToken ct = default)
    {
        // Quem já saiu da fila fica fora por padrão: a tela é de quem ainda espera.
        var esperas = await _db.ListaDeEspera
            .AsNoTracking()
            .Include(e => e.Cliente)
            .Include(e => e.ItemCatalogo)
            .Include(e => e.Responsavel)
            .Where(e => status == null
                ? e.Status == StatusNaEspera.Aguardando || e.Status == StatusNaEspera.Avisado
                : e.Status == status)
            .OrderBy(e => e.CriadoEm)
            .Take(200)
            .ToListAsync(ct);

        return Ok(esperas.Select(ParaDto).ToList());
    }

    [HttpPost]
    [RequerPermissao("lista-de-espera.criar")]
    [RequerRecurso(CatalogoRecursos.ListaDeEspera)]
    public async Task<ActionResult<EsperaDto>> Entrar(NovaEsperaRequest req, CancellationToken ct)
    {
        NaoNulo(
            await _db.Clientes.FirstOrDefaultAsync(c => c.Id == req.ClienteId, ct),
            "Cliente não encontrado.");

        var servico = NaoNulo(
            await _db.ItensCatalogo.FirstOrDefaultAsync(
                i => i.Id == req.ItemCatalogoId && i.Tipo == TipoItem.Servico && i.Ativo, ct),
            "Serviço não encontrado.");

        // Esperar por um dia que já passou é esperar por nada.
        if (req.DataDesejada is { } data && data < DateOnly.FromDateTime(Agora.UtcDateTime))
        {
            throw new RegraDeNegocioException(
                "A data desejada já passou.", "DATA_NO_PASSADO");
        }

        var (entrada, jaEstava) = await _fila.EntrarAsync(
            req.ClienteId, servico.Id, req.DataDesejada, req.ResponsavelId, req.Observacao, ct);

        var completa = await CarregarAsync(entrada.Id, ct);

        // Já estar na fila não é erro: é a resposta certa para quem pediu de novo.
        return jaEstava
            ? Ok(ParaDto(completa!))
            : CreatedAtAction(nameof(Listar), new { }, ParaDto(completa!));
    }

    /// <summary>
    /// Quem estava esperando pelo que este agendamento cancelado liberou. Não avisa
    /// ninguém: só mostra a quem o time pode oferecer a vaga.
    /// </summary>
    [HttpGet("oportunidades/{agendamentoId:long}")]
    [RequerPermissao("lista-de-espera.ver")]
    public async Task<ActionResult<OportunidadeDto>> Oportunidades(
        long agendamentoId, CancellationToken ct)
    {
        var agendamento = NaoNulo(
            await _db.Agendamentos.AsNoTracking()
                .Include(a => a.Itens)
                .FirstOrDefaultAsync(a => a.Id == agendamentoId, ct),
            "Agendamento não encontrado.");

        var esperando = await _fila.QuemEsperavaPorAsync(agendamento, ct);

        return Ok(new OportunidadeDto(
            DateOnly.FromDateTime(agendamento.Inicio.UtcDateTime),
            agendamento.Id,
            esperando.Select(ParaDto).ToList()));
    }

    /// <summary>Marca como avisado quem o time chamou.</summary>
    [HttpPost("{id:long}/avisar")]
    [RequerPermissao("lista-de-espera.chamar")]
    public async Task<ActionResult<EsperaDto>> Avisar(long id, CancellationToken ct)
    {
        NaoNulo(await CarregarAsync(id, ct), "Espera não encontrada.");
        await _fila.MarcarComoAvisadosAsync(new[] { id }, Agora, ct);
        return Ok(ParaDto((await CarregarAsync(id, ct))!));
    }

    /// <summary>Tira da fila. O registro fica: é o que mostra a demanda que existiu.</summary>
    [HttpDelete("{id:long}")]
    [RequerPermissao("lista-de-espera.remover")]
    public async Task<ActionResult<EsperaDto>> Sair(long id, CancellationToken ct)
    {
        var entrada = NaoNulo(
            await _db.ListaDeEspera.FirstOrDefaultAsync(e => e.Id == id, ct),
            "Espera não encontrada.");

        entrada.Status = StatusNaEspera.Cancelado;
        await _db.SaveChangesAsync(ct);

        return Ok(ParaDto((await CarregarAsync(id, ct))!));
    }

    private Task<EntradaListaDeEspera?> CarregarAsync(long id, CancellationToken ct) =>
        _db.ListaDeEspera
            .Include(e => e.Cliente)
            .Include(e => e.ItemCatalogo)
            .Include(e => e.Responsavel)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

    private static EsperaDto ParaDto(EntradaListaDeEspera e)
    {
        var servico = e.ItemCatalogo?.Nome ?? "—";
        var quando = e.DataDesejada is { } d ? d.ToString("dd/MM") : "qualquer dia";
        var comQuem = e.Responsavel?.Nome is { } nome ? $" com {nome}" : string.Empty;

        return new EsperaDto(
            e.Id, e.ClienteId, e.Cliente?.NomeExibicao ?? "—",
            e.ItemCatalogoId, servico,
            e.DataDesejada, e.ResponsavelId, e.Responsavel?.Nome,
            e.Status,
            e.Status switch
            {
                StatusNaEspera.Aguardando => "Na fila",
                StatusNaEspera.Avisado => "Avisado",
                StatusNaEspera.Convertido => "Virou agendamento",
                StatusNaEspera.Cancelado => "Saiu da fila",
                _ => "Expirado",
            },
            e.CriadoEm, e.AvisadoEm, e.AgendamentoId, e.Observacao,
            $"{servico} · {quando}{comQuem}");
    }
}

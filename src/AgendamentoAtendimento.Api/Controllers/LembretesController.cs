using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>
/// Lembretes e confirmação. A fila é registro do que saiu e do que vai sair — é o que
/// deixa responder "este cliente foi avisado?" sem chutar.
/// </summary>
[Route("api/lembretes")]
public class LembretesController : ControllerBaseApi
{
    private readonly AppDbContext _db;
    private readonly LembreteService _lembretes;
    private readonly IEnviadorDeLembrete _enviador;

    public LembretesController(
        AppDbContext db, LembreteService lembretes, IEnviadorDeLembrete enviador)
    {
        _db = db;
        _lembretes = lembretes;
        _enviador = enviador;
    }

    private static DateTimeOffset Agora => DateTimeOffset.UtcNow;

    /// <summary>
    /// Só há canal de verdade quando alguém configurou um. O enviador de log entrega
    /// para o log, e a tela tem de saber a diferença.
    /// </summary>
    private bool CanalConfigurado => _enviador is not EnviadorDeLembreteEmLog;

    [HttpGet("configuracao")]
    [RequerPermissao("lembretes.ver")]
    public async Task<ActionResult<ConfiguracaoDeLembreteDto>> Configuracao(CancellationToken ct)
    {
        var config = await _lembretes.ConfiguracaoAsync(ct);
        return Ok(ParaDto(config));
    }

    [HttpPut("configuracao")]
    [RequerPermissao("lembretes.editar")]
    public async Task<ActionResult<ConfiguracaoDeLembreteDto>> Salvar(
        ConfiguracaoDeLembreteRequest req, CancellationToken ct)
    {
        if (req.HorasDeAntecedencia is < 1 or > 168)
        {
            throw new RegraDeNegocioException(
                "A antecedência do lembrete vai de 1 a 168 horas (uma semana).", "ANTECEDENCIA");
        }

        if (req.ToleranciaDeAtrasoMinutos is < 5 or > 1440)
        {
            throw new RegraDeNegocioException(
                "A tolerância de atraso vai de 5 minutos a 24 horas.", "TOLERANCIA");
        }

        var config = await _db.ConfiguracoesDeLembrete.FirstOrDefaultAsync(ct);
        if (config is null)
        {
            config = new ConfiguracaoDeLembrete();
            _db.ConfiguracoesDeLembrete.Add(config);
        }

        config.Ativo = req.Ativo;
        config.HorasDeAntecedencia = req.HorasDeAntecedencia;
        config.AvisarAoMarcar = req.AvisarAoMarcar;
        config.PedirConfirmacao = req.PedirConfirmacao;
        config.ToleranciaDeAtrasoMinutos = req.ToleranciaDeAtrasoMinutos;

        await _db.SaveChangesAsync(ct);
        return Ok(ParaDto(config));
    }

    /// <summary>A fila: o que está esperando a hora e o que já saiu.</summary>
    [HttpGet]
    [RequerPermissao("lembretes.ver")]
    public async Task<ActionResult<IReadOnlyList<LembreteDto>>> Listar(
        [FromQuery] StatusDeLembrete? status, CancellationToken ct = default)
    {
        var lembretes = await _db.Lembretes
            .AsNoTracking()
            .Include(l => l.Agendamento).ThenInclude(a => a!.Cliente)
            .Where(l => status == null || l.Status == status)
            .OrderBy(l => l.Status == StatusDeLembrete.Pendente ? 0 : 1)
            .ThenBy(l => l.QuandoEnviar)
            .Take(200)
            .ToListAsync(ct);

        return Ok(lembretes.Select(ParaDto).ToList());
    }

    /// <summary>
    /// Manda o que venceu. Fica como endpoint — e não só como rotina de fundo — porque
    /// quem configurou precisa conseguir testar sem esperar a próxima varredura.
    /// </summary>
    [HttpPost("despachar")]
    [RequerPermissao("lembretes.enviar")]
    public async Task<ActionResult<DespachoDto>> Despachar(CancellationToken ct)
    {
        var (enviados, falharam, expirados) = await _lembretes.DespacharAsync(Agora, ct);

        var partes = new List<string>();
        if (enviados > 0) partes.Add($"{enviados} enviado(s)");
        if (falharam > 0) partes.Add($"{falharam} com falha");
        if (expirados > 0) partes.Add($"{expirados} vencido(s) sem envio");

        var resumo = partes.Count == 0
            ? "Nada vencido na fila."
            : string.Join(", ", partes) + ".";

        if (enviados > 0 && !CanalConfigurado)
        {
            resumo += " Sem canal configurado: o aviso foi para o log, não para o cliente.";
        }

        return Ok(new DespachoDto(enviados, falharam, expirados, resumo));
    }

    private ConfiguracaoDeLembreteDto ParaDto(ConfiguracaoDeLembrete c) => new(
        c.Ativo, c.HorasDeAntecedencia, c.AvisarAoMarcar, c.PedirConfirmacao, c.Canal,
        c.ToleranciaDeAtrasoMinutos,
        CanalConfigurado,
        CanalConfigurado
            ? "Os avisos saem por e-mail."
            : "Nenhum canal de envio configurado nesta instalação: os avisos são "
              + "registrados no log em vez de chegarem ao cliente.");

    private static LembreteDto ParaDto(LembreteDeAgendamento l) => new(
        l.Id,
        l.AgendamentoId,
        l.Agendamento?.Cliente?.Nome ?? "—",
        l.Tipo,
        l.Tipo == TipoDeLembrete.Confirmacao ? "Aviso ao marcar" : "Lembrete antes",
        l.Canal,
        l.Status,
        l.Status switch
        {
            StatusDeLembrete.Pendente => "Na fila",
            StatusDeLembrete.Enviado => "Enviado",
            StatusDeLembrete.Cancelado => "Cancelado",
            _ => "Falhou",
        },
        l.QuandoEnviar,
        l.EnviadoEm,
        l.Agendamento?.Inicio ?? l.QuandoEnviar,
        l.Destino,
        l.Erro,
        l.Tentativas);
}

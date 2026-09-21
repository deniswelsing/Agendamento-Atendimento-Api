using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>
/// A página pública de agendamento: o cliente marca sozinho, sem conta e sem token.
///
/// É a única porta da Api aberta, então tudo que ela devolve é escolha explícita de quem
/// configurou a página — serviço a serviço. Nenhuma rota daqui aceita id de tenant vindo
/// do chamador: o tenant sai do slug, e só depois de a página ser encontrada e o plano
/// da empresa liberar o recurso.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/publico/{slug}")]
[Produces("application/json")]
public class PublicoController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PaginaPublicaService _paginas;

    public PublicoController(AppDbContext db, PaginaPublicaService paginas)
    {
        _db = db;
        _paginas = paginas;
    }

    private static DateTimeOffset Agora => DateTimeOffset.UtcNow;

    /// <summary>O que a página mostra ao abrir: serviços, profissionais e a janela aberta.</summary>
    [HttpGet]
    public async Task<ActionResult<PaginaPublicaInfoDto>> Info(string slug, CancellationToken ct)
    {
        var pagina = await _paginas.AssumirPorSlugAsync(slug, ct);
        if (pagina is null)
        {
            return PaginaInexistente();
        }

        var empresa = await _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == pagina.TenantId, ct);

        var servicos = await _paginas.ServicosPublicosAsync(ct);
        var (primeira, ultima) = _paginas.JanelaPublica(pagina, Agora);

        // A lista de profissionais só sai quando quem configurou deixou o cliente escolher,
        // e mesmo assim vai só o nome: e-mail e perfil são dado interno.
        // Quem ainda não aceitou o convite fica de fora: oferecer alguém que nem entrou
        // no sistema é prometer atendimento que ninguém vai prestar.
        var profissionais = pagina.PermiteEscolherProfissional
            ? await _db.Usuarios.AsNoTracking()
                .Where(u => u.Ativo && u.Atendente && !u.ConvitePendente)
                .OrderBy(u => u.Nome)
                .Select(u => new ProfissionalPublicoDto(u.Id, u.Nome))
                .ToListAsync(ct)
            : new List<ProfissionalPublicoDto>();

        return Ok(new PaginaPublicaInfoDto(
            pagina.Slug,
            pagina.TituloPublico ?? empresa?.NomeEmpresa ?? "Agendamento",
            pagina.Mensagem,
            pagina.Endereco,
            pagina.TelefoneContato,
            empresa?.Moeda ?? "BRL",
            empresa?.FusoHorario ?? "America/Sao_Paulo",
            pagina.ExigeTelefone,
            pagina.PermiteEscolherProfissional,
            pagina.ExigeAprovacao,
            pagina.AntecedenciaMinimaHoras,
            primeira,
            ultima,
            servicos.Select(s => new ServicoPublicoDto(
                s.Id, s.Nome, s.Descricao, s.Categoria, s.DuracaoMinutos ?? 0, s.Preco)).ToList(),
            profissionais));
    }

    /// <summary>Horários que o cliente pode escolher num dia.</summary>
    [HttpGet("disponibilidade")]
    public async Task<ActionResult<DiaDaAgendaDto>> Disponibilidade(
        string slug,
        [FromQuery] DateOnly data,
        [FromQuery] long[]? itensIds,
        [FromQuery] long? responsavelId,
        CancellationToken ct)
    {
        var pagina = await _paginas.AssumirPorSlugAsync(slug, ct);
        if (pagina is null)
        {
            return PaginaInexistente();
        }

        var (resultado, dia) = await _paginas.DisponibilidadeAsync(
            pagina, data, itensIds ?? Array.Empty<long>(), responsavelId, Agora, ct);

        return resultado.Ok && dia is not null
            ? Ok(dia.ParaDto())
            : Recusa(resultado);
    }

    /// <summary>
    /// O cliente marca. Devolve o código que ele guarda — é com ele, e só com ele, que
    /// dá para consultar e desmarcar depois.
    /// </summary>
    [HttpPost("agendamentos")]
    public async Task<ActionResult<AgendamentoPublicoDto>> Agendar(
        string slug, NovoAgendamentoPublicoRequest req, CancellationToken ct)
    {
        var pagina = await _paginas.AssumirPorSlugAsync(slug, ct);
        if (pagina is null)
        {
            return PaginaInexistente();
        }

        var (resultado, agendamento) = await _paginas.AgendarAsync(
            pagina, req.Nome, req.Email, req.Telefone, req.ItensIds ?? Array.Empty<long>(),
            req.Inicio, req.ResponsavelId, req.Observacoes, Agora, ct);

        if (!resultado.Ok || agendamento is null)
        {
            return Recusa(resultado);
        }

        var completo = await _paginas.PorCodigoAsync(agendamento.CodigoPublico!, ct);
        return Ok(await ComprovanteAsync(completo!, pagina, ct));
    }

    /// <summary>Consulta pelo código. Sem código não há listagem: não existe "meus agendamentos".</summary>
    [HttpGet("agendamentos/{codigo}")]
    public async Task<ActionResult<AgendamentoPublicoDto>> Consultar(
        string slug, string codigo, CancellationToken ct)
    {
        var pagina = await _paginas.AssumirPorSlugAsync(slug, ct);
        if (pagina is null)
        {
            return PaginaInexistente();
        }

        var agendamento = await _paginas.PorCodigoAsync(codigo, ct);
        return agendamento is null
            ? NotFound(new ErroApi("Código não encontrado.", "NAO_ENCONTRADO"))
            : Ok(await ComprovanteAsync(agendamento, pagina, ct));
    }

    /// <summary>
    /// O cliente desmarca pelo código. Depois de iniciado ou concluído não desmarca mais:
    /// a essa altura o atendimento já aconteceu e quem resolve é o time.
    /// </summary>
    [HttpDelete("agendamentos/{codigo}")]
    public async Task<ActionResult<AgendamentoPublicoDto>> Cancelar(
        string slug, string codigo, [FromQuery] string? motivo, CancellationToken ct)
    {
        var pagina = await _paginas.AssumirPorSlugAsync(slug, ct);
        if (pagina is null)
        {
            return PaginaInexistente();
        }

        var agendamento = await _paginas.PorCodigoAsync(codigo, ct);
        if (agendamento is null)
        {
            return NotFound(new ErroApi("Código não encontrado.", "NAO_ENCONTRADO"));
        }

        if (agendamento.Status is StatusAgendamento.EmAtendimento
            or StatusAgendamento.Concluido or StatusAgendamento.NaoCompareceu)
        {
            return BadRequest(new ErroApi(
                "Este agendamento não pode mais ser desmarcado por aqui. Fale com a gente.",
                "CANCELAMENTO_INDISPONIVEL"));
        }

        if (agendamento.Status != StatusAgendamento.Cancelado)
        {
            agendamento.Status = StatusAgendamento.Cancelado;
            agendamento.MotivoCancelamento = string.IsNullOrWhiteSpace(motivo)
                ? "Cancelado pelo cliente na página de agendamento."
                : motivo.Trim();
            await _db.SaveChangesAsync(ct);
        }

        return Ok(await ComprovanteAsync(agendamento, pagina, ct));
    }

    /// <summary>
    /// O cliente confirma presença pelo código — é o que o link do lembrete abre. Sem
    /// isso, o lembrete só informa, e a agenda continua sem saber quem vem.
    /// </summary>
    [HttpPost("agendamentos/{codigo}/confirmar")]
    public async Task<ActionResult<AgendamentoPublicoDto>> Confirmar(
        string slug, string codigo, CancellationToken ct)
    {
        var pagina = await _paginas.AssumirPorSlugAsync(slug, ct);
        if (pagina is null)
        {
            return PaginaInexistente();
        }

        var agendamento = await _paginas.PorCodigoAsync(codigo, ct);
        if (agendamento is null)
        {
            return NotFound(new ErroApi("Código não encontrado.", "NAO_ENCONTRADO"));
        }

        // Cancelado ou já atendido não se confirma: confirmar presença no que já passou
        // não diz nada a ninguém, e no que foi cancelado ressuscitaria o horário.
        if (agendamento.Status is StatusAgendamento.Cancelado or StatusAgendamento.EmAtendimento
            or StatusAgendamento.Concluido or StatusAgendamento.NaoCompareceu)
        {
            return BadRequest(new ErroApi(
                "Este agendamento não pode mais ser confirmado por aqui. Fale com a gente.",
                "CONFIRMACAO_INDISPONIVEL"));
        }

        // Um pedido esperando aprovação do time não vira compromisso porque o cliente
        // clicou: quem aprova é o time. A confirmação fica registrada para quando for.
        if (agendamento.Status == StatusAgendamento.Agendado)
        {
            agendamento.Status = StatusAgendamento.Confirmado;
        }

        // Confirmar duas vezes é o normal: o cliente clica no link de novo. A data da
        // primeira é a que vale — reescrever contaria a história errada.
        agendamento.ConfirmadoEm ??= Agora;
        await _db.SaveChangesAsync(ct);

        return Ok(await ComprovanteAsync(agendamento, pagina, ct));
    }

    private async Task<AgendamentoPublicoDto> ComprovanteAsync(
        Agendamento a, ConfiguracaoPaginaPublica pagina, CancellationToken ct)
    {
        var empresa = await _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == pagina.TenantId, ct);

        return new AgendamentoPublicoDto(
            a.CodigoPublico ?? string.Empty,
            a.Status,
            a.Inicio,
            a.Fim,
            a.Responsavel?.Nome,
            a.Itens.Select(i => i.Nome).ToList(),
            a.Itens.Sum(i => i.PrecoUnitario * i.Quantidade),
            pagina.TituloPublico ?? empresa?.NomeEmpresa ?? string.Empty,
            a.Status == StatusAgendamento.PendenteAprovacao);
    }

    /// <summary>
    /// Página desligada, inexistente ou fora do plano respondem igual, de propósito: quem
    /// desliga a página não quer que o endereço antigo continue confirmando nada.
    /// </summary>
    private NotFoundObjectResult PaginaInexistente() =>
        NotFound(new ErroApi("Página de agendamento não encontrada.", "NAO_ENCONTRADO"));

    private ObjectResult Recusa(ResultadoPublico r)
    {
        var codigo = r.Motivo.ToString();
        var corpo = new ErroApi(r.Mensagem ?? "Não foi possível concluir.", codigo);

        // Excesso de pedido é 429 para quem monitora a porta aberta ver o que é abuso.
        return r.Motivo == RecusaPublica.LimiteDiario
            ? StatusCode(StatusCodes.Status429TooManyRequests, corpo)
            : BadRequest(corpo);
    }
}

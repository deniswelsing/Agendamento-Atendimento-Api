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
    private readonly LembreteService? _lembretes;
    private readonly ListaDeEsperaService? _fila;

    public PublicoController(
        AppDbContext db, PaginaPublicaService paginas,
        LembreteService? lembretes = null, ListaDeEsperaService? fila = null)
    {
        _db = db;
        _paginas = paginas;
        _lembretes = lembretes;
        _fila = fila;
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

        if (!await _paginas.AssinaturaEmDiaAsync(Agora, ct))
        {
            return PaginaIndisponivel();
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

        if (!await _paginas.AssinaturaEmDiaAsync(Agora, ct))
        {
            return PaginaIndisponivel();
        }

        var (resultado, dia) = await _paginas.DisponibilidadeAsync(
            pagina, data, itensIds ?? Array.Empty<long>(), responsavelId, Agora, ct);

        if (!resultado.Ok || dia is null)
        {
            return Recusa(resultado);
        }

        var dto = dia.ParaDto();
        return Ok(pagina.PermiteEscolherProfissional ? dto with { TotalAgendamentos = 0 } : SemOTime(dto));
    }

    /// <summary>
    /// Os horários sem ninguém do time. Com a escolha de profissional desligada, a lista
    /// de profissionais já não saía — mas cada encaixe levava o id, o nome e os candidatos
    /// de quem atenderia: o time inteiro, pela porta aberta. E o total de atendimentos do
    /// dia da empresa não é assunto de quem está marcando.
    /// </summary>
    private static DiaDaAgendaDto SemOTime(DiaDaAgendaDto dia)
    {
        static SlotDto Anonimo(SlotDto slot) => slot with
        {
            ResponsavelId = null,
            ResponsavelNome = null,
            Atribuicoes = slot.Atribuicoes
                .Select(a => a with
                {
                    ResponsavelId = null,
                    ResponsavelNome = null,
                    Candidatos = Array.Empty<PessoaResumoDto>(),
                })
                .ToList(),
        };

        return dia with
        {
            TotalAgendamentos = 0,
            Livres = dia.Livres.Select(Anonimo).ToList(),
            Sugestoes = dia.Sugestoes?
                .Select(s => s with { Slots = s.Slots.Select(Anonimo).ToList() })
                .ToList(),
        };
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

        if (!await _paginas.AssinaturaEmDiaAsync(Agora, ct))
        {
            return PaginaIndisponivel();
        }

        // O que o cliente digita tem forma e tamanho: "x" não é telefone, "abc" não é
        // e-mail, e um texto maior que a coluna derrubava a porta aberta com 500.
        if (!string.IsNullOrWhiteSpace(req.Email) && !Validacoes.EmailValido(req.Email))
        {
            return BadRequest(new ErroApi("O e-mail informado não é válido.", "EMAIL_INVALIDO"));
        }
        if (!string.IsNullOrWhiteSpace(req.Telefone) && !Validacoes.TelefoneValido(req.Telefone))
        {
            return BadRequest(new ErroApi("O telefone informado não é válido.", "TELEFONE_INVALIDO"));
        }
        Validacoes.Cabe(req.Nome, 150, "O nome");
        Validacoes.Cabe(req.Email, 200, "O e-mail");
        Validacoes.Cabe(req.Telefone, 30, "O telefone");
        Validacoes.Cabe(req.Observacoes, 1000, "A observação");

        var (resultado, agendamento) = await _paginas.AgendarAsync(
            pagina, req.Nome, req.Email, req.Telefone, req.ItensIds ?? Array.Empty<long>(),
            req.Inicio, req.ResponsavelId, req.Observacoes, Agora, ct);

        if (!resultado.Ok || agendamento is null)
        {
            return Recusa(resultado);
        }

        // Quem marcou pela página é avisado como quem foi marcado pelo time (o pedido
        // pendente só quando for aprovado), e sai da fila de espera se estava nela. Só a
        // agenda fazia as duas coisas.
        if (_lembretes is not null)
        {
            await _lembretes.ReprogramarAsync(agendamento.Id, Agora, ct);
        }
        // O pedido que ainda espera aprovação não é compromisso: a espera só vira
        // "Convertido" quando a empresa aprovar — recusado, o cliente continua na fila.
        if (_fila is not null && agendamento.Status != StatusAgendamento.PendenteAprovacao)
        {
            await _fila.ConverterPorAgendamentoAsync(agendamento, ct);
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

        Validacoes.Cabe(motivo, 500, "O motivo");

        if (agendamento.Status != StatusAgendamento.Cancelado)
        {
            agendamento.Status = StatusAgendamento.Cancelado;
            agendamento.MotivoCancelamento = string.IsNullOrWhiteSpace(motivo)
                ? "Cancelado pelo cliente na página de agendamento."
                : motivo.Trim();
            await _db.SaveChangesAsync(ct);

            // Desmarcado, os avisos que estavam na fila não saem mais.
            if (_lembretes is not null)
            {
                await _lembretes.CancelarPendentesAsync(agendamento.Id, ct);
            }
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

    /// <summary>
    /// A empresa está com a assinatura parada: a página não mostra horários nem aceita
    /// pedido novo. É 503, e não 402, de propósito — quem abre a página é o cliente da
    /// empresa, e não é ele quem tem de pagar nada. Consultar, confirmar e desmarcar pelo
    /// código continuam funcionando: quem já marcou não perde o acesso ao que marcou.
    /// </summary>
    private ObjectResult PaginaIndisponivel() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new ErroApi(
            "A agenda online desta empresa está temporariamente indisponível. " +
            "Entre em contato diretamente com a empresa para marcar.",
            "PAGINA_INDISPONIVEL"));

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

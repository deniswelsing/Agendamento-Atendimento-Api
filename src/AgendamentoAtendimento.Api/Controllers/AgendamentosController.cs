using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>
/// Agenda. A disponibilidade é calculada no servidor: o app só desenha os encaixes que
/// vierem daqui e nunca decide sozinho se um horário cabe.
/// </summary>
[Route("api/agendamentos")]
public class AgendamentosController : ControllerBaseApi
{
    private readonly AppDbContext _db;
    private readonly DisponibilidadeService _disponibilidade;

    public AgendamentosController(AppDbContext db, DisponibilidadeService disponibilidade)
    {
        _db = db;
        _disponibilidade = disponibilidade;
    }

    [HttpGet]
    [RequerPermissao("agenda.ver")]
    public async Task<ActionResult<IReadOnlyList<AgendamentoDto>>> Listar(
        [FromQuery] DateOnly de,
        [FromQuery] DateOnly ate,
        [FromQuery] long? responsavelId,
        [FromQuery] long? clienteId,
        [FromQuery] StatusAgendamento? status,
        CancellationToken ct = default)
    {
        if (ate < de)
        {
            (de, ate) = (ate, de);
        }

        if (ate.DayNumber - de.DayNumber > 92)
        {
            throw new RegraDeNegocioException("O período não pode passar de 92 dias.", "PERIODO");
        }

        var inicio = new DateTimeOffset(de.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var fim = new DateTimeOffset(ate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var agendamentos = await _db.Agendamentos
            .AsNoTracking()
            .Include(a => a.Cliente)
            .Include(a => a.Responsavel)
            .Include(a => a.Itens)
            .Where(a => a.Inicio < fim && a.Fim > inicio)
            .Where(a => responsavelId == null || a.ResponsavelId == responsavelId)
            .Where(a => clienteId == null || a.ClienteId == clienteId)
            .Where(a => status == null || a.Status == status)
            .OrderBy(a => a.Inicio)
            .ToListAsync(ct);

        return Ok(agendamentos.Select(a => a.ParaDto()).ToList());
    }

    [HttpGet("{id:long}")]
    [RequerPermissao("agenda.ver")]
    public async Task<ActionResult<AgendamentoDto>> Obter(long id, CancellationToken ct)
    {
        var agendamento = NaoNulo(await CarregarAsync(id, ct), "Agendamento não encontrado.");
        return Ok(agendamento.ParaDto());
    }

    /// <summary>Encaixes livres de um dia, já considerando empresa, jornada e ocupação.</summary>
    [HttpGet("disponibilidade")]
    [RequerPermissao("agenda.ver")]
    public async Task<ActionResult<DiaDaAgendaDto>> Disponibilidade(
        [FromQuery] DateOnly data,
        [FromQuery] long[]? itensIds,
        [FromQuery] long? responsavelId,
        CancellationToken ct = default)
    {
        var itens = itensIds ?? Array.Empty<long>();
        var duracao = await DuracaoDosItensAsync(itens, ct);
        // Os itens entram no cálculo: só quem presta todos eles aparece como encaixe.
        var dia = await _disponibilidade.ObterDiaAsync(data, duracao, responsavelId, ct, itens);
        return Ok(dia.ParaDto());
    }

    /// <summary>
    /// Mesma coisa para um período — é o que alimenta as visões de semana e de mês do app,
    /// incluindo quais dias estão fechados.
    /// </summary>
    [HttpGet("disponibilidade/periodo")]
    [RequerPermissao("agenda.ver")]
    public async Task<ActionResult<IReadOnlyList<DiaDaAgendaDto>>> DisponibilidadeDoPeriodo(
        [FromQuery] DateOnly de,
        [FromQuery] DateOnly ate,
        [FromQuery] long[]? itensIds,
        [FromQuery] long? responsavelId,
        CancellationToken ct = default)
    {
        if (ate.DayNumber - de.DayNumber > 62)
        {
            throw new RegraDeNegocioException("O período não pode passar de 62 dias.", "PERIODO");
        }

        var itens = itensIds ?? Array.Empty<long>();
        var duracao = await DuracaoDosItensAsync(itens, ct);
        // Os itens também dizem quem pode prestar: sem eles, a semana contaria encaixes
        // com quem não presta o serviço, e o dia — que já filtra — mostraria menos.
        var dias = await _disponibilidade.ObterPeriodoAsync(de, ate, duracao, responsavelId, ct, itens);
        return Ok(dias.Select(d => d.ParaDto()).ToList());
    }

    [HttpPost]
    [RequerPermissao("agenda.criar")]
    public async Task<ActionResult<AgendamentoDto>> Criar(NovoAgendamentoRequest req, CancellationToken ct)
    {
        if (req.ItensIds is null || req.ItensIds.Count == 0)
        {
            throw new RegraDeNegocioException("Escolha ao menos um serviço.", "SEM_SERVICO");
        }

        var cliente = NaoNulo(
            await _db.Clientes.FirstOrDefaultAsync(c => c.Id == req.ClienteId, ct),
            "Cliente não encontrado.");

        var servicos = await _db.ItensCatalogo
            .Where(i => req.ItensIds.Contains(i.Id) && i.Tipo == TipoItem.Servico && i.Ativo)
            .ToListAsync(ct);

        if (servicos.Count != req.ItensIds.Distinct().Count())
        {
            throw new RegraDeNegocioException(
                "Algum serviço não existe ou está inativo.", "SERVICO_INVALIDO");
        }

        var duracao = servicos.Sum(s => s.DuracaoMinutos ?? 0);
        var inicio = req.Inicio.ToUniversalTime();
        var fim = inicio.AddMinutes(duracao);

        var responsavelId = req.ResponsavelId
            ?? await EscolherResponsavelLivreAsync(inicio, duracao, ct, req.ItensIds);

        if (responsavelId is null)
        {
            throw new RegraDeNegocioException(
                "Ninguém do time está livre nesse horário.", "SEM_RESPONSAVEL");
        }

        // Revalida no servidor: o app pode ter mostrado uma agenda desatualizada.
        if (!await _disponibilidade.EstaLivreAsync(
                inicio, fim, responsavelId.Value, null, ct, req.ItensIds))
        {
            throw new RegraDeNegocioException(
                "Esse horário acabou de ser ocupado ou está fora da janela de atendimento.",
                "HORARIO_INDISPONIVEL");
        }

        var agendamento = new Agendamento
        {
            ClienteId = cliente.Id,
            Inicio = inicio,
            Fim = fim,
            Status = StatusAgendamento.Agendado,
            ResponsavelId = responsavelId,
            Observacoes = req.Observacoes,
            LocalAtendimento = req.LocalAtendimento,
        };

        foreach (var servico in servicos)
        {
            agendamento.Itens.Add(new AgendamentoItem
            {
                ItemCatalogoId = servico.Id,
                Nome = servico.Nome,
                DuracaoMinutos = servico.DuracaoMinutos ?? 0,
                PrecoUnitario = servico.Preco,
            });
        }

        _db.Agendamentos.Add(agendamento);
        await _db.SaveChangesAsync(ct);

        var completo = await CarregarAsync(agendamento.Id, ct);
        return CreatedAtAction(nameof(Obter), new { id = agendamento.Id }, completo!.ParaDto());
    }

    [HttpPut("{id:long}")]
    [RequerPermissao("agenda.editar")]
    public async Task<ActionResult<AgendamentoDto>> Reagendar(
        long id, NovoAgendamentoRequest req, CancellationToken ct)
    {
        var agendamento = NaoNulo(
            await _db.Agendamentos.Include(a => a.Itens).FirstOrDefaultAsync(a => a.Id == id, ct),
            "Agendamento não encontrado.");

        if (agendamento.Status is StatusAgendamento.Concluido or StatusAgendamento.Cancelado)
        {
            throw new RegraDeNegocioException(
                "Agendamento concluído ou cancelado não pode ser alterado.", "STATUS_FINAL");
        }

        var servicos = await _db.ItensCatalogo
            .Where(i => req.ItensIds.Contains(i.Id) && i.Tipo == TipoItem.Servico)
            .ToListAsync(ct);

        if (servicos.Count == 0)
        {
            throw new RegraDeNegocioException("Escolha ao menos um serviço.", "SEM_SERVICO");
        }

        var duracao = servicos.Sum(s => s.DuracaoMinutos ?? 0);
        var inicio = req.Inicio.ToUniversalTime();
        var fim = inicio.AddMinutes(duracao);
        var responsavelId = req.ResponsavelId ?? agendamento.ResponsavelId;

        if (responsavelId is null ||
            !await _disponibilidade.EstaLivreAsync(
                inicio, fim, responsavelId.Value, id, ct, req.ItensIds))
        {
            throw new RegraDeNegocioException(
                "Esse horário não está disponível para o responsável escolhido.",
                "HORARIO_INDISPONIVEL");
        }

        agendamento.ClienteId = req.ClienteId;
        agendamento.Inicio = inicio;
        agendamento.Fim = fim;
        agendamento.ResponsavelId = responsavelId;
        agendamento.Observacoes = req.Observacoes;
        agendamento.LocalAtendimento = req.LocalAtendimento;

        agendamento.Itens.Clear();
        foreach (var servico in servicos)
        {
            agendamento.Itens.Add(new AgendamentoItem
            {
                ItemCatalogoId = servico.Id,
                Nome = servico.Nome,
                DuracaoMinutos = servico.DuracaoMinutos ?? 0,
                PrecoUnitario = servico.Preco,
            });
        }

        await _db.SaveChangesAsync(ct);
        var completo = await CarregarAsync(id, ct);
        return Ok(completo!.ParaDto());
    }

    /// <summary>
    /// Troca o status. Cancelar exige permissão própria, porque libera a agenda e some com
    /// o atendimento do dia.
    /// </summary>
    [HttpPatch("{id:long}/status")]
    [RequerPermissao("agenda.concluir")]
    public async Task<ActionResult<AgendamentoDto>> AlterarStatus(
        long id, AlterarStatusRequest req, CancellationToken ct)
    {
        var agendamento = NaoNulo(
            await _db.Agendamentos.FirstOrDefaultAsync(a => a.Id == id, ct),
            "Agendamento não encontrado.");

        if (req.Status == StatusAgendamento.Cancelado &&
            !Domain.Usuarios.Permissoes.Permite(PermissoesDoUsuario, "agenda.cancelar"))
        {
            return Forbid();
        }

        if (!TransicaoValida(agendamento.Status, req.Status))
        {
            throw new RegraDeNegocioException(
                $"Não é possível ir de {agendamento.Status} para {req.Status}.", "TRANSICAO_INVALIDA");
        }

        agendamento.Status = req.Status;
        agendamento.IniciadoEm = req.Status == StatusAgendamento.EmAtendimento
            ? DateTimeOffset.UtcNow : agendamento.IniciadoEm;
        agendamento.ConcluidoEm = req.Status == StatusAgendamento.Concluido
            ? DateTimeOffset.UtcNow : agendamento.ConcluidoEm;
        agendamento.MotivoCancelamento = req.Status == StatusAgendamento.Cancelado
            ? req.Motivo : agendamento.MotivoCancelamento;

        await _db.SaveChangesAsync(ct);
        var completo = await CarregarAsync(id, ct);
        return Ok(completo!.ParaDto());
    }

    [HttpDelete("{id:long}")]
    [RequerPermissao("agenda.cancelar")]
    public async Task<IActionResult> Cancelar(
        long id, [FromQuery] string? motivo, CancellationToken ct)
    {
        var agendamento = NaoNulo(
            await _db.Agendamentos.FirstOrDefaultAsync(a => a.Id == id, ct),
            "Agendamento não encontrado.");

        if (agendamento.Status == StatusAgendamento.Concluido)
        {
            throw new RegraDeNegocioException(
                "Atendimento concluído não pode ser cancelado.", "STATUS_FINAL");
        }

        agendamento.Status = StatusAgendamento.Cancelado;
        agendamento.MotivoCancelamento = motivo;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static bool TransicaoValida(StatusAgendamento de, StatusAgendamento para) => de switch
    {
        // Um pedido da página só sai de pendente aprovado ou recusado — nunca direto
        // para atendimento, que passaria por cima da aprovação que o dono pediu.
        StatusAgendamento.PendenteAprovacao => para is StatusAgendamento.Confirmado
            or StatusAgendamento.Agendado or StatusAgendamento.Cancelado,
        StatusAgendamento.Agendado => para is StatusAgendamento.Confirmado
            or StatusAgendamento.EmAtendimento or StatusAgendamento.Cancelado
            or StatusAgendamento.NaoCompareceu,
        StatusAgendamento.Confirmado => para is StatusAgendamento.EmAtendimento
            or StatusAgendamento.Cancelado or StatusAgendamento.NaoCompareceu,
        StatusAgendamento.EmAtendimento => para is StatusAgendamento.Concluido
            or StatusAgendamento.Cancelado,
        _ => false,
    };

    private async Task<int> DuracaoDosItensAsync(IReadOnlyCollection<long> itensIds, CancellationToken ct)
    {
        if (itensIds.Count == 0)
        {
            return 0;
        }

        return await _db.ItensCatalogo
            .Where(i => itensIds.Contains(i.Id))
            .SumAsync(i => i.DuracaoMinutos ?? 0, ct);
    }

    /// <summary>Quando o app manda "quem estiver livre", o servidor escolhe.</summary>
    private async Task<long?> EscolherResponsavelLivreAsync(
        DateTimeOffset inicio, int duracao, CancellationToken ct,
        IReadOnlyCollection<long>? itensIds = null)
    {
        var data = DateOnly.FromDateTime(inicio.UtcDateTime);
        var dia = await _disponibilidade.ObterDiaAsync(data, duracao, null, ct, itensIds);
        return dia.Livres.FirstOrDefault(s => s.Inicio == inicio)?.ResponsavelId;
    }

    private Task<Agendamento?> CarregarAsync(long id, CancellationToken ct) =>
        _db.Agendamentos
            .Include(a => a.Cliente)
            .Include(a => a.Responsavel)
            .Include(a => a.Itens)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
}

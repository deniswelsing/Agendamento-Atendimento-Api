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
    private readonly LembreteService _lembretes;

    public AgendamentosController(
        AppDbContext db, DisponibilidadeService disponibilidade, LembreteService lembretes)
    {
        _db = db;
        _disponibilidade = disponibilidade;
        _lembretes = lembretes;
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
            .Include(a => a.Itens).ThenInclude(i => i.Responsavel)
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

        // Dia sem encaixe não é beco sem saída: o servidor já diz onde há o próximo.
        // Deixar a tela procurar dia a dia seria uma requisição por dia, e ela nem sabe
        // quem presta o quê.
        if (dia.Livres.Count == 0 && itens.Length > 0)
        {
            var proxima = await _disponibilidade.ProximaOportunidadeAsync(
                data.AddDays(1), itens, responsavelId, ct: ct);

            if (proxima is { } achado)
            {
                return Ok(dia.ParaDto() with
                {
                    Proxima = new ProximaOportunidadeDto(achado.Data, achado.Slot.ParaDto()),
                });
            }
        }
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

        var inicio = req.Inicio.ToUniversalTime();

        // Quem presta cada serviço: o que veio no pedido, e o resto o servidor resolve.
        // É a mesma conta que montou a grade, então o que a tela ofereceu é o que entra.
        var atribuicoes = await _disponibilidade.MontarAtribuicoesAsync(
            inicio, req.ItensIds, req.ResponsavelId, null, 0, ct);

        if (atribuicoes is null)
        {
            throw new RegraDeNegocioException(
                "Esse horário acabou de ser ocupado ou está fora da janela de atendimento.",
                "HORARIO_INDISPONIVEL");
        }

        atribuicoes = AplicarEscolhas(atribuicoes, req.ItensIds, req.ResponsaveisPorItem);
        await ValidarEscolhasAsync(atribuicoes, inicio, req.ItensIds, null, ct);

        var fim = atribuicoes[^1].Fim;

        var agendamento = new Agendamento
        {
            ClienteId = cliente.Id,
            Inicio = inicio,
            Fim = fim,
            Status = StatusAgendamento.Agendado,
            // Quem responde pelo atendimento é quem presta o primeiro serviço.
            ResponsavelId = atribuicoes[0].ResponsavelId,
            Observacoes = req.Observacoes,
            LocalAtendimento = req.LocalAtendimento,
        };

        var ordem = 0;
        foreach (var id in req.ItensIds)
        {
            var servico = servicos.First(x => x.Id == id);
            agendamento.Itens.Add(new AgendamentoItem
            {
                ItemCatalogoId = servico.Id,
                Nome = servico.Nome,
                DuracaoMinutos = servico.DuracaoMinutos ?? 0,
                PrecoUnitario = servico.Preco,
                Ordem = ordem,
                ResponsavelId = atribuicoes[ordem].ResponsavelId,
            });
            ordem++;
        }

        _db.Agendamentos.Add(agendamento);
        await _db.SaveChangesAsync(ct);

        // A fila de avisos nasce junto: um agendamento sem lembrete programado é um
        // cliente que ninguém vai avisar.
        await _lembretes.ReprogramarAsync(agendamento.Id, DateTimeOffset.UtcNow, ct);

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

        var inicio = req.Inicio.ToUniversalTime();

        // Ignora o próprio agendamento na conta: reagendar para o mesmo horário não pode
        // esbarrar no compromisso que está sendo movido.
        var atribuicoes = await _disponibilidade.MontarAtribuicoesAsync(
            inicio, req.ItensIds, req.ResponsavelId, id, 0, ct);

        if (atribuicoes is null)
        {
            throw new RegraDeNegocioException(
                "Esse horário não está disponível para os serviços escolhidos.",
                "HORARIO_INDISPONIVEL");
        }

        atribuicoes = AplicarEscolhas(atribuicoes, req.ItensIds, req.ResponsaveisPorItem);
        await ValidarEscolhasAsync(atribuicoes, inicio, req.ItensIds, id, ct);

        agendamento.ClienteId = req.ClienteId;
        agendamento.Inicio = inicio;
        agendamento.Fim = atribuicoes[^1].Fim;
        agendamento.ResponsavelId = atribuicoes[0].ResponsavelId;
        agendamento.Observacoes = req.Observacoes;
        agendamento.LocalAtendimento = req.LocalAtendimento;

        agendamento.Itens.Clear();
        var ordem = 0;
        foreach (var itemId in req.ItensIds)
        {
            var servico = servicos.First(x => x.Id == itemId);
            agendamento.Itens.Add(new AgendamentoItem
            {
                ItemCatalogoId = servico.Id,
                Nome = servico.Nome,
                DuracaoMinutos = servico.DuracaoMinutos ?? 0,
                PrecoUnitario = servico.Preco,
                Ordem = ordem,
                ResponsavelId = atribuicoes[ordem].ResponsavelId,
            });
            ordem++;
        }

        await _db.SaveChangesAsync(ct);

        // Remarcar invalida o lembrete antigo: ele aponta para uma hora que não existe
        // mais. Morre um, nasce outro — e o cliente não recebe aviso da hora errada.
        await _lembretes.ReprogramarAsync(id, DateTimeOffset.UtcNow, ct);

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

        // Atendimento que acabou — de qualquer jeito — não tem mais o que lembrar.
        if (req.Status is StatusAgendamento.Cancelado or StatusAgendamento.Concluido
            or StatusAgendamento.NaoCompareceu or StatusAgendamento.EmAtendimento)
        {
            await _lembretes.CancelarPendentesAsync(id, ct);
        }

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

        // Lembrar de um atendimento cancelado é pior que não lembrar de nada.
        await _lembretes.CancelarPendentesAsync(id, ct);
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
    /// <summary>
    /// Troca, na cadeia que o servidor montou, quem o pedido escolheu a dedo. Posição
    /// nula mantém a escolha do servidor — é o caso de quem só quis mexer num serviço.
    /// </summary>
    /// <summary>
    /// Troca quem presta um serviço já marcado, sem mexer no resto do atendimento.
    ///
    /// A pessoa nova precisa prestar aquele serviço e estar livre na janela dele. Nulo
    /// devolve o serviço para quem responde pelo atendimento.
    /// </summary>
    [HttpPatch("{id:long}/itens/{itemId:long}/responsavel")]
    [RequerPermissao("agenda.editar")]
    public async Task<ActionResult<AgendamentoDto>> TrocarResponsavelDoItem(
        long id, long itemId, TrocarResponsavelRequest req, CancellationToken ct)
    {
        var agendamento = NaoNulo(
            await _db.Agendamentos.Include(a => a.Itens).FirstOrDefaultAsync(a => a.Id == id, ct),
            "Agendamento não encontrado.");

        if (agendamento.Status is StatusAgendamento.Cancelado)
        {
            throw new RegraDeNegocioException(
                "Agendamento cancelado não muda de responsável.", "STATUS_FINAL");
        }

        var item = NaoNulo(
            agendamento.Itens.FirstOrDefault(i => i.Id == itemId),
            "Serviço não encontrado neste agendamento.");

        var janela = agendamento.Janelas().First(j => j.Item.Id == itemId);

        if (req.ResponsavelId is { } novo)
        {
            await ValidarEscolhasAsync(
                new[]
                {
                    new AtribuicaoDeServico(
                        item.ItemCatalogoId, item.Nome, janela.Inicio, janela.Fim,
                        novo, string.Empty, Array.Empty<PessoaResumo>()),
                },
                janela.Inicio, new[] { item.ItemCatalogoId }, id, ct);
        }

        item.ResponsavelId = req.ResponsavelId;

        // Quem responde pelo atendimento é quem presta o primeiro serviço: trocar o
        // primeiro troca o dono, senão a agenda listaria o compromisso no nome errado.
        var primeiro = agendamento.Itens.OrderBy(i => i.Ordem).ThenBy(i => i.Id).First();
        agendamento.ResponsavelId = primeiro.ResponsavelId ?? agendamento.ResponsavelId;

        await _db.SaveChangesAsync(ct);

        var completo = await CarregarAsync(id, ct);
        return Ok(completo!.ParaDto());
    }

    private static IReadOnlyList<AtribuicaoDeServico> AplicarEscolhas(
        IReadOnlyList<AtribuicaoDeServico> automaticas,
        IReadOnlyList<long> itensIds,
        IReadOnlyList<long?>? escolhidos)
    {
        if (escolhidos is null || escolhidos.Count == 0)
        {
            return automaticas;
        }

        if (escolhidos.Count != itensIds.Count)
        {
            throw new RegraDeNegocioException(
                "A lista de responsáveis precisa ter um item para cada serviço.",
                "RESPONSAVEIS_INVALIDOS");
        }

        return automaticas
            .Select((a, i) => escolhidos[i] is { } escolhido
                ? a with { ResponsavelId = escolhido, ResponsavelNome = string.Empty }
                : a)
            .ToList();
    }

    /// <summary>
    /// Confere quem foi escolhido a dedo: tem de prestar aquele serviço e estar livre na
    /// janela dele. Sem isto, mandar um id qualquer furaria a regra pela porta dos fundos.
    /// </summary>
    private async Task ValidarEscolhasAsync(
        IReadOnlyList<AtribuicaoDeServico> atribuicoes,
        DateTimeOffset inicio,
        IReadOnlyList<long> itensIds,
        long? ignorarAgendamentoId,
        CancellationToken ct)
    {
        foreach (var atribuicao in atribuicoes)
        {
            // Checagem direta, não pela grade: a grade só tem horários nas fronteiras do
            // intervalo, e o segundo serviço começa quando o primeiro acaba. A janela do
            // atendimento inteiro vai junto porque é ela que vale quando a empresa conta
            // ocupação por funcionário.
            var pode = await _disponibilidade.PodePrestarAsync(
                atribuicao.ResponsavelId,
                atribuicao.ItemCatalogoId == 0 ? null : atribuicao.ItemCatalogoId,
                atribuicao.Inicio, atribuicao.Fim, ignorarAgendamentoId, ct,
                atribuicoes[0].Inicio, atribuicoes[^1].Fim);

            if (!pode)
            {
                var pessoa = await _db.Usuarios.AsNoTracking()
                    .Where(u => u.Id == atribuicao.ResponsavelId)
                    .Select(u => u.Nome)
                    .FirstOrDefaultAsync(ct) ?? "A pessoa escolhida";

                throw new RegraDeNegocioException(
                    $"{pessoa} não pode atender \"{atribuicao.Nome}\" nesse horário.",
                    "RESPONSAVEL_INDISPONIVEL");
            }
        }
    }

    private Task<Agendamento?> CarregarAsync(long id, CancellationToken ct) =>
        _db.Agendamentos
            .Include(a => a.Cliente)
            .Include(a => a.Responsavel)
            // Cada item traz quem o presta: é o nome que a tela mostra ao lado do serviço.
            .Include(a => a.Itens).ThenInclude(i => i.Responsavel)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
}

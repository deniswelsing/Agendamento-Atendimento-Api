using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Pacotes;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Infrastructure.Servicos;

/// <summary>
/// Um atendimento proposto para o cliente do pacote. Proposta, e não agendamento: o
/// time confere antes de marcar, porque a hora combinada nem sempre está livre e a
/// escolha de trocar de horário ou de pessoa é de quem atende o telefone.
/// </summary>
public sealed record PropostaDePacote(
    DateOnly Data,
    DateTimeOffset? Inicio,
    DateTimeOffset? Fim,
    long? ResponsavelId,
    string? ResponsavelNome,
    /// <summary>Quem mais poderia pegar esta data e hora. Vazio quando não há encaixe.</summary>
    IReadOnlyList<PessoaResumo> Candidatos,
    /// <summary>
    /// Falso quando o dia não tem encaixe nenhum. A proposta continua na lista, com a
    /// data e o motivo: sumir com ela esconderia que falta marcar aquela sessão.
    /// </summary>
    bool TemEncaixe,
    /// <summary>
    /// Quando o horário não é o combinado, diz isso. O cliente pediu quarta às 14h; se
    /// só há 15h, quem liga para ele precisa saber o que está oferecendo.
    /// </summary>
    string? Observacao);

/// <summary>
/// Monta a agenda de um cliente dentro de um pacote a partir da preferência dele —
/// "toda quarta às 14h" — e do saldo do ciclo.
///
/// A preferência é um ponto de partida, não uma promessa: o serviço procura o encaixe
/// mais próximo da hora combinada em cada semana, com quem presta e está livre. O dia em
/// que ninguém pode volta na lista sem horário, para o time resolver em vez de descobrir
/// a falta quando o cliente aparecer.
/// </summary>
public class PacoteAgendaService
{
    private readonly AppDbContext _db;
    private readonly DisponibilidadeService _disponibilidade;

    public PacoteAgendaService(AppDbContext db, DisponibilidadeService disponibilidade)
    {
        _db = db;
        _disponibilidade = disponibilidade;
    }

    /// <summary>
    /// As datas que a preferência gera: a próxima ocorrência do dia da semana a partir
    /// de `apartirDe`, e dali de sete em sete, uma por sessão do saldo.
    ///
    /// Sem dia da semana escolhido não há o que calcular — o pacote vira só saldo, e as
    /// datas saem de onde o time quiser.
    /// </summary>
    public static IReadOnlyList<DateOnly> DatasDaPreferencia(
        DayOfWeek? diaDaSemana, DateOnly apartirDe, int quantidade)
    {
        if (diaDaSemana is not { } dia || quantidade <= 0)
        {
            return Array.Empty<DateOnly>();
        }

        // Quantos dias até o próximo dia combinado. Zero quando `apartirDe` já é ele:
        // marcar para hoje é legítimo, e quem não quiser é só pedir a partir de amanhã.
        var passos = ((int)dia - (int)apartirDe.DayOfWeek + 7) % 7;
        var primeira = apartirDe.AddDays(passos);

        return Enumerable.Range(0, quantidade)
            .Select(i => primeira.AddDays(7 * i))
            .ToList();
    }

    /// <summary>
    /// As propostas para o saldo que resta do ciclo aberto deste cliente. Uma por sessão
    /// disponível, já descontando o que ele tem marcado.
    /// </summary>
    public async Task<IReadOnlyList<PropostaDePacote>> ProporAsync(
        long pacoteClienteId, DateOnly apartirDe, CancellationToken ct = default)
    {
        var vinculo = await _db.PacoteClientes.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == pacoteClienteId, ct);
        if (vinculo is null)
        {
            return Array.Empty<PropostaDePacote>();
        }

        var pacote = await _db.Pacotes.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == vinculo.PacoteId, ct);
        if (pacote is null)
        {
            return Array.Empty<PropostaDePacote>();
        }

        var ciclo = await _db.CiclosDePacote.AsNoTracking()
            .Where(c => c.PacoteClienteId == pacoteClienteId && !c.Encerrado)
            .OrderByDescending(c => c.Ciclo)
            .FirstOrDefaultAsync(ct);
        if (ciclo is null)
        {
            return Array.Empty<PropostaDePacote>();
        }

        // O que já está marcado neste ciclo conta duas vezes: desconta do que falta E
        // tira a data da lista. Sem tirar a data, a semana já marcada voltaria como
        // proposta e alguém marcaria o mesmo dia de novo.
        var marcados = await _db.Agendamentos.AsNoTracking()
            .Where(a => a.PacoteClienteId == pacoteClienteId
                        && a.PacoteCiclo == ciclo.Ciclo
                        && a.Status != StatusAgendamento.Cancelado)
            .Select(a => a.Inicio)
            .ToListAsync(ct);

        var ocupadas = marcados
            .Select(i => DateOnly.FromDateTime(i.UtcDateTime))
            .ToHashSet();

        var faltam = Math.Max(0, ciclo.Total - marcados.Count);
        if (faltam == 0)
        {
            return Array.Empty<PropostaDePacote>();
        }

        var itens = await _db.PacoteItens.AsNoTracking()
            .Where(i => i.PacoteId == pacote.Id)
            .Select(i => i.ItemCatalogoId)
            .ToListAsync(ct);

        var duracao = await _db.ItensCatalogo.AsNoTracking()
            .Where(i => itens.Contains(i.Id))
            .SumAsync(i => (int?)i.DuracaoMinutos, ct) ?? 0;
        if (duracao <= 0)
        {
            duracao = 30;
        }

        // Gera com folga e descarta as semanas já marcadas: pedir exatamente `faltam`
        // datas devolveria menos propostas do que sessões a marcar.
        //
        // O ciclo é o teto. Propor além do fim dele marcaria, dentro deste ciclo, uma
        // sessão que acontece no seguinte: na virada o saldo já teria sido contado como
        // usado aqui, e num pacote sem recorrência o cliente receberia o estorno das
        // sessões que sobraram com um atendimento marcado no futuro. O que não couber no
        // ciclo é justamente o que vira crédito — ou estorno — quando ele fecha.
        var datas = DatasDaPreferencia(vinculo.DiaDaSemana, apartirDe, faltam + ocupadas.Count)
            .Where(d => !ocupadas.Contains(d) && d <= ciclo.Fim)
            .Take(faltam)
            .ToList();
        if (datas.Count == 0)
        {
            return Array.Empty<PropostaDePacote>();
        }

        var propostas = new List<PropostaDePacote>();
        foreach (var data in datas)
        {
            propostas.Add(await PropostaDoDiaAsync(data, duracao, itens, vinculo, ct));
        }

        return propostas;
    }

    private async Task<PropostaDePacote> PropostaDoDiaAsync(
        DateOnly data,
        int duracao,
        IReadOnlyCollection<long> itens,
        PacoteCliente vinculo,
        CancellationToken ct)
    {
        var dia = await _disponibilidade.ObterDiaAsync(
            data, duracao, vinculo.ResponsavelPreferidoId, ct, itens);

        if (dia.Livres.Count == 0)
        {
            return new PropostaDePacote(
                data, null, null, null, null, Array.Empty<PessoaResumo>(), false,
                dia.MotivoSemEncaixe ?? dia.MotivoFechado
                ?? "Nenhum encaixe neste dia.");
        }

        // O encaixe mais perto da hora combinada. Sem hora combinada, o primeiro do dia.
        var alvo = vinculo.Hora;
        var escolhido = alvo is { } hora
            ? dia.Livres
                .OrderBy(s => Math.Abs(
                    (TimeOnly.FromDateTime(s.Inicio.UtcDateTime) - hora).Ticks))
                .First()
            : dia.Livres[0];

        var inicioEscolhido = TimeOnly.FromDateTime(escolhido.Inicio.UtcDateTime);
        var observacao = alvo is { } combinada && inicioEscolhido != combinada
            ? $"A hora combinada ({combinada:HH\\:mm}) não está livre; o mais perto é "
              + $"{inicioEscolhido:HH\\:mm}."
            : null;

        return new PropostaDePacote(
            data, escolhido.Inicio, escolhido.Fim,
            escolhido.ResponsavelId, escolhido.ResponsavelNome,
            escolhido.Atribuicoes.Count > 0
                ? escolhido.Atribuicoes[0].Candidatos
                : Array.Empty<PessoaResumo>(),
            true, observacao);
    }
}

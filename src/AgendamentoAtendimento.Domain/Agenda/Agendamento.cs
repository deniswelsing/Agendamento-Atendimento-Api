using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Common;
using AgendamentoAtendimento.Domain.Usuarios;

namespace AgendamentoAtendimento.Domain.Agenda;

public enum StatusAgendamento
{
    Agendado = 1,
    Confirmado = 2,
    EmAtendimento = 3,
    Concluido = 4,
    Cancelado = 5,
    NaoCompareceu = 6,

    /// <summary>
    /// Pedido feito pelo cliente na página pública, esperando alguém do time aprovar.
    /// Já segura o horário: soltá-lo deixaria dois clientes pedirem o mesmo encaixe.
    /// </summary>
    PendenteAprovacao = 7,
}

/// <summary>
/// Como a agenda decide que alguém está ocupado. A escolha é da empresa porque as duas
/// leituras são legítimas e nenhuma serve a todo mundo.
/// </summary>
public enum ModoDeOcupacao
{
    /// <summary>
    /// O serviço é a unidade: cada pessoa fica ocupada só na janela do serviço que
    /// presta. Quem presta apenas o segundo serviço continua livre durante o primeiro —
    /// é o que abre encaixe em atendimento que passa por mais de uma pessoa.
    /// </summary>
    PorServico = 1,

    /// <summary>
    /// O funcionário é a unidade: quem entra no atendimento fica ocupado do começo ao
    /// fim dele, inclusive nos serviços que não presta. Perde encaixe de propósito — é o
    /// que vale onde a pessoa acompanha o cliente o atendimento inteiro.
    /// </summary>
    PorFuncionario = 2,
}

/// <summary>Agendamento de atendimento para um cliente (pessoa ou empresa).</summary>
public class Agendamento : EntidadeDeTenant
{
    public long ClienteId { get; set; }
    public Cliente? Cliente { get; set; }

    public DateTimeOffset Inicio { get; set; }
    public DateTimeOffset Fim { get; set; }

    public StatusAgendamento Status { get; set; } = StatusAgendamento.Agendado;

    /// <summary>
    /// Quem responde pelo atendimento como um todo — na prática, quem presta o primeiro
    /// serviço. Cada serviço tem o seu em <see cref="AgendamentoItem.ResponsavelId"/>;
    /// este continua existindo porque é por ele que a agenda filtra e lista.
    /// </summary>
    public long? ResponsavelId { get; set; }
    public Usuario? Responsavel { get; set; }

    public string? Observacoes { get; set; }
    public string? LocalAtendimento { get; set; }
    public string? MotivoCancelamento { get; set; }

    public DateTimeOffset? IniciadoEm { get; set; }
    public DateTimeOffset? ConcluidoEm { get; set; }

    /// <summary>Venda gerada na conclusão do atendimento.</summary>
    public long? VendaId { get; set; }

    /// <summary>Quem marcou: o time pelo app, ou o próprio cliente na página pública.</summary>
    public OrigemAgendamento Origem { get; set; } = OrigemAgendamento.Interno;

    /// <summary>
    /// Código opaco entregue a quem marcou pela página. É com ele — e só com ele — que
    /// o cliente consulta e desmarca depois, sem ter conta no sistema.
    /// </summary>
    public string? CodigoPublico { get; set; }

    /// <summary>
    /// Quando o cliente confirmou presença. Nulo não quer dizer que ele não vem — quer
    /// dizer que ninguém perguntou, ou que ele ainda não respondeu. São coisas diferentes
    /// do status, que muda por decisão do time também.
    /// </summary>
    public DateTimeOffset? ConfirmadoEm { get; set; }

    public ICollection<AgendamentoItem> Itens { get; set; } = new List<AgendamentoItem>();

    public int DuracaoMinutos => (int)(Fim - Inicio).TotalMinutes;

    /// <summary>
    /// Os serviços na ordem em que acontecem, cada um já com a sua janela. Os serviços
    /// são sequenciais: o cliente faz um, depois o outro — por isso pessoas diferentes
    /// podem prestá-los sem conflito.
    /// </summary>
    public IEnumerable<(AgendamentoItem Item, DateTimeOffset Inicio, DateTimeOffset Fim)> Janelas()
    {
        var cursor = Inicio;
        foreach (var item in Itens.OrderBy(i => i.Ordem).ThenBy(i => i.Id))
        {
            var fim = cursor.AddMinutes(item.DuracaoMinutos * item.Quantidade);
            yield return (item, cursor, fim);
            cursor = fim;
        }
    }

    /// <summary>
    /// Quem está ocupado por causa deste agendamento, e quando.
    ///
    /// Em <see cref="ModoDeOcupacao.PorServico"/> é a janela do serviço — e não a do
    /// atendimento — que tira alguém da grade: quem presta só o segundo serviço continua
    /// livre durante o primeiro. Em <see cref="ModoDeOcupacao.PorFuncionario"/> todo
    /// mundo que aparece no atendimento fica preso a ele do início ao fim.
    /// </summary>
    public IEnumerable<(long UsuarioId, DateTimeOffset Inicio, DateTimeOffset Fim)> Ocupacoes(
        ModoDeOcupacao modo = ModoDeOcupacao.PorServico)
    {
        if (modo == ModoDeOcupacao.PorFuncionario)
        {
            // Cada pessoa uma vez só: repetir a mesma janela não muda o resultado e
            // engorda a grade à toa.
            var vistos = new HashSet<long>();
            foreach (var item in Itens)
            {
                if ((item.ResponsavelId ?? ResponsavelId) is { } quem && vistos.Add(quem))
                {
                    yield return (quem, Inicio, Fim);
                }
            }

            if (vistos.Count == 0 && ResponsavelId is { } unico)
            {
                yield return (unico, Inicio, Fim);
            }

            yield break;
        }

        var alguem = false;
        foreach (var (item, inicio, fim) in Janelas())
        {
            var responsavel = item.ResponsavelId ?? ResponsavelId;
            if (responsavel is null)
            {
                continue;
            }

            alguem = true;
            yield return (responsavel.Value, inicio, fim);
        }

        // Agendamento sem item algum (ou sem ninguém em nenhum item) ainda ocupa quem
        // responde por ele: o compromisso existe na agenda.
        if (!alguem && ResponsavelId is { } dono)
        {
            yield return (dono, Inicio, Fim);
        }
    }
}

public class AgendamentoItem : EntidadeDeTenant
{
    public long AgendamentoId { get; set; }
    public Agendamento? Agendamento { get; set; }

    public long ItemCatalogoId { get; set; }
    public ItemCatalogo? ItemCatalogo { get; set; }

    /// <summary>Cópia do nome e da duração no momento do agendamento.</summary>
    public required string Nome { get; set; }
    public int DuracaoMinutos { get; set; }
    public int Quantidade { get; set; } = 1;
    public decimal PrecoUnitario { get; set; }

    /// <summary>Posição na sequência. É o que define a janela de cada serviço.</summary>
    public int Ordem { get; set; }

    /// <summary>
    /// Quem presta **este** serviço. Nulo cai no responsável do agendamento — é o que
    /// mantém válido tudo que foi marcado antes desta regra existir.
    ///
    /// A comissão da venda segue esta pessoa, e não quem abriu o atendimento.
    /// </summary>
    public long? ResponsavelId { get; set; }
    public Usuario? Responsavel { get; set; }
}

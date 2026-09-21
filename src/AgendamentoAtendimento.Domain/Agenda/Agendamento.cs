using AgendamentoAtendimento.Domain.Vendas;
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

    /// <summary>
    /// O cliente-no-pacote que este atendimento consome. Nulo é atendimento avulso — a
    /// esmagadora maioria. É por aqui que o saldo do pacote sabe o que já foi usado.
    /// </summary>
    public long? PacoteClienteId { get; set; }

    /// <summary>
    /// De qual ciclo do pacote ele saiu. Sem isto, um atendimento remarcado para depois
    /// da virada descontaria do ciclo errado.
    /// </summary>
    public int? PacoteCiclo { get; set; }

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
    /// <param name="modo">Como a empresa conta ocupação.</param>
    /// <returns>
    /// `ItemCatalogoId` diz qual serviço gerou o bloqueio — é por ele que a grade
    /// reconhece uma turma e continua oferecendo o horário enquanto houver vaga. Nulo
    /// quando o bloqueio não vem de um serviço específico.
    /// </returns>
    public IEnumerable<(long UsuarioId, DateTimeOffset Inicio, DateTimeOffset Fim, long? ItemCatalogoId)>
        Ocupacoes(ModoDeOcupacao modo = ModoDeOcupacao.PorServico)
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
                    // A janela é a do atendimento inteiro, então ela não representa um
                    // serviço só: turma nesse modo vale apenas quando é o único serviço.
                    yield return (quem, Inicio, Fim,
                        Itens.Count == 1 ? item.ItemCatalogoId : null);
                }
            }

            if (vistos.Count == 0 && ResponsavelId is { } unico)
            {
                yield return (unico, Inicio, Fim, null);
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
            yield return (responsavel.Value, inicio, fim, item.ItemCatalogoId);
        }

        // Agendamento sem item algum (ou sem ninguém em nenhum item) ainda ocupa quem
        // responde por ele: o compromisso existe na agenda.
        if (!alguem && ResponsavelId is { } dono)
        {
            yield return (dono, Inicio, Fim, null);
        }
    }

    /// <summary>
    /// O próximo passo de cobrança deste atendimento, dado o status da venda ligada a
    /// ele (nulo quando não há venda). É a regra que decide se o cartão da agenda mostra
    /// "Receber pagamento", "Ver venda" ou nada.
    ///
    /// Venda cancelada devolve o atendimento à fila: o trabalho foi entregue e continua
    /// a receber. Tratá-la como venda existente deixaria o atendimento sem cobrança para
    /// sempre por causa de um cancelamento.
    /// </summary>
    public AcaoDeCobranca CobrancaDisponivel(StatusVenda? statusDaVenda)
    {
        // Sem serviço não há o que cobrar, em qualquer status.
        if (Itens.Count == 0)
        {
            return AcaoDeCobranca.Nenhuma;
        }

        if (VendaId is not null)
        {
            // Sem saber o status, só dá para olhar. Oferecer gerar aqui prometeria uma
            // venda que a Api recusa com ATENDIMENTO_JA_FATURADO — e quem toca só
            // descobre no erro.
            if (statusDaVenda is not { } status)
            {
                return AcaoDeCobranca.VerVenda;
            }

            if (status != StatusVenda.Cancelada)
            {
                return status == StatusVenda.Paga
                    ? AcaoDeCobranca.VerVenda
                    : AcaoDeCobranca.ReceberPagamento;
            }
        }

        // Faturar conclui o que ainda está em andamento — é o que o app faz — então o
        // atendimento em curso também já pode virar venda.
        return Status is StatusAgendamento.Concluido or StatusAgendamento.EmAtendimento
            ? AcaoDeCobranca.GerarVenda
            : AcaoDeCobranca.Nenhuma;
    }

    /// <summary>O texto do botão. Vem daqui para as três telas dizerem a mesma coisa.</summary>
    public static string RotuloDaCobranca(AcaoDeCobranca acao) => acao switch
    {
        AcaoDeCobranca.GerarVenda => "Receber pagamento",
        AcaoDeCobranca.ReceberPagamento => "Receber pagamento",
        AcaoDeCobranca.VerVenda => "Ver venda",
        _ => string.Empty,
    };
}

/// <summary>
/// O que a tela oferece como próximo passo de cobrança de um atendimento. Quem decide é
/// o servidor porque a resposta depende de três coisas que só ele tem juntas: o status
/// do atendimento, se ele já virou venda e em que pé essa venda está.
///
/// Existe como enum, e não como bool, porque "não dá para cobrar" e "já foi pago" levam
/// a botões diferentes — e um bool obrigaria a tela a adivinhar qual.
/// </summary>
public enum AcaoDeCobranca
{
    /// <summary>Nada a fazer: o atendimento não chegou lá, ou não tem o que cobrar.</summary>
    Nenhuma = 1,

    /// <summary>Ainda não virou venda. O toque cria a venda e abre o fechamento.</summary>
    GerarVenda = 2,

    /// <summary>Já tem venda com saldo. O toque leva ao fechamento dela.</summary>
    ReceberPagamento = 3,

    /// <summary>Venda quitada. Só há o que conferir.</summary>
    VerVenda = 4,
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

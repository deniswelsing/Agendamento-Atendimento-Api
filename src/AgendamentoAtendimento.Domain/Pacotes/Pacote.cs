using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Common;
using AgendamentoAtendimento.Domain.Usuarios;

namespace AgendamentoAtendimento.Domain.Pacotes;

/// <summary>
/// De quanto em quanto tempo o pacote se renova. `Nenhuma` é o pacote avulso: acaba
/// quando as sessões acabam, e o que sobrou é estornado em vez de virar crédito.
/// </summary>
public enum RecorrenciaDePacote
{
    Nenhuma = 1,
    Semanal = 2,
    Quinzenal = 3,
    Mensal = 4,
    Trimestral = 5,
    Semestral = 6,
    Anual = 7,
}

public enum StatusDePacote
{
    Ativo = 1,

    /// <summary>Chegou ao fim: as sessões acabaram e não há próxima recorrência.</summary>
    Encerrado = 2,

    /// <summary>Interrompido antes da hora. O que não foi usado vira estorno.</summary>
    Cancelado = 3,
}

/// <summary>
/// Um pacote de prateleira: nome, quantas sessões dá e quais serviços cobre. Existe para
/// não remontar o mesmo combinado a cada cliente — quem quiser algo diferente monta um
/// pacote personalizado, que é o mesmo objeto sem modelo por trás.
/// </summary>
public class PacoteModelo : EntidadeDeTenant
{
    public required string Nome { get; set; }
    public string? Descricao { get; set; }

    /// <summary>Quantos atendimentos o pacote dá por ciclo.</summary>
    public int Quantidade { get; set; }

    /// <summary>O que o cliente paga pelo ciclo inteiro, adiantado.</summary>
    public decimal Preco { get; set; }

    public RecorrenciaDePacote Recorrencia { get; set; } = RecorrenciaDePacote.Nenhuma;

    public bool Ativo { get; set; } = true;

    public ICollection<PacoteModeloItem> Itens { get; set; } = new List<PacoteModeloItem>();
}

/// <summary>Um serviço coberto pelo modelo de pacote.</summary>
public class PacoteModeloItem : EntidadeDeTenant
{
    public long PacoteModeloId { get; set; }
    public PacoteModelo? PacoteModelo { get; set; }

    public long ItemCatalogoId { get; set; }
    public ItemCatalogo? ItemCatalogo { get; set; }
}

/// <summary>
/// Um pacote vendido. Nasce de um modelo ou montado à mão para o cliente — daí
/// <see cref="PacoteModeloId"/> ser opcional.
///
/// Um pacote cobre VÁRIOS clientes (uma família, uma equipe), mas cada cliente só pode
/// estar em um pacote ativo: duas bolsas de sessões para a mesma pessoa não teriam como
/// decidir de qual delas sai o atendimento de hoje.
/// </summary>
public class Pacote : EntidadeDeTenant
{
    public long? PacoteModeloId { get; set; }
    public PacoteModelo? PacoteModelo { get; set; }

    public required string Nome { get; set; }

    /// <summary>Quantos atendimentos CADA cliente do pacote tem por ciclo.</summary>
    public int QuantidadePorCliente { get; set; }

    /// <summary>O que cada cliente paga pelo ciclo, adiantado.</summary>
    public decimal PrecoPorCliente { get; set; }

    public RecorrenciaDePacote Recorrencia { get; set; } = RecorrenciaDePacote.Nenhuma;

    public StatusDePacote Status { get; set; } = StatusDePacote.Ativo;

    /// <summary>Em que ciclo o pacote está. O primeiro é 1.</summary>
    public int CicloAtual { get; set; } = 1;

    public DateOnly InicioDoCicloAtual { get; set; }

    /// <summary>
    /// Último dia do ciclo. Num pacote sem recorrência ele ainda existe: é o prazo para
    /// usar as sessões, e o que sobrar depois dele é estornado.
    /// </summary>
    public DateOnly FimDoCicloAtual { get; set; }

    /// <summary>
    /// Quando o time foi avisado de que este ciclo está para vencer. Existe para o job
    /// diário não avisar o mesmo pacote sete dias seguidos.
    /// </summary>
    public DateTimeOffset? AvisadoEm { get; set; }

    /// <summary>De qual ciclo foi o aviso. Sem isto, renovar não liberaria o próximo.</summary>
    public int? AvisoDoCiclo { get; set; }

    public ICollection<PacoteItem> Itens { get; set; } = new List<PacoteItem>();
    public ICollection<PacoteCliente> Clientes { get; set; } = new List<PacoteCliente>();

    public bool EhRecorrente => Recorrencia != RecorrenciaDePacote.Nenhuma;

    /// <summary>O que uma sessão vale. É a conta do estorno e do crédito.</summary>
    public decimal ValorPorAtendimento => QuantidadePorCliente <= 0
        ? 0m
        : decimal.Round(PrecoPorCliente / QuantidadePorCliente, 2, MidpointRounding.AwayFromZero);

    /// <summary>Quantos dias dura um ciclo desta recorrência.</summary>
    public static int DiasDoCiclo(RecorrenciaDePacote recorrencia) => recorrencia switch
    {
        RecorrenciaDePacote.Semanal => 7,
        RecorrenciaDePacote.Quinzenal => 14,
        RecorrenciaDePacote.Mensal => 30,
        RecorrenciaDePacote.Trimestral => 90,
        RecorrenciaDePacote.Semestral => 180,
        RecorrenciaDePacote.Anual => 365,
        _ => 30,
    };

    /// <summary>
    /// Quando o ciclo vence, contado do início. Um pacote sem recorrência também vence:
    /// é o prazo para usar o que foi pago.
    /// </summary>
    public DateOnly FimCalculado(DateOnly inicio) =>
        inicio.AddDays(DiasDoCiclo(Recorrencia) - 1);

    /// <summary>
    /// Quantos dias faltam para o ciclo vencer. Negativo quando já venceu — e é isso que
    /// o job usa para separar "avisar" de "virar o ciclo".
    /// </summary>
    public int DiasAteVencer(DateOnly hoje) => FimDoCicloAtual.DayNumber - hoje.DayNumber;
}

/// <summary>Um serviço coberto pelo pacote vendido.</summary>
public class PacoteItem : EntidadeDeTenant
{
    public long PacoteId { get; set; }
    public Pacote? Pacote { get; set; }

    public long ItemCatalogoId { get; set; }
    public ItemCatalogo? ItemCatalogo { get; set; }
}

/// <summary>
/// Um cliente dentro de um pacote, com a preferência de horário dele e o saldo de cada
/// ciclo. A preferência é o que permite montar a agenda inteira de uma vez: "toda
/// quarta às 14h" vira as N datas, e o servidor procura quem atende livre em cada uma.
/// </summary>
public class PacoteCliente : EntidadeDeTenant
{
    public long PacoteId { get; set; }
    public Pacote? Pacote { get; set; }

    public long ClienteId { get; set; }
    public Cliente? Cliente { get; set; }

    /// <summary>
    /// O dia da semana combinado. Nulo quando o cliente não tem dia fixo — aí as datas
    /// saem de outro lugar e o pacote só controla o saldo.
    /// </summary>
    public DayOfWeek? DiaDaSemana { get; set; }

    /// <summary>A hora combinada naquele dia.</summary>
    public TimeOnly? Hora { get; set; }

    /// <summary>
    /// Com quem o cliente prefere ser atendido. Nulo deixa o servidor escolher entre
    /// quem presta o serviço e está livre — que é o caso comum.
    /// </summary>
    public long? ResponsavelPreferidoId { get; set; }
    public Usuario? ResponsavelPreferido { get; set; }

    public bool Ativo { get; set; } = true;

    public ICollection<CicloDoCliente> Ciclos { get; set; } = new List<CicloDoCliente>();
}

/// <summary>
/// O saldo de um cliente num ciclo do pacote. É uma linha por ciclo, e não um contador
/// que se sobrescreve, porque a regra do crédito olha para trás: o que não foi usado num
/// ciclo desconta do seguinte, e isso precisa continuar verificável depois.
/// </summary>
public class CicloDoCliente : EntidadeDeTenant
{
    public long PacoteClienteId { get; set; }
    public PacoteCliente? PacoteCliente { get; set; }

    /// <summary>O número do ciclo no pacote. O primeiro é 1.</summary>
    public int Ciclo { get; set; }

    public DateOnly Inicio { get; set; }
    public DateOnly Fim { get; set; }

    /// <summary>Quantos atendimentos este ciclo comprou.</summary>
    public int QuantidadeContratada { get; set; }

    /// <summary>
    /// O que sobrou do ciclo anterior e veio para cá. É o "desconta a quantidade que ele
    /// não foi na recorrência anterior": o cliente não perde o que pagou e não usou.
    /// </summary>
    public int CreditoRecebido { get; set; }

    /// <summary>Atendimentos deste ciclo que aconteceram.</summary>
    public int QuantidadeUsada { get; set; }

    public bool Encerrado { get; set; }
    public DateTimeOffset? EncerradoEm { get; set; }

    /// <summary>O que sobrou daqui e foi para o ciclo seguinte.</summary>
    public int CreditoCedido { get; set; }

    /// <summary>
    /// O que sobrou e NÃO tinha ciclo seguinte: vira dinheiro de volta. Fica registrado
    /// em quantidade e em valor porque o preço da sessão pode mudar entre um pacote e
    /// outro, e o estorno é do que foi pago.
    /// </summary>
    public int EstornoQuantidade { get; set; }
    public decimal EstornoValor { get; set; }

    /// <summary>Quantas sessões este ciclo dá ao todo, com o crédito que veio de trás.</summary>
    public int Total => QuantidadeContratada + CreditoRecebido;

    /// <summary>Quantas ainda cabem. Nunca negativo: gastar além do saldo é outro erro.</summary>
    public int Disponivel => Math.Max(0, Total - QuantidadeUsada);

    /// <summary>
    /// Fecha o ciclo e decide o destino do que não foi usado.
    ///
    /// Há próximo ciclo: o saldo vira crédito e desce para ele — o cliente pagou e não
    /// perde. É o "na próxima recorrência desconta a quantidade que ele não foi".
    ///
    /// Não há: vira estorno, em quantidade e em valor. É o fim da linha, e o que sobrou
    /// tem de voltar para quem pagou em vez de evaporar.
    ///
    /// Fechar duas vezes não dobra nada: um ciclo já encerrado sai daqui intacto.
    /// </summary>
    public void Encerrar(bool haProximoCiclo, decimal valorPorAtendimento, DateTimeOffset agora)
    {
        if (Encerrado)
        {
            return;
        }

        var naoUsadas = Disponivel;

        if (haProximoCiclo)
        {
            CreditoCedido = naoUsadas;
            EstornoQuantidade = 0;
            EstornoValor = 0m;
        }
        else
        {
            CreditoCedido = 0;
            EstornoQuantidade = naoUsadas;
            EstornoValor = decimal.Round(
                naoUsadas * valorPorAtendimento, 2, MidpointRounding.AwayFromZero);
        }

        Encerrado = true;
        EncerradoEm = agora;
    }
}

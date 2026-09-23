using AgendamentoAtendimento.Domain.Common;

namespace AgendamentoAtendimento.Domain.Vendas;

public enum StatusPagamento
{
    Pendente = 1,
    Confirmado = 2,
    Recusado = 3,
    Estornado = 4,
}

/// <summary>
/// Forma de pagamento aceita pelo tenant. As taxas ficam aqui para que a venda registre o
/// líquido real — é o que o PetShop.Route faz com maquininha de cartão.
/// </summary>
public class FormaPagamento : EntidadeDeTenant
{
    public required string Nome { get; set; }

    /// <summary>DINHEIRO, PIX, CREDITO, DEBITO, BOLETO, TRANSFERENCIA, OUTRO.</summary>
    public required string Codigo { get; set; }

    public bool Ativa { get; set; } = true;
    public bool PermiteParcelamento { get; set; }
    public int MaximoParcelas { get; set; } = 1;

    /// <summary>Taxa cobrada pela adquirente, descontada do valor líquido.</summary>
    public decimal TaxaPercentual { get; set; }
    public decimal TaxaFixa { get; set; }

    /// <summary>Dias até o dinheiro cair na conta.</summary>
    public int DiasParaLiquidacao { get; set; }
}

/// <summary>Recebimento (ou parcela) de uma venda.</summary>
public class Pagamento : EntidadeDeTenant
{
    public long VendaId { get; set; }
    public Venda? Venda { get; set; }

    public long FormaPagamentoId { get; set; }
    public FormaPagamento? FormaPagamento { get; set; }

    public StatusPagamento Status { get; set; } = StatusPagamento.Pendente;

    public decimal Valor { get; set; }

    /// <summary>
    /// Taxa considerada verdadeira hoje. Começa igual à estimada e é corrigida quando a
    /// adquirente informa a real. É esta que entra no líquido.
    /// </summary>
    public decimal ValorTaxa { get; set; }

    /// <summary>
    /// O que a alíquota configurada previa, congelado no lançamento. Guardar as duas é o
    /// que permite ver que a adquirente cobrou diferente do combinado.
    /// </summary>
    public decimal ValorTaxaEstimada { get; set; }

    /// <summary>
    /// true quando a taxa veio da adquirente, e não da alíquota configurada. Enquanto for
    /// false, o líquido é uma previsão — não um fato.
    /// </summary>
    public bool TaxaConferida { get; set; }

    public decimal ValorLiquido { get; set; }

    /// <summary>Diferença entre o que a adquirente cobrou e o que a configuração previa.</summary>
    public decimal DivergenciaDaTaxa => TaxaConferida ? ValorTaxa - ValorTaxaEstimada : 0m;

    public int Parcela { get; set; } = 1;
    public int TotalParcelas { get; set; } = 1;

    public DateTimeOffset? ConfirmadoEm { get; set; }
    public DateOnly? PrevisaoLiquidacao { get; set; }

    /// <summary>
    /// Como o dinheiro foi capturado. `Manual` quer dizer que o sistema não viu a
    /// transação: alguém passou o cartão numa maquininha de fora e digitou o valor.
    /// </summary>
    public MeioDeCaptura Meio { get; set; } = MeioDeCaptura.Manual;

    /// <summary>Código de autorização. Digitado à mão na captura manual.</summary>
    public string? Autorizacao { get; set; }

    // Identificação da transação na adquirente — é por aqui que se concilia o extrato.
    public string? Nsu { get; set; }
    public string? Bandeira { get; set; }
    public string? UltimosDigitos { get; set; }
    public string? AdquirenteChave { get; set; }

    /// <summary>Quando a taxa real foi confirmada contra a adquirente.</summary>
    public DateTimeOffset? ConciliadoEm { get; set; }

    public string? Observacao { get; set; }

    /// <summary>
    /// Quando o recebimento foi estornado (status <see cref="StatusPagamento.Estornado"/>).
    /// O lançamento não é apagado: o estorno é história, e o caixa precisa dela.
    /// </summary>
    public DateTimeOffset? EstornadoEm { get; set; }

    public string? MotivoEstorno { get; set; }

    public bool Estornado => Status == StatusPagamento.Estornado;
}

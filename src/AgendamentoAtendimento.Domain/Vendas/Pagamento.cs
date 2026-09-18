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
    public decimal ValorTaxa { get; set; }
    public decimal ValorLiquido { get; set; }

    public int Parcela { get; set; } = 1;
    public int TotalParcelas { get; set; } = 1;

    public DateTimeOffset? ConfirmadoEm { get; set; }
    public DateOnly? PrevisaoLiquidacao { get; set; }
    public string? Autorizacao { get; set; }
    public string? Observacao { get; set; }
}

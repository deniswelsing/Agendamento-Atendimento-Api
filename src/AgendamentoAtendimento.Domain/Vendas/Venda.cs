using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Common;

namespace AgendamentoAtendimento.Domain.Vendas;

public enum StatusVenda
{
    Aberta = 1,
    AguardandoPagamento = 2,
    Paga = 3,
    Cancelada = 4,
    Estornada = 5,
}

/// <summary>Venda de produtos e/ou serviços para um cliente.</summary>
public class Venda : EntidadeDeTenant
{
    public long ClienteId { get; set; }
    public Cliente? Cliente { get; set; }

    public long? AgendamentoId { get; set; }
    public Agendamento? Agendamento { get; set; }

    public StatusVenda Status { get; set; } = StatusVenda.Aberta;

    public decimal TotalBruto { get; set; }
    public decimal TotalDescontos { get; set; }
    public decimal TotalLiquido { get; set; }
    public decimal DescontoGeral { get; set; }
    public decimal TotalImpostos { get; set; }
    public decimal TotalPago { get; set; }
    public decimal TotalEstornado { get; set; }

    public string? Observacao { get; set; }
    public DateTimeOffset? FinalizadaEm { get; set; }
    public DateTimeOffset? CanceladaEm { get; set; }

    public ICollection<VendaItem> Itens { get; set; } = new List<VendaItem>();
    public ICollection<Pagamento> Pagamentos { get; set; } = new List<Pagamento>();

    public decimal SaldoAberto => Math.Max(0m, TotalLiquido - TotalPago);
}

public class VendaItem : EntidadeDeTenant
{
    public long VendaId { get; set; }
    public Venda? Venda { get; set; }

    public long ItemCatalogoId { get; set; }
    public ItemCatalogo? ItemCatalogo { get; set; }

    public TipoItem Tipo { get; set; }

    /// <summary>Nome e preço congelados no momento da venda.</summary>
    public required string Nome { get; set; }

    public decimal Quantidade { get; set; } = 1m;
    public decimal PrecoUnitario { get; set; }
    public decimal DescontoValor { get; set; }
    public decimal TaxaPercentual { get; set; }

    public decimal TotalBruto => decimal.Round(PrecoUnitario * Quantidade, 2, MidpointRounding.AwayFromZero);

    public decimal TotalLiquido =>
        decimal.Round(Math.Max(0m, TotalBruto - DescontoValor), 2, MidpointRounding.AwayFromZero);
}

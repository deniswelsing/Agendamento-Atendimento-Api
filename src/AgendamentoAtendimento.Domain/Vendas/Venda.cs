using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Common;
using AgendamentoAtendimento.Domain.Usuarios;

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

    /// <summary>
    /// Quem leva a comissão. Numa venda que veio de atendimento, é quem atendeu; num
    /// balcão, quem vendeu. Nulo quando ninguém é creditado.
    /// </summary>
    public long? VendedorId { get; set; }
    public Usuario? Vendedor { get; set; }

    public string? Observacao { get; set; }
    public DateTimeOffset? FinalizadaEm { get; set; }
    public DateTimeOffset? CanceladaEm { get; set; }

    public ICollection<VendaItem> Itens { get; set; } = new List<VendaItem>();
    public ICollection<Pagamento> Pagamentos { get; set; } = new List<Pagamento>();

    public decimal SaldoAberto => Math.Max(0m, TotalLiquido - TotalPago);

    /// <summary>
    /// O que a venda gera de comissão. Só faz sentido com vendedor: comissão sem alguém
    /// para receber é número solto.
    /// </summary>
    /// <summary>
    /// O que a empresa paga de comissão nesta venda. Conta item a item, porque cada um
    /// pode ter ido para uma pessoa diferente — item sem ninguém não gera comissão.
    /// </summary>
    public decimal TotalComissao => decimal.Round(
        Itens.Where(i => (i.VendedorId ?? VendedorId) is not null).Sum(i => i.ComissaoValor),
        2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Quanto cada pessoa leva. É o que o financeiro precisa quando dois funcionários
    /// atenderam o mesmo cliente — somar tudo num nome só pagaria a pessoa errada.
    /// </summary>
    public IReadOnlyList<(long VendedorId, decimal Valor)> ComissoesPorVendedor() =>
        Itens
            .Select(i => (Vendedor: i.VendedorId ?? VendedorId, i.ComissaoValor))
            .Where(x => x.Vendedor is not null && x.ComissaoValor > 0)
            .GroupBy(x => x.Vendedor!.Value)
            .Select(g => (
                VendedorId: g.Key,
                Valor: decimal.Round(g.Sum(x => x.ComissaoValor), 2, MidpointRounding.AwayFromZero)))
            .OrderBy(x => x.VendedorId)
            .ToList();
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

    /// <summary>
    /// Percentual de comissão congelado no momento da venda, copiado do item do catálogo.
    /// Congelar importa: mudar a comissão do catálogo amanhã não pode alterar o que já
    /// foi vendido e prometido a quem atendeu.
    /// </summary>
    public decimal ComissaoPercentual { get; set; }

    /// <summary>
    /// Quem leva a comissão **deste** item. Nulo cai no vendedor da venda.
    ///
    /// Existe porque um atendimento pode ter serviços prestados por pessoas diferentes,
    /// e a comissão tem de seguir quem prestou — não quem abriu a venda.
    /// </summary>
    public long? VendedorId { get; set; }
    public Usuario? Vendedor { get; set; }

    public decimal TotalBruto => decimal.Round(PrecoUnitario * Quantidade, 2, MidpointRounding.AwayFromZero);

    public decimal TotalLiquido =>
        decimal.Round(Math.Max(0m, TotalBruto - DescontoValor), 2, MidpointRounding.AwayFromZero);

    /// <summary>Comissão deste item, sobre o líquido — desconto dado reduz a comissão.</summary>
    public decimal ComissaoValor =>
        decimal.Round(TotalLiquido * ComissaoPercentual / 100m, 2, MidpointRounding.AwayFromZero);
}

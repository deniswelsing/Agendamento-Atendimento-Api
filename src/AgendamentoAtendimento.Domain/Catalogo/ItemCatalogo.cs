using AgendamentoAtendimento.Domain.Common;

namespace AgendamentoAtendimento.Domain.Catalogo;

public enum TipoItem
{
    Servico = 1,
    Produto = 2,
}

/// <summary>
/// Catálogo unificado: produtos e serviços na mesma tabela, porque a venda mistura os dois
/// e o agendamento só consome os serviços.
/// </summary>
public class ItemCatalogo : EntidadeDeTenant
{
    public TipoItem Tipo { get; set; } = TipoItem.Servico;
    public required string Nome { get; set; }
    public string? Descricao { get; set; }
    public string? Categoria { get; set; }

    public decimal Preco { get; set; }
    public decimal Custo { get; set; }

    /// <summary>Duração do serviço, usada pelo motor de disponibilidade.</summary>
    public int? DuracaoMinutos { get; set; }

    /// <summary>Saldo do produto. Serviços ficam nulos.</summary>
    public int? Estoque { get; set; }

    public string? CodigoDeBarras { get; set; }
    public string? ImagemUrl { get; set; }
    public bool Ativo { get; set; } = true;

    /// <summary>Percentual de comissão pago ao atendente.</summary>
    public decimal ComissaoPercentual { get; set; }

    /// <summary>Percentual de imposto aplicado na venda.</summary>
    public decimal TaxaPercentual { get; set; }

    public bool Agendavel => Tipo == TipoItem.Servico;
}

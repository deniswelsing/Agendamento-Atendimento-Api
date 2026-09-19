using AgendamentoAtendimento.Domain.Common;
using AgendamentoAtendimento.Domain.Usuarios;

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

/// <summary>
/// Quem pode prestar um serviço. Sem nenhuma linha para um serviço, **qualquer atendente
/// pode** — é o padrão, e é o que mantém os serviços que já existiam agendáveis.
///
/// Assim que alguém é marcado, a lista passa a ser fechada: a agenda deixa de oferecer
/// encaixe com quem não sabe fazer aquilo.
/// </summary>
public class ExecutorDeServico : EntidadeDeTenant
{
    public long ItemCatalogoId { get; set; }
    public ItemCatalogo? ItemCatalogo { get; set; }

    public long UsuarioId { get; set; }
    public Usuario? Usuario { get; set; }
}

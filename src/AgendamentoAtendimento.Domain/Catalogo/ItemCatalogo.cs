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

    /// <summary>
    /// Aparece na página pública. Serviço interno (retorno, cortesia, avaliação de
    /// garantia) continua no catálogo sem ser oferecido a quem chega de fora.
    /// </summary>
    public bool VisivelOnline { get; set; } = true;

    /// <summary>
    /// Quantas pessoas cabem na mesma sessão. 1 é atendimento individual — o padrão, e o
    /// que todo serviço já era. Acima disso é turma: o horário continua sendo oferecido
    /// enquanto houver vaga, em vez de sumir no primeiro inscrito.
    /// </summary>
    public int CapacidadeTurma { get; set; } = 1;

    /// <summary>Turma é o serviço que aceita mais de uma pessoa na mesma sessão.</summary>
    public bool EhTurma => Tipo == TipoItem.Servico && CapacidadeTurma > 1;

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

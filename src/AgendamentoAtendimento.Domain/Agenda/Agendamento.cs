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
}

/// <summary>Agendamento de atendimento para um cliente (pessoa ou empresa).</summary>
public class Agendamento : EntidadeDeTenant
{
    public long ClienteId { get; set; }
    public Cliente? Cliente { get; set; }

    public DateTimeOffset Inicio { get; set; }
    public DateTimeOffset Fim { get; set; }

    public StatusAgendamento Status { get; set; } = StatusAgendamento.Agendado;

    public long? ResponsavelId { get; set; }
    public Usuario? Responsavel { get; set; }

    public string? Observacoes { get; set; }
    public string? LocalAtendimento { get; set; }
    public string? MotivoCancelamento { get; set; }

    public DateTimeOffset? IniciadoEm { get; set; }
    public DateTimeOffset? ConcluidoEm { get; set; }

    /// <summary>Venda gerada na conclusão do atendimento.</summary>
    public long? VendaId { get; set; }

    public ICollection<AgendamentoItem> Itens { get; set; } = new List<AgendamentoItem>();

    public int DuracaoMinutos => (int)(Fim - Inicio).TotalMinutes;
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
}

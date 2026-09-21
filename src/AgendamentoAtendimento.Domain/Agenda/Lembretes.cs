using AgendamentoAtendimento.Domain.Common;

namespace AgendamentoAtendimento.Domain.Agenda;

/// <summary>Por onde o aviso sai. Hoje só e-mail; SMS e WhatsApp são do plano maior.</summary>
public enum CanalDeLembrete
{
    Email = 1,
}

/// <summary>Qual aviso é este. Os dois saem do mesmo motor, em momentos diferentes.</summary>
public enum TipoDeLembrete
{
    /// <summary>Sai logo que o agendamento é marcado: "está marcado para tal dia".</summary>
    Confirmacao = 1,

    /// <summary>Sai N horas antes do atendimento.</summary>
    Lembrete = 2,
}

public enum StatusDeLembrete
{
    /// <summary>Na fila, esperando a hora.</summary>
    Pendente = 1,
    Enviado = 2,

    /// <summary>O agendamento mudou de hora ou foi cancelado: este aviso não vale mais.</summary>
    Cancelado = 3,

    /// <summary>O canal recusou. Fica registrado para não sumir em silêncio.</summary>
    Falhou = 4,
}

/// <summary>
/// Quando e como a empresa avisa o cliente. Nasce desligado: mandar e-mail em nome de
/// alguém que não pediu é coisa que não se faz por padrão.
/// </summary>
public class ConfiguracaoDeLembrete : EntidadeDeTenant
{
    public bool Ativo { get; set; }

    /// <summary>Quantas horas antes do atendimento o lembrete sai.</summary>
    public int HorasDeAntecedencia { get; set; } = 24;

    /// <summary>Um aviso na hora de marcar, além do lembrete que sai depois.</summary>
    public bool AvisarAoMarcar { get; set; } = true;

    /// <summary>
    /// O lembrete traz o link de confirmar presença. Sem isso ele só informa — e a agenda
    /// continua sem saber quem vem.
    /// </summary>
    public bool PedirConfirmacao { get; set; } = true;

    public CanalDeLembrete Canal { get; set; } = CanalDeLembrete.Email;

    /// <summary>
    /// Limite de quanto tempo depois da hora um aviso atrasado ainda vale. Um lembrete de
    /// "amanhã às 9h" entregue depois das 9h não é lembrete, é confusão.
    /// </summary>
    public int ToleranciaDeAtrasoMinutos { get; set; } = 120;
}

/// <summary>
/// Um aviso na fila. É registro, não cálculo: sem guardar o que já saiu, remarcar um
/// agendamento mandaria o mesmo lembrete de novo, e ninguém saberia dizer se o cliente
/// foi avisado.
/// </summary>
public class LembreteDeAgendamento : EntidadeDeTenant
{
    public long AgendamentoId { get; set; }
    public Agendamento? Agendamento { get; set; }

    public TipoDeLembrete Tipo { get; set; }
    public CanalDeLembrete Canal { get; set; } = CanalDeLembrete.Email;

    /// <summary>A hora a partir da qual este aviso pode sair.</summary>
    public DateTimeOffset QuandoEnviar { get; set; }

    public DateTimeOffset? EnviadoEm { get; set; }
    public StatusDeLembrete Status { get; set; } = StatusDeLembrete.Pendente;

    /// <summary>
    /// Para onde foi — copiado na hora de agendar o aviso. O cliente pode trocar de
    /// e-mail depois, e o histórico tem de continuar dizendo para onde aquilo saiu.
    /// </summary>
    public required string Destino { get; set; }

    /// <summary>Preenchido quando o canal recusa. É o que a tela mostra em vez de sumir.</summary>
    public string? Erro { get; set; }

    public int Tentativas { get; set; }
}

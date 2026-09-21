using AgendamentoAtendimento.Domain.Common;
using AgendamentoAtendimento.Domain.Usuarios;

namespace AgendamentoAtendimento.Domain.Agenda;

/// <summary>
/// Janela de funcionamento da empresa em um dia da semana, com pausa — mesmo modelo do
/// `HorarioFuncionamento` do PetShop.Route.
/// </summary>
public class HorarioFuncionamento : EntidadeDeTenant
{
    public DayOfWeek DiaDaSemana { get; set; }
    public TimeOnly? Abertura { get; set; }
    public TimeOnly? Fechamento { get; set; }

    /// <summary>Pausa padrão da empresa (almoço). Nenhum encaixe é oferecido dentro dela.</summary>
    public TimeOnly? PausaInicio { get; set; }
    public TimeOnly? PausaFim { get; set; }

    /// <summary>Normal, Feriado, Especial.</summary>
    public string TipoDia { get; set; } = "Normal";

    public bool Aberto { get; set; } = true;

    /// <summary>Granularidade dos encaixes oferecidos na agenda, em minutos.</summary>
    public int IntervaloSlotMinutos { get; set; } = 30;
}

/// <summary>Data específica que sobrepõe o horário padrão (feriado, recesso, evento).</summary>
public class ExcecaoHorarioFuncionamento : EntidadeDeTenant
{
    public DateOnly Data { get; set; }
    public bool Fechado { get; set; } = true;
    public TimeOnly? Abertura { get; set; }
    public TimeOnly? Fechamento { get; set; }
    public TimeOnly? PausaInicio { get; set; }
    public TimeOnly? PausaFim { get; set; }
    public string? Motivo { get; set; }
}

/// <summary>
/// Jornada de um atendente. O que vale na agenda é sempre a interseção desta janela com a
/// da empresa, menos as pausas e menos o que já está agendado.
/// </summary>
public class HorarioStaff : EntidadeDeTenant
{
    public long UsuarioId { get; set; }
    public Usuario? Usuario { get; set; }

    public DayOfWeek DiaDaSemana { get; set; }

    /// <summary>
    /// O turno deste dia, quando a pessoa segue escala. Nulo = horário aberto, com a
    /// janela própria logo abaixo. Quando há turno, é dele que saem os horários — copiar
    /// os minutos para cá deixaria a escala desatualizada no dia em que o turno mudasse.
    /// </summary>
    public long? TurnoId { get; set; }
    public Turno? Turno { get; set; }

    public TimeOnly Inicio { get; set; }
    public TimeOnly Fim { get; set; }

    public TimeOnly? PausaInicio { get; set; }
    public TimeOnly? PausaFim { get; set; }

    /// <summary>Falso = folga fixa nesse dia.</summary>
    public bool Trabalha { get; set; } = true;

    /// <summary>A janela que vale hoje: a do turno, quando há um; a própria, quando não.</summary>
    public TimeOnly InicioEfetivo => Turno?.Inicio ?? Inicio;
    public TimeOnly FimEfetivo => Turno?.Fim ?? Fim;
    public TimeOnly? PausaInicioEfetiva => Turno is null ? PausaInicio : Turno.PausaInicio;
    public TimeOnly? PausaFimEfetiva => Turno is null ? PausaFim : Turno.PausaFim;
}

/// <summary>Ausência pontual de um atendente (férias, atestado, compromisso).</summary>
public class ExcecaoHorarioStaff : EntidadeDeTenant
{
    public long UsuarioId { get; set; }
    public Usuario? Usuario { get; set; }

    public DateOnly Data { get; set; }
    public bool DiaInteiro { get; set; } = true;
    public TimeOnly? Inicio { get; set; }
    public TimeOnly? Fim { get; set; }
    public string? Motivo { get; set; }
}

using AgendamentoAtendimento.Domain.Common;

namespace AgendamentoAtendimento.Domain.Agenda;

/// <summary>
/// Um turno nomeado — "Manhã", "Tarde", "Fechamento". Existe para não redigitar o mesmo
/// horário em cada pessoa e cada dia: mudar o turno muda a escala inteira de quem o usa.
///
/// Turno é opcional de propósito. Quem trabalha em escala fixa usa turno; quem tem
/// horário próprio continua com a janela livre. Obrigar turno faria a agenda mentir
/// sobre quem atende fora de escala.
/// </summary>
public class Turno : EntidadeDeTenant
{
    public required string Nome { get; set; }

    public TimeOnly Inicio { get; set; }
    public TimeOnly Fim { get; set; }

    /// <summary>Intervalo dentro do turno (almoço, janta). Nenhum encaixe cai dentro.</summary>
    public TimeOnly? PausaInicio { get; set; }
    public TimeOnly? PausaFim { get; set; }

    /// <summary>Cor para a escala ficar legível de relance. Hex, sem alfa.</summary>
    public string? Cor { get; set; }

    public bool Ativo { get; set; } = true;

    /// <summary>Quantos minutos o turno cobre, já descontando a pausa.</summary>
    public int MinutosUteis
    {
        get
        {
            // `TimeOnly - TimeOnly` dá a volta no relógio: 18:00 até 08:00 devolveria 14
            // horas, e um turno invertido passaria por um turno longo. Ele não existe —
            // a gravação recusa —, mas silenciar aqui seria esconder o erro em número.
            if (Fim <= Inicio)
            {
                return 0;
            }

            var total = (int)(Fim - Inicio).TotalMinutes;
            if (PausaInicio is { } pi && PausaFim is { } pf && pf > pi)
            {
                total -= (int)(pf - pi).TotalMinutes;
            }

            return Math.Max(0, total);
        }
    }

    /// <summary>`08:00 – 12:00` ou `08:00 – 17:00 (pausa 12:00 – 13:00)`.</summary>
    public string Janela => PausaInicio is { } pi && PausaFim is { } pf
        ? $"{Inicio:HH\\:mm} – {Fim:HH\\:mm} (pausa {pi:HH\\:mm} – {pf:HH\\:mm})"
        : $"{Inicio:HH\\:mm} – {Fim:HH\\:mm}";
}

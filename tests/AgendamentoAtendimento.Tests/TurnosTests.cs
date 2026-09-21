using AgendamentoAtendimento.Domain.Agenda;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// Turnos são atalho de escala, não camisa de força: quem trabalha em horário fixo segue
/// um turno, quem tem horário próprio continua com a janela livre. As duas formas
/// convivem na mesma semana da mesma pessoa.
/// </summary>
public class TurnosTests
{
    private static Turno Manha() => new()
    {
        TenantId = 1, Nome = "Manhã",
        Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(12, 0),
    };

    private static Turno Integral() => new()
    {
        TenantId = 1, Nome = "Integral",
        Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(18, 0),
        PausaInicio = new TimeOnly(12, 0), PausaFim = new TimeOnly(13, 0),
    };

    [Fact]
    public void Minutos_uteis_descontam_a_pausa()
    {
        Assert.Equal(240, Manha().MinutosUteis);
        // 10 horas menos 1 de pausa.
        Assert.Equal(540, Integral().MinutosUteis);
    }

    [Fact]
    public void A_janela_do_turno_vem_pronta_para_a_tela()
    {
        Assert.Equal("08:00 – 12:00", Manha().Janela);
        Assert.Equal("08:00 – 18:00 (pausa 12:00 – 13:00)", Integral().Janela);
    }

    [Fact]
    public void Com_turno_a_janela_efetiva_e_a_do_turno()
    {
        // A linha guarda um horário antigo; o turno é que manda. Copiar os minutos para a
        // linha deixaria a escala desatualizada no dia em que o turno mudasse.
        var escala = new HorarioStaff
        {
            TenantId = 1, UsuarioId = 1, DiaDaSemana = DayOfWeek.Monday,
            Inicio = new TimeOnly(9, 0), Fim = new TimeOnly(17, 0),
            Turno = Manha(), TurnoId = 1, Trabalha = true,
        };

        Assert.Equal(new TimeOnly(8, 0), escala.InicioEfetivo);
        Assert.Equal(new TimeOnly(12, 0), escala.FimEfetivo);
    }

    [Fact]
    public void Sem_turno_vale_o_horario_aberto_da_pessoa()
    {
        var escala = new HorarioStaff
        {
            TenantId = 1, UsuarioId = 1, DiaDaSemana = DayOfWeek.Tuesday,
            Inicio = new TimeOnly(13, 30), Fim = new TimeOnly(19, 0),
            PausaInicio = new TimeOnly(16, 0), PausaFim = new TimeOnly(16, 30),
            Trabalha = true,
        };

        Assert.Null(escala.TurnoId);
        Assert.Equal(new TimeOnly(13, 30), escala.InicioEfetivo);
        Assert.Equal(new TimeOnly(19, 0), escala.FimEfetivo);
        Assert.Equal(new TimeOnly(16, 0), escala.PausaInicioEfetiva);
    }

    [Fact]
    public void A_pausa_do_turno_substitui_a_da_linha_inteira()
    {
        // Somar as duas daria dois intervalos numa escala que tem um. Quem segue turno
        // segue a pausa do turno — é o que "seguir a escala" quer dizer.
        var escala = new HorarioStaff
        {
            TenantId = 1, UsuarioId = 1, DiaDaSemana = DayOfWeek.Monday,
            PausaInicio = new TimeOnly(15, 0), PausaFim = new TimeOnly(15, 30),
            Turno = Integral(), TurnoId = 1, Trabalha = true,
        };

        Assert.Equal(new TimeOnly(12, 0), escala.PausaInicioEfetiva);
        Assert.Equal(new TimeOnly(13, 0), escala.PausaFimEfetiva);
    }

    [Fact]
    public void Turno_sem_pausa_nao_herda_a_pausa_da_linha()
    {
        var escala = new HorarioStaff
        {
            TenantId = 1, UsuarioId = 1, DiaDaSemana = DayOfWeek.Monday,
            PausaInicio = new TimeOnly(10, 0), PausaFim = new TimeOnly(10, 15),
            Turno = Manha(), TurnoId = 1, Trabalha = true,
        };

        Assert.Null(escala.PausaInicioEfetiva);
    }

    [Fact]
    public void Turno_invertido_nao_tem_minutos_uteis_negativos()
    {
        var quebrado = new Turno
        {
            TenantId = 1, Nome = "?", Inicio = new TimeOnly(18, 0), Fim = new TimeOnly(8, 0),
        };

        Assert.Equal(0, quebrado.MinutosUteis);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// Hora com e sem segundos. O conversor de fábrica só aceita `HH:mm:ss`, e um campo de
/// hora de qualquer tela produz `09:00` — obrigar cada cliente a costurar o `:00`
/// espalharia a mesma regrinha por todos eles.
/// </summary>
public class HoraFlexivelTests
{
    /// <summary>Os mesmos conversores do Program.cs: é o pipeline real que importa.</summary>
    private static readonly JsonSerializerOptions Opcoes = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(), new HoraFlexivelConverter() },
    };

    [Theory]
    [InlineData("09:00", 9, 0)]
    [InlineData("09:30:00", 9, 30)]
    [InlineData("23:59:59", 23, 59)]
    public void Le_hora_com_ou_sem_segundos(string texto, int hora, int minuto)
    {
        var lida = JsonSerializer.Deserialize<TimeOnly>($"\"{texto}\"", Opcoes);

        Assert.Equal(hora, lida.Hour);
        Assert.Equal(minuto, lida.Minute);
    }

    [Fact]
    public void Hora_nula_continua_nula()
    {
        var req = JsonSerializer.Deserialize<EntrarNoPacoteRequest>(
            """{"clienteId": 7, "diaDaSemana": "Wednesday"}""", Opcoes);

        Assert.NotNull(req);
        Assert.Null(req!.Hora);
        Assert.Equal(DayOfWeek.Wednesday, req.DiaDaSemana);
    }

    [Fact]
    public void Hora_do_pedido_chega_inteira_sem_os_segundos()
    {
        var req = JsonSerializer.Deserialize<EntrarNoPacoteRequest>(
            """{"clienteId": 7, "diaDaSemana": "Wednesday", "hora": "14:00"}""", Opcoes);

        Assert.Equal(new TimeOnly(14, 0), req!.Hora);
    }

    [Fact]
    public void Texto_que_nao_e_hora_continua_sendo_erro()
    {
        // Aceitar mais formatos não é aceitar qualquer coisa: "meio-dia" segue recusado.
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<TimeOnly>("\"meio-dia\"", Opcoes));
    }

    [Fact]
    public void A_resposta_continua_com_os_segundos()
    {
        // O que já era escrito assim continua igual: nenhum cliente precisa mudar por
        // causa desta flexibilidade na leitura.
        var escrito = JsonSerializer.Serialize(new TimeOnly(9, 5), Opcoes);

        Assert.Equal("\"09:05:00\"", escrito);
    }
}

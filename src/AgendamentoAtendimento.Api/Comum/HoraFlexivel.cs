using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgendamentoAtendimento.Api.Comum;

/// <summary>
/// Lê hora em `HH:mm` além de `HH:mm:ss`.
///
/// O conversor de fábrica exige os segundos: `"09:00"` volta 400 com uma mensagem sobre
/// o tipo do campo inteiro, que não diz o que fazer. Só que `09:00` é uma hora — é o que
/// um campo de hora de qualquer tela produz —, e obrigar cada cliente a costurar `:00`
/// espalha a mesma regrinha por todos eles. Quem aceita o formato é quem o define.
///
/// Escreve sempre `HH:mm:ss`: a resposta continua igual à de antes.
/// </summary>
public sealed class HoraFlexivelConverter : JsonConverter<TimeOnly>
{
    private const string Padrao = "HH:mm:ss";

    private static readonly string[] Aceitos =
    {
        "HH:mm:ss.FFFFFFF", "HH:mm:ss", "HH:mm",
    };

    public override TimeOnly Read(
        ref Utf8JsonReader leitor, Type tipo, JsonSerializerOptions opcoes)
    {
        var texto = leitor.GetString();

        if (!string.IsNullOrWhiteSpace(texto) && TimeOnly.TryParseExact(
                texto, Aceitos, CultureInfo.InvariantCulture, DateTimeStyles.None, out var hora))
        {
            return hora;
        }

        throw new JsonException($"'{texto}' não é uma hora. Use HH:mm ou HH:mm:ss.");
    }

    public override void Write(
        Utf8JsonWriter escritor, TimeOnly valor, JsonSerializerOptions opcoes) =>
        escritor.WriteStringValue(valor.ToString(Padrao, CultureInfo.InvariantCulture));
}

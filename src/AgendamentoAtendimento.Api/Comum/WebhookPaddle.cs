using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgendamentoAtendimento.Domain.Assinaturas;

namespace AgendamentoAtendimento.Api.Comum;

/// <summary>O que a Api aproveita de um evento do Paddle.</summary>
public sealed record EventoPaddle(
    string EventoId, string Tipo, string? Status, string? SubscriptionId, string? CustomerId,
    long? TenantId, long? PlanoId, CicloCobranca? Ciclo, int? Assentos)
{
    /// <summary>
    /// Eventos que dizem "pago": a transação concluída e a assinatura criada/ativada ou
    /// atualizada em estado ativo. Só eles aplicam plano e assentos.
    /// </summary>
    public bool ConfirmaCompra => Tipo switch
    {
        "transaction.completed" or "transaction.paid" => true,
        "subscription.created" or "subscription.activated" or "subscription.updated" =>
            string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
}

/// <summary>
/// Conferência e leitura do webhook do Paddle Billing. Fica fora do controller para ser
/// testada sem HTTP.
/// </summary>
public static class WebhookPaddle
{
    /// <summary>Evento mais velho que isto é recusado: é assim que um replay não entra.</summary>
    public static readonly TimeSpan Tolerancia = TimeSpan.FromMinutes(5);

    /// <summary>
    /// O `custom_data` que o backend põe na transação ao criar o checkout. É ele que diz
    /// ao webhook o que aplicar — `assentos` é o TOTAL contratado, inclusos somados.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DadosDaCompra(
        long tenantId, long planoId, CicloCobranca ciclo, int assentos) =>
        new Dictionary<string, string>
        {
            ["tenant_id"] = tenantId.ToString(CultureInfo.InvariantCulture),
            ["plano_id"] = planoId.ToString(CultureInfo.InvariantCulture),
            ["ciclo"] = ciclo.ToString(),
            ["assentos"] = assentos.ToString(CultureInfo.InvariantCulture),
        };

    /// <summary>
    /// `Paddle-Signature: ts=...;h1=...` — HMAC-SHA256 de `"{ts}:{corpo}"` com o segredo do
    /// webhook, comparado em tempo constante. Sem segredo configurado, nada é válido.
    /// </summary>
    public static bool AssinaturaValida(string? cabecalho, string corpo, string? segredo, DateTimeOffset agora)
    {
        if (string.IsNullOrWhiteSpace(cabecalho) || string.IsNullOrWhiteSpace(segredo))
        {
            return false;
        }

        string? ts = null;
        var assinaturas = new List<string>();
        foreach (var parte in cabecalho.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var igual = parte.IndexOf('=');
            if (igual <= 0)
            {
                continue;
            }

            var chave = parte[..igual];
            var valor = parte[(igual + 1)..];
            if (chave == "ts")
            {
                ts = valor;
            }
            else if (chave == "h1")
            {
                assinaturas.Add(valor);
            }
        }

        if (ts is null || assinaturas.Count == 0 ||
            !long.TryParse(ts, NumberStyles.Integer, CultureInfo.InvariantCulture, out var segundos))
        {
            return false;
        }

        var quando = DateTimeOffset.FromUnixTimeSeconds(segundos);
        if ((agora - quando).Duration() > Tolerancia)
        {
            return false;
        }

        var esperado = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(segredo), Encoding.UTF8.GetBytes($"{ts}:{corpo}"));

        return assinaturas.Any(h1 =>
        {
            try
            {
                return CryptographicOperations.FixedTimeEquals(esperado, Convert.FromHexString(h1));
            }
            catch (FormatException)
            {
                return false;
            }
        });
    }

    /// <summary>Monta o cabeçalho de assinatura — usado pelos testes e por quem simula o Paddle.</summary>
    public static string Assinar(string corpo, string segredo, DateTimeOffset quando)
    {
        var ts = quando.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), Encoding.UTF8.GetBytes($"{ts}:{corpo}"));
        return $"ts={ts};h1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public static EventoPaddle? Ler(string corpo)
    {
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var raiz = doc.RootElement;
            var id = Texto(raiz, "event_id");
            var tipo = Texto(raiz, "event_type");
            if (id is null || tipo is null)
            {
                return null;
            }

            var dados = raiz.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                ? d : default;
            var extra = dados.ValueKind == JsonValueKind.Object &&
                        dados.TryGetProperty("custom_data", out var c) && c.ValueKind == JsonValueKind.Object
                ? c : default;

            // Na transação o id da assinatura vem em `subscription_id`; nos eventos de
            // assinatura, o próprio `id` é o dela.
            var subscription = Texto(dados, "subscription_id")
                ?? (tipo.StartsWith("subscription.", StringComparison.Ordinal) ? Texto(dados, "id") : null);

            return new EventoPaddle(
                id, tipo, Texto(dados, "status"), subscription, Texto(dados, "customer_id"),
                Numero(extra, "tenant_id"), Numero(extra, "plano_id"),
                Enum.TryParse<CicloCobranca>(Texto(extra, "ciclo"), true, out var ciclo) ? ciclo : null,
                Numero(extra, "assentos") is { } assentos ? (int)assentos : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Texto(JsonElement objeto, string nome) =>
        objeto.ValueKind == JsonValueKind.Object && objeto.TryGetProperty(nome, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static long? Numero(JsonElement objeto, string nome) =>
        long.TryParse(Texto(objeto, nome), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : null;
}

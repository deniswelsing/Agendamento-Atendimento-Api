using AgendamentoAtendimento.Infrastructure.Tenancy;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// Relógios para os testes. Os testes antigos pensam em UTC — a empresa deles fica em
/// Greenwich, de propósito, e 09:00 é 09:00Z. Os de fuso escolhem o fuso e congelam o
/// "agora" quando precisam.
/// </summary>
internal static class RelogioDeTeste
{
    /// <summary>Uma empresa em UTC: hora de parede e instante coincidem.</summary>
    public static RelogioDoTenant Utc => RelogioDoTenant.Fixo("UTC");

    /// <summary>Uma empresa no fuso dado, com o "agora" parado em <paramref name="agora"/>.</summary>
    public static RelogioDoTenant Em(string fuso, DateTimeOffset? agora = null) =>
        RelogioDoTenant.Fixo(fuso, agora is { } a ? new TempoParado(a) : null);
}

/// <summary>Um TimeProvider que não anda. É o que deixa perguntar "que dia é hoje às 23:30?".</summary>
internal sealed class TempoParado : TimeProvider
{
    private readonly DateTimeOffset _agora;

    public TempoParado(DateTimeOffset agora) => _agora = agora;

    public override DateTimeOffset GetUtcNow() => _agora.ToUniversalTime();
}

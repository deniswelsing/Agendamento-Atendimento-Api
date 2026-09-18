namespace AgendamentoAtendimento.Domain.Assinaturas;

/// <summary>Detalhamento do preço de uma assinatura, sempre em USD.</summary>
public sealed record DetalhePreco(
    CicloCobranca Ciclo,
    int AssentosTotais,
    int AssentosIncluidos,
    int AssentosAdicionais,
    decimal PrecoBase,
    decimal PrecoUnitarioAssentoAdicional,
    decimal TotalAssentosAdicionais,
    decimal Total,
    decimal EquivalenteMensal,
    decimal EconomiaAnual,
    bool SobConsulta);

/// <summary>
/// Regra de preço da assinatura. Esta classe é a fonte da verdade: o app Android tem uma
/// cópia da mesma conta só para a tela reagir na hora, mas quem cobra é o servidor.
///
/// O plano já inclui <see cref="Plano.UsuariosIncluidos"/> assentos; cada usuário adicional
/// custa US$ 10,00 por mês (US$ 120,00 por ano no ciclo anual). O desconto do ciclo anual
/// vale para o preço do plano, nunca para os assentos.
/// </summary>
public static class PrecificacaoAssinatura
{
    /// <summary>Preço mensal, em USD, de cada usuário além dos inclusos no plano.</summary>
    public const decimal PrecoAssentoAdicionalMensalUsd = 10.00m;

    public const int MesesNoAno = 12;

    public const decimal PrecoAssentoAdicionalAnualUsd =
        PrecoAssentoAdicionalMensalUsd * MesesNoAno;

    public static decimal PrecoAssentoAdicional(CicloCobranca ciclo) =>
        ciclo == CicloCobranca.Mensal
            ? PrecoAssentoAdicionalMensalUsd
            : PrecoAssentoAdicionalAnualUsd;

    /// <summary>Assentos cobrados à parte. Nunca negativo.</summary>
    public static int AssentosAdicionais(Plano plano, int assentosTotais) =>
        Math.Max(0, assentosTotais - plano.UsuariosIncluidos);

    public static DetalhePreco Calcular(Plano plano, CicloCobranca ciclo, int assentosTotais)
    {
        ArgumentNullException.ThrowIfNull(plano);

        var assentos = Math.Max(1, assentosTotais);
        var adicionais = AssentosAdicionais(plano, assentos);
        var unitario = PrecoAssentoAdicional(ciclo);
        var totalAdicionais = Escala(unitario * adicionais);

        var baseDoPlano = plano.PrecoBase(ciclo);
        var sobConsulta = plano.IsCustom || baseDoPlano is null;
        var precoBase = Escala(baseDoPlano ?? 0m);
        var total = Escala(precoBase + totalAdicionais);

        var equivalenteMensal = ciclo == CicloCobranca.Mensal
            ? total
            : Escala(total / MesesNoAno);

        return new DetalhePreco(
            Ciclo: ciclo,
            AssentosTotais: assentos,
            AssentosIncluidos: plano.UsuariosIncluidos,
            AssentosAdicionais: adicionais,
            PrecoBase: precoBase,
            PrecoUnitarioAssentoAdicional: unitario,
            TotalAssentosAdicionais: totalAdicionais,
            Total: total,
            EquivalenteMensal: equivalenteMensal,
            EconomiaAnual: EconomiaAnual(plano, assentos),
            SobConsulta: sobConsulta);
    }

    /// <summary>
    /// Diferença entre pagar doze meses no ciclo mensal e pagar o ciclo anual. Zero quando o
    /// plano não publica os dois preços.
    /// </summary>
    public static decimal EconomiaAnual(Plano plano, int assentosTotais)
    {
        ArgumentNullException.ThrowIfNull(plano);

        if (plano.PrecoMensalUsd is not { } mensal || plano.PrecoAnualUsd is not { } anual)
        {
            return Escala(0m);
        }

        var adicionais = AssentosAdicionais(plano, Math.Max(1, assentosTotais));
        var dozeMeses = (mensal + PrecoAssentoAdicionalMensalUsd * adicionais) * MesesNoAno;
        var cicloAnual = anual + PrecoAssentoAdicionalAnualUsd * adicionais;

        return Escala(Math.Max(0m, dozeMeses - cicloAnual));
    }

    /// <summary>
    /// Valor proporcional ao adicionar assentos no meio do ciclo. Informativo: a cobrança
    /// efetiva é emitida pelo Paddle ou pelo Google Play.
    /// </summary>
    public static decimal ProporcionalNovosAssentos(
        CicloCobranca ciclo, int novosAssentos, int diasRestantes, int diasDoCiclo)
    {
        if (novosAssentos <= 0 || diasDoCiclo <= 0 || diasRestantes <= 0)
        {
            return Escala(0m);
        }

        var dias = Math.Min(diasRestantes, diasDoCiclo);
        var cheio = PrecoAssentoAdicional(ciclo) * novosAssentos;
        return Escala(cheio * dias / diasDoCiclo);
    }

    private static decimal Escala(decimal valor) =>
        decimal.Round(valor, 2, MidpointRounding.AwayFromZero);
}

using AgendamentoAtendimento.Domain.Common;

namespace AgendamentoAtendimento.Domain.Assinaturas;

public enum CicloCobranca
{
    Mensal = 1,
    Anual = 2,
}

public enum GatewayPagamento
{
    Paddle = 1,
    GooglePlay = 2,
    /// <summary>Plano gratuito ou acordo comercial cobrado fora da plataforma.</summary>
    Manual = 3,
}

public enum StatusAssinatura
{
    Pendente = 1,
    Ativa = 2,
    EmPeriodoDeGraca = 3,
    Suspensa = 4,
    Cancelada = 5,
    Expirada = 6,
}

/// <summary>
/// Plano de assinatura da plataforma. Todos os preços em USD, espelhando a tabela `Planos`
/// do PetShop.Route (Basic / Platinum / Ultimate / Custom).
/// </summary>
public class Plano : Entidade
{
    public required string Codigo { get; set; }
    public required string Nome { get; set; }
    public string? Descricao { get; set; }

    /// <summary>Nulo em plano sob consulta.</summary>
    public decimal? PrecoMensalUsd { get; set; }
    public decimal? PrecoAnualUsd { get; set; }

    /// <summary>Assentos já inclusos no preço base.</summary>
    public int UsuariosIncluidos { get; set; } = 1;

    /// <summary>Teto de assentos. Nulo = ilimitado.</summary>
    public int? LimiteUsuarios { get; set; }

    public int Ordem { get; set; }
    public bool Ativo { get; set; } = true;
    public bool IsCustom { get; set; }

    /// <summary>Recursos liberados, separados por vírgula.</summary>
    public string? Recursos { get; set; }

    // Identificadores nos gateways
    public string? PaddlePriceIdMensal { get; set; }
    public string? PaddlePriceIdAnual { get; set; }
    public string? PlayProductId { get; set; }
    public string? PlayBasePlanIdMensal { get; set; }
    public string? PlayBasePlanIdAnual { get; set; }

    public decimal? PrecoBase(CicloCobranca ciclo) =>
        ciclo == CicloCobranca.Mensal ? PrecoMensalUsd : PrecoAnualUsd;

    public bool Suporta(int assentos) => LimiteUsuarios is null || assentos <= LimiteUsuarios;
}

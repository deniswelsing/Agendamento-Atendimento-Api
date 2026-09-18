using AgendamentoAtendimento.Domain.Common;

namespace AgendamentoAtendimento.Domain.MultiTenancy;

/// <summary>Uma empresa assinante. É o limite de isolamento de todos os dados.</summary>
public class Tenant : Entidade
{
    public required string Slug { get; set; }
    public required string NomeEmpresa { get; set; }
    public string? Documento { get; set; }
    public string Moeda { get; set; } = "BRL";
    public string FusoHorario { get; set; } = "America/Sao_Paulo";
    public string IdiomaPadrao { get; set; } = "pt-BR";
    public bool Ativo { get; set; } = true;

    /// <summary>
    /// Identificador opaco enviado ao Google Play como `obfuscatedAccountId`.
    /// É por ele que a RTDN do Play é ligada de volta ao tenant.
    /// </summary>
    public string? ReferenciaExterna { get; set; }
}

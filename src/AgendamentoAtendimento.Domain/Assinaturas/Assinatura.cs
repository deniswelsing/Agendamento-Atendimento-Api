using AgendamentoAtendimento.Domain.Common;
using AgendamentoAtendimento.Domain.MultiTenancy;

namespace AgendamentoAtendimento.Domain.Assinaturas;

/// <summary>
/// Assinatura do tenant. É compartilhada entre os produtos da suíte: os mesmos assentos
/// valem para o PetShop.Route e para o Agendamento &amp; Atendimento.
/// </summary>
public class Assinatura : Entidade
{
    public long TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public long PlanoId { get; set; }
    public Plano? Plano { get; set; }

    public CicloCobranca Ciclo { get; set; } = CicloCobranca.Mensal;
    public GatewayPagamento Gateway { get; set; } = GatewayPagamento.Paddle;
    public StatusAssinatura Status { get; set; } = StatusAssinatura.Pendente;

    /// <summary>Assentos contratados, incluindo os que já vêm no plano.</summary>
    public int AssentosContratados { get; set; } = 1;

    public DateTimeOffset? InicioCicloAtual { get; set; }
    public DateTimeOffset? FimCicloAtual { get; set; }
    public DateTimeOffset? ProximaCobranca { get; set; }
    public DateTimeOffset? UltimoPagamentoEm { get; set; }
    public decimal? ValorUltimaCobrancaUsd { get; set; }

    /// <summary>Período de graça após falha de pagamento, como no PetShop.Route.</summary>
    public DateTimeOffset? FimPeriodoDeGraca { get; set; }

    public bool CancelamentoAgendado { get; set; }
    public DateTimeOffset? CanceladaEm { get; set; }
    public string? MotivoCancelamento { get; set; }

    // Identificadores do gateway
    public string? PaddleSubscriptionId { get; set; }
    public string? PaddleCustomerId { get; set; }
    public string? PlayPurchaseTokenPlano { get; set; }
    public string? PlayPurchaseTokenAssentos { get; set; }

    public string? GerenciamentoUrl { get; set; }

    public ICollection<AssinaturaProduto> Produtos { get; set; } = new List<AssinaturaProduto>();

    /// <summary>Libera o uso do app, considerando o período de graça.</summary>
    public bool LiberaAcesso =>
        Status == StatusAssinatura.Ativa ||
        (Status == StatusAssinatura.EmPeriodoDeGraca && FimPeriodoDeGraca > DateTimeOffset.UtcNow);

    public int AssentosAdicionais(Plano plano) =>
        Math.Max(0, AssentosContratados - plano.UsuariosIncluidos);
}

/// <summary>
/// Produto coberto por uma assinatura. É a tabela que materializa a assinatura
/// compartilhada: uma linha para `petshop-route`, outra para `agendamento-atendimento`.
/// </summary>
public class AssinaturaProduto : Entidade
{
    public long AssinaturaId { get; set; }
    public Assinatura? Assinatura { get; set; }

    public required string ProdutoChave { get; set; }
    public bool Ativo { get; set; } = true;
}

/// <summary>Chaves de produto conhecidas pela Api (cabeçalho `X-Produto`).</summary>
public static class Produtos
{
    public const string Agendamento = "agendamento-atendimento";
    public const string PetShopRoute = "petshop-route";

    public static readonly IReadOnlyList<string> Todos = new[] { Agendamento, PetShopRoute };

    public static bool Conhecido(string? chave) =>
        !string.IsNullOrWhiteSpace(chave) &&
        Todos.Any(p => string.Equals(p, chave, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Evento cru recebido de um gateway. Gravado antes do processamento para dar
/// idempotência: um webhook reentregue não cobra nem libera nada duas vezes.
/// </summary>
public class EventoGateway : Entidade
{
    public GatewayPagamento Gateway { get; set; }

    /// <summary>Identificador do evento no gateway — é a chave de idempotência.</summary>
    public required string EventoExternoId { get; set; }

    public required string Tipo { get; set; }
    public long? TenantId { get; set; }
    public long? AssinaturaId { get; set; }
    public required string Payload { get; set; }
    public bool Processado { get; set; }
    public DateTimeOffset? ProcessadoEm { get; set; }
    public string? Erro { get; set; }
}

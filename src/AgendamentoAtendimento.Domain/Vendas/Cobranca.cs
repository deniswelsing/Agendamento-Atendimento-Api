using AgendamentoAtendimento.Domain.Common;

namespace AgendamentoAtendimento.Domain.Vendas;

/// <summary>Como o dinheiro foi (ou será) capturado.</summary>
public enum MeioDeCaptura
{
    /// <summary>
    /// Alguém passou o cartão numa maquininha de fora e digitou o valor aqui. O sistema
    /// não viu a transação: a taxa é estimativa e o código de autorização é digitado.
    /// </summary>
    Manual = 1,

    /// <summary>Cartão presente, capturado pelo terminal que roda o app.</summary>
    TerminalPresente = 2,

    /// <summary>Pix com QR dinâmico, confirmado pelo PSP.</summary>
    PixQr = 3,

    /// <summary>Cartão em arquivo ou link, cobrado por gateway online.</summary>
    GatewayOnline = 4,
}

public enum StatusCobranca
{
    /// <summary>Criada, ainda não entregue ao terminal ou ao PSP.</summary>
    Criada = 1,

    /// <summary>Entregue: o terminal está com o cliente, ou o QR está na tela.</summary>
    EmAndamento = 2,

    Aprovada = 3,
    Recusada = 4,
    Cancelada = 5,

    /// <summary>Passou do prazo sem resposta. Nunca vira pagamento.</summary>
    Expirada = 6,
}

/// <summary>
/// A intenção de cobrar, criada ANTES de mandar o terminal cobrar ou de mostrar o QR.
///
/// Existe por um motivo só: entre "mandar cobrar" e "saber o resultado" o app pode cair,
/// a rede pode sumir e a pessoa pode tentar de novo. Sem um registro criado antes, uma
/// cobrança aprovada que não voltou vira dinheiro cobrado do cliente e não lançado — ou,
/// pior, uma segunda cobrança. A chave de idempotência faz a repetição devolver a mesma
/// cobrança em vez de criar outra.
///
/// É o mesmo desenho para maquininha, Pix e gateway: só muda quem responde.
/// </summary>
public class Cobranca : EntidadeDeTenant
{
    public long VendaId { get; set; }
    public Venda? Venda { get; set; }

    /// <summary>Enviada pelo app. Repetir a mesma chave devolve esta cobrança.</summary>
    public required string ChaveIdempotencia { get; set; }

    public MeioDeCaptura Meio { get; set; }
    public StatusCobranca Status { get; set; } = StatusCobranca.Criada;

    public long FormaPagamentoId { get; set; }
    public FormaPagamento? FormaPagamento { get; set; }

    public decimal Valor { get; set; }
    public int Parcelas { get; set; } = 1;

    /// <summary>Quem capturou: `stone`, `cielo`, `pagbank`, `mercadopago`, `pix-psp`…</summary>
    public string? AdquirenteChave { get; set; }

    /// <summary>Série do terminal, quando a captura foi presencial.</summary>
    public string? TerminalSerie { get; set; }

    // ------------------------------------------------------ resposta da captura
    /// <summary>NSU da adquirente — é por ele que se acha a transação no extrato.</summary>
    public string? Nsu { get; set; }
    public string? CodigoAutorizacao { get; set; }
    public string? Bandeira { get; set; }
    public string? UltimosDigitos { get; set; }

    /// <summary>Identificador da transação no PSP/gateway, quando não é cartão presente.</summary>
    public string? TransacaoExternaId { get; set; }

    /// <summary>Copia-e-cola do Pix, quando o meio é QR.</summary>
    public string? PixCopiaECola { get; set; }

    /// <summary>Taxa que a adquirente informou. Nula enquanto ninguém confirmou.</summary>
    public decimal? ValorTaxaReal { get; set; }

    public string? MotivoRecusa { get; set; }

    public DateTimeOffset? EnviadaEm { get; set; }
    public DateTimeOffset? RespondidaEm { get; set; }
    public DateTimeOffset ExpiraEm { get; set; }

    /// <summary>Pagamento gerado quando a cobrança é aprovada.</summary>
    public long? PagamentoId { get; set; }
    public Pagamento? Pagamento { get; set; }

    public bool EstaAberta =>
        Status is StatusCobranca.Criada or StatusCobranca.EmAndamento;

    public bool Expirou(DateTimeOffset agora) => EstaAberta && agora >= ExpiraEm;
}

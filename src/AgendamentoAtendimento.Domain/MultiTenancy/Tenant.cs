using AgendamentoAtendimento.Domain.Agenda;
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
    /// Como a agenda conta ocupação. Nasce em <see cref="ModoDeOcupacao.PorServico"/>
    /// porque é o modo que aproveita mais a agenda; quem precisa do funcionário preso ao
    /// atendimento inteiro troca nas configurações.
    /// </summary>
    public ModoDeOcupacao ModoDeOcupacao { get; set; } = ModoDeOcupacao.PorServico;

    /// <summary>
    /// Teto de atendimentos que a empresa aceita por dia. Zero é sem limite — e é o
    /// padrão, porque uma empresa que nunca pediu teto não pode ganhar um.
    ///
    /// Serve a quem prefere atender bem menos gente do que mal muita: o dia fecha quando
    /// enche, em vez de a agenda continuar oferecendo horário até não caber.
    /// </summary>
    public int LimiteDiarioDeAtendimentos { get; set; }

    /// <summary>
    /// O mesmo teto, por pessoa do time. Existe separado porque as duas perguntas são
    /// diferentes: a empresa pode aguentar vinte atendimentos num dia em que ninguém
    /// deveria fazer mais de seis.
    /// </summary>
    public int LimiteDiarioPorPessoa { get; set; }

    /// <summary>
    /// Identificador opaco enviado ao Google Play como `obfuscatedAccountId`.
    /// É por ele que a RTDN do Play é ligada de volta ao tenant.
    /// </summary>
    public string? ReferenciaExterna { get; set; }
}

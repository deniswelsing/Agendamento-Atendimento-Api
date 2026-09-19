using AgendamentoAtendimento.Domain.Common;

namespace AgendamentoAtendimento.Domain.Agenda;

/// <summary>De onde veio o agendamento. O que o cliente marcou sozinho fica marcado.</summary>
public enum OrigemAgendamento
{
    /// <summary>Alguém do time marcou pelo app.</summary>
    Interno = 1,

    /// <summary>O próprio cliente marcou pela página pública.</summary>
    Online = 2,
}

/// <summary>
/// A página pública de agendamento de uma empresa: o endereço que o cliente abre para
/// marcar sozinho, sem ligar e sem ter conta.
///
/// É a única porta da Api que responde sem token, então tudo que ela expõe é escolha
/// explícita de quem configurou — serviço por serviço, e só quando <see cref="Ativa"/>.
/// </summary>
public class ConfiguracaoPaginaPublica : EntidadeDeTenant
{
    /// <summary>Enquanto for falso, a página responde 404 como se não existisse.</summary>
    public bool Ativa { get; set; }

    /// <summary>
    /// O que vai no endereço. É único no sistema inteiro, não por tenant: dois endereços
    /// iguais apontariam para empresas diferentes.
    /// </summary>
    public required string Slug { get; set; }

    /// <summary>Nome exibido ao cliente. Vazio cai no nome da empresa.</summary>
    public string? TituloPublico { get; set; }

    public string? Mensagem { get; set; }
    public string? Endereco { get; set; }
    public string? TelefoneContato { get; set; }

    /// <summary>
    /// Quanto tempo antes o cliente ainda pode marcar. Sem isso alguém marcaria para
    /// daqui a cinco minutos e ninguém veria a tempo.
    /// </summary>
    public int AntecedenciaMinimaHoras { get; set; } = 2;

    /// <summary>Até quando a agenda fica aberta ao público, em dias.</summary>
    public int JanelaMaximaDias { get; set; } = 60;

    /// <summary>
    /// Quando ligado, o que o cliente marca entra como pedido e só vira compromisso
    /// depois que alguém do time aprova. O horário fica preso enquanto isso — soltá-lo
    /// deixaria dois clientes pedirem o mesmo encaixe.
    /// </summary>
    public bool ExigeAprovacao { get; set; }

    /// <summary>Deixa o cliente escolher com quem quer ser atendido.</summary>
    public bool PermiteEscolherProfissional { get; set; } = true;

    public bool ExigeTelefone { get; set; } = true;

    /// <summary>Limite de pedidos por e-mail por dia. Segura o abuso da porta aberta.</summary>
    public int LimiteDiarioPorCliente { get; set; } = 5;
}

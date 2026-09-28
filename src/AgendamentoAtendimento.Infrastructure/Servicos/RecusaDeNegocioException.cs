namespace AgendamentoAtendimento.Infrastructure.Servicos;

/// <summary>
/// Uma regra de negócio recusada dentro de um serviço, já com o código que a Api devolve.
///
/// Os serviços não conhecem HTTP; o middleware de erro da Api traduz esta recusa no 400 de
/// sempre (`{ message, code }`). Herda de <see cref="InvalidOperationException"/> para que
/// quem já tratava a recusa genérica continue tratando.
/// </summary>
public class RecusaDeNegocioException : InvalidOperationException
{
    public RecusaDeNegocioException(string mensagem, string codigo) : base(mensagem) => Codigo = codigo;

    public string Codigo { get; }
}

using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AgendamentoAtendimento.Api.Comum;

[ApiController]
[Authorize]
[Route("api/[controller]")]
[Produces("application/json")]
public abstract class ControllerBaseApi : ControllerBase
{
    protected long TenantId => User.ObterLong(ClaimsApp.TenantId)
        ?? throw new RegraDeNegocioException("Requisição sem tenant.");

    protected long UsuarioId => User.ObterLong(ClaimsApp.UsuarioId)
        ?? throw new RegraDeNegocioException("Requisição sem usuário.");

    protected string Produto =>
        Request.Headers[ContextoMiddleware.CabecalhoProduto].FirstOrDefault()
        ?? User.FindFirst(ClaimsApp.Produto)?.Value
        ?? AgendamentoAtendimento.Domain.Assinaturas.Produtos.Agendamento;

    protected IReadOnlyList<string> PermissoesDoUsuario => User.Permissoes();

    protected static T NaoNulo<T>(T? valor, string mensagem) where T : class =>
        valor ?? throw new NaoEncontradoException(mensagem);
}

/// <summary>Envelope de listagem paginada usado por todos os endpoints de lista.</summary>
public sealed record PaginaDto<T>(IReadOnlyList<T> Itens, int Pagina, int TamanhoPagina, int Total)
{
    public bool TemProximaPagina => Pagina * TamanhoPagina < Total;
}

/// <summary>
/// Página pedida na query. Aceita as chaves soltas — `?pagina=2&tamanhoPagina=50`, que é
/// o que o web e o Android mandam — e também a forma com prefixo (`pagina.pagina=2`).
/// </summary>
/// <remarks>
/// Tem binder próprio porque o de fábrica, para um parâmetro chamado `pagina`, vê a chave
/// `pagina` na query, passa a procurar tudo com o prefixo `pagina.` e ignora as chaves
/// soltas: `?pagina=2&tamanhoPagina=50` voltava sempre a página 1 com 20 itens.
/// </remarks>
[ModelBinder(BinderType = typeof(ParametrosDePaginaBinder))]
public sealed record ParametrosDePagina
{
    private const int TamanhoMaximo = 100;

    public int Pagina { get; init; } = 1;
    public int TamanhoPagina { get; init; } = 20;

    public int PaginaSegura => Pagina < 1 ? 1 : Pagina;

    public int TamanhoSeguro => TamanhoPagina is < 1 or > TamanhoMaximo ? 20 : TamanhoPagina;

    public int Pular => (PaginaSegura - 1) * TamanhoSeguro;
}

/// <summary>
/// Lê <see cref="ParametrosDePagina"/> da query pelas chaves soltas ou com o prefixo do
/// parâmetro. Sem nenhuma das chaves, devolve nulo e a ação usa o padrão dela.
/// </summary>
public sealed class ParametrosDePaginaBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);

        var prefixo = string.IsNullOrEmpty(contexto.FieldName) ? "pagina" : contexto.FieldName;

        var pagina = Ler(contexto, $"{prefixo}.{nameof(ParametrosDePagina.Pagina)}")
            ?? Ler(contexto, nameof(ParametrosDePagina.Pagina));
        var tamanho = Ler(contexto, $"{prefixo}.{nameof(ParametrosDePagina.TamanhoPagina)}")
            ?? Ler(contexto, nameof(ParametrosDePagina.TamanhoPagina));

        if (pagina is null && tamanho is null)
        {
            contexto.Result = ModelBindingResult.Success(null);
            return Task.CompletedTask;
        }

        var padrao = new ParametrosDePagina();
        contexto.Result = ModelBindingResult.Success(new ParametrosDePagina
        {
            Pagina = Converter(contexto, pagina) ?? padrao.Pagina,
            TamanhoPagina = Converter(contexto, tamanho) ?? padrao.TamanhoPagina,
        });
        return Task.CompletedTask;
    }

    private static (string Chave, string Valor)? Ler(ModelBindingContext contexto, string chave)
    {
        // A query é lida sem diferenciar maiúsculas, como o binder de fábrica faz.
        var valor = contexto.ValueProvider.GetValue(chave).FirstValue;
        return string.IsNullOrWhiteSpace(valor) ? null : (chave, valor);
    }

    private static int? Converter(ModelBindingContext contexto, (string Chave, string Valor)? lido)
    {
        if (lido is not { } l)
        {
            return null;
        }

        if (int.TryParse(l.Valor, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var numero))
        {
            return numero;
        }

        // Número inválido é erro de quem chamou (400), como seria no binder de fábrica.
        contexto.ModelState.TryAddModelError(l.Chave, $"'{l.Valor}' não é um número.");
        return null;
    }
}

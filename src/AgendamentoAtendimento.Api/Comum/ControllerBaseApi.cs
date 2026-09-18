using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

public sealed record ParametrosDePagina
{
    private const int TamanhoMaximo = 100;

    public int Pagina { get; init; } = 1;
    public int TamanhoPagina { get; init; } = 20;

    public int PaginaSegura => Pagina < 1 ? 1 : Pagina;

    public int TamanhoSeguro => TamanhoPagina is < 1 or > TamanhoMaximo ? 20 : TamanhoPagina;

    public int Pular => (PaginaSegura - 1) * TamanhoSeguro;
}

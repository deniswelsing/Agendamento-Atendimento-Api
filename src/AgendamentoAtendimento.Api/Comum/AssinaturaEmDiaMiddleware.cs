using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;

namespace AgendamentoAtendimento.Api.Comum;

/// <summary>
/// Assinatura em dia em TODA requisição autenticada, e não só no bootstrap. Antes, a
/// checagem ficava só no `GET /api/bootstrap`: quem já tinha token (ou chamava a Api
/// direto) continuava usando tudo com a assinatura cancelada ou expirada.
///
/// "Em dia" é a mesma regra de <see cref="Assinatura.LiberaAcesso"/>: ativa, ou em período
/// de graça que ainda não acabou. Empresa sem assinatura nenhuma passa — é o mesmo
/// tratamento que o bootstrap sempre deu (o plano de entrada vale até contratar).
///
/// Recusa com 402 `ASSINATURA_INATIVA` (ou `PRODUTO_NAO_COBERTO` quando o produto do
/// cabeçalho `X-Produto` não está na assinatura). O app já traduz 402 em "Ver planos".
/// </summary>
/// <remarks>
/// Fica depois da autorização: sem token, quem responde é o 401 de sempre. As rotas que
/// continuam abertas com a assinatura parada estão em <see cref="Liberada"/> — entrar,
/// desenhar a tela, pagar e a saúde do serviço. A página pública é anônima e tem a regra
/// dela em <c>PublicoController</c>.
/// </remarks>
public class AssinaturaEmDiaMiddleware
{
    private static readonly string[] RotasLiberadas =
    {
        "/api/auth",
        "/api/bootstrap",
        "/api/assinatura",
        "/api/publico",
        "/health",
        "/swagger",
    };

    private readonly RequestDelegate _proximo;
    private readonly TimeProvider _relogio;

    public AssinaturaEmDiaMiddleware(RequestDelegate proximo, TimeProvider relogio)
    {
        _proximo = proximo;
        _relogio = relogio;
    }

    public static bool Liberada(PathString caminho) =>
        RotasLiberadas.Any(r => caminho.StartsWithSegments(r, StringComparison.OrdinalIgnoreCase));

    public async Task InvokeAsync(HttpContext http, ContextoAtual contexto, AssinaturaService assinaturas)
    {
        if (contexto.TenantId is null || Liberada(http.Request.Path))
        {
            await _proximo(http);
            return;
        }

        var situacao = await assinaturas.SituacaoAsync(http.RequestAborted);
        if (situacao.Existe)
        {
            if (!situacao.LiberaAcesso(_relogio.GetUtcNow()))
            {
                throw new AssinaturaExigidaException(
                    $"A assinatura desta empresa está {Descrever(situacao.Status)}. " +
                    "Regularize o pagamento para continuar usando o sistema.",
                    MotivoRecusa.AssinaturaInativa, "ASSINATURA_INATIVA");
            }

            var produto = http.Request.Headers[ContextoMiddleware.CabecalhoProduto].FirstOrDefault()
                ?? http.User.FindFirst(ClaimsApp.Produto)?.Value
                ?? Produtos.Agendamento;
            if (!situacao.Cobre(produto))
            {
                throw new AssinaturaExigidaException(
                    $"A assinatura atual não cobre o produto {produto}.",
                    MotivoRecusa.ProdutoNaoCoberto, "PRODUTO_NAO_COBERTO");
            }
        }

        await _proximo(http);
    }

    private static string Descrever(StatusAssinatura status) => status switch
    {
        StatusAssinatura.Pendente => "pendente",
        StatusAssinatura.EmPeriodoDeGraca => "com o período de graça encerrado",
        StatusAssinatura.Suspensa => "suspensa",
        StatusAssinatura.Cancelada => "cancelada",
        StatusAssinatura.Expirada => "expirada",
        _ => "inativa",
    };
}

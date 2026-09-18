using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Infrastructure.Servicos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AgendamentoAtendimento.Api.Autenticacao;

/// <summary>
/// Exige que o plano assinado libere um recurso do catálogo. Quando não libera, a resposta
/// é 402 com o nome do plano mínimo — o mesmo código que o app já usa para abrir a compra.
/// </summary>
/// <remarks>
/// É diferente de <see cref="RequerPermissaoAttribute"/>: permissão é o que o admin deu ao
/// perfil, recurso é o que a empresa comprou. Um admin pode ter a permissão e ainda assim
/// esbarrar no plano.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequerRecursoAttribute : TypeFilterAttribute
{
    public RequerRecursoAttribute(string recurso) : base(typeof(RecursoFilter))
    {
        Arguments = new object[] { recurso };
    }

    private sealed class RecursoFilter : IAsyncActionFilter
    {
        private readonly AssinaturaService _assinaturas;
        private readonly string _recurso;

        public RecursoFilter(AssinaturaService assinaturas, string recurso)
        {
            _assinaturas = assinaturas;
            _recurso = recurso;
        }

        public async Task OnActionExecutionAsync(
            ActionExecutingContext contexto, ActionExecutionDelegate proxima)
        {
            var resultado = await _assinaturas.VerificarRecursoAsync(
                _recurso, contexto.HttpContext.RequestAborted);

            if (!resultado.Ok)
            {
                throw new AssinaturaExigidaException(
                    resultado.Mensagem ?? "Recurso fora do plano contratado.", resultado.Motivo);
            }

            await proxima();
        }
    }
}

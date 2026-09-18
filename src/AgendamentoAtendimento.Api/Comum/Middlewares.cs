using System.Net;
using System.Text.Json;
using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;

namespace AgendamentoAtendimento.Api.Comum;

/// <summary>Corpo de erro padrão. O app traduz o status; a mensagem vem pronta daqui.</summary>
public sealed record ErroApi(string Message, string? Code = null, object? Dados = null);

/// <summary>
/// Preenche o <see cref="ContextoAtual"/> a partir do token e do cabeçalho `X-Produto`.
/// Roda depois da autenticação e antes de qualquer acesso ao banco.
/// </summary>
public class ContextoMiddleware
{
    public const string CabecalhoProduto = "X-Produto";

    private readonly RequestDelegate _proximo;

    public ContextoMiddleware(RequestDelegate proximo) => _proximo = proximo;

    public async Task InvokeAsync(HttpContext http, ContextoAtual contexto)
    {
        contexto.Produto = http.Request.Headers[CabecalhoProduto].FirstOrDefault()
            ?? Produtos.Agendamento;
        contexto.Ip = http.Request.Headers["X-Forwarded-For"].FirstOrDefault()
            ?? http.Connection.RemoteIpAddress?.ToString();

        if (http.User.Identity?.IsAuthenticated == true)
        {
            contexto.TenantId = http.User.ObterLong(ClaimsApp.TenantId);
            contexto.UsuarioId = http.User.ObterLong(ClaimsApp.UsuarioId);
            contexto.TenantSlug = http.User.FindFirst(ClaimsApp.TenantSlug)?.Value;
        }

        await _proximo(http);
    }
}

/// <summary>
/// Converte exceções em respostas JSON consistentes e traduz a recusa de assinatura em 402,
/// que é o código que o app usa para abrir a compra de assento.
/// </summary>
public class TratamentoDeErroMiddleware
{
    private readonly RequestDelegate _proximo;
    private readonly ILogger<TratamentoDeErroMiddleware> _log;

    public TratamentoDeErroMiddleware(RequestDelegate proximo, ILogger<TratamentoDeErroMiddleware> log)
    {
        _proximo = proximo;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext http)
    {
        try
        {
            await _proximo(http);
        }
        catch (AssinaturaExigidaException ex)
        {
            await EscreverAsync(http, HttpStatusCode.PaymentRequired,
                new ErroApi(ex.Message, ex.Motivo.ToString()));
        }
        catch (RegraDeNegocioException ex)
        {
            await EscreverAsync(http, HttpStatusCode.BadRequest, new ErroApi(ex.Message, ex.Codigo));
        }
        catch (NaoEncontradoException ex)
        {
            await EscreverAsync(http, HttpStatusCode.NotFound, new ErroApi(ex.Message, "NAO_ENCONTRADO"));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha não tratada em {Caminho}", http.Request.Path);
            await EscreverAsync(http, HttpStatusCode.InternalServerError,
                new ErroApi("Erro inesperado ao processar a requisição.", "ERRO_INTERNO"));
        }
    }

    private static async Task EscreverAsync(HttpContext http, HttpStatusCode status, ErroApi corpo)
    {
        if (http.Response.HasStarted)
        {
            return;
        }

        http.Response.Clear();
        http.Response.StatusCode = (int)status;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsync(JsonSerializer.Serialize(corpo, OpcoesJson.Padrao));
    }
}

public static class OpcoesJson
{
    public static readonly JsonSerializerOptions Padrao = new(JsonSerializerDefaults.Web);
}

public class RegraDeNegocioException : Exception
{
    public RegraDeNegocioException(string mensagem, string? codigo = null) : base(mensagem)
        => Codigo = codigo;

    public string? Codigo { get; }
}

public class NaoEncontradoException : Exception
{
    public NaoEncontradoException(string mensagem) : base(mensagem) { }
}

/// <summary>Assinatura inativa, produto não coberto ou sem assento livre.</summary>
public class AssinaturaExigidaException : Exception
{
    public AssinaturaExigidaException(string mensagem, MotivoRecusa motivo) : base(mensagem)
        => Motivo = motivo;

    public MotivoRecusa Motivo { get; }
}

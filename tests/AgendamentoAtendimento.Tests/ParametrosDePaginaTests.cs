using System.Net;
using System.Net.Http.Json;
using AgendamentoAtendimento.Api.Comum;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>Ação mínima com o mesmo parâmetro das listagens, para o binder de verdade rodar.</summary>
[ApiController]
[Route("eco-da-pagina")]
public class EcoDaPaginaController : ControllerBase
{
    [HttpGet]
    public ActionResult<int[]> Eco([FromQuery] ParametrosDePagina? pagina = null) =>
        pagina is null ? new[] { 0, 0 } : new[] { pagina.PaginaSegura, pagina.TamanhoSeguro };
}

/// <summary>
/// A paginação que o web e o Android mandam — `?pagina=2&tamanhoPagina=50` — tem de chegar
/// à ação. O binder de fábrica, vendo a chave `pagina`, passava a exigir o prefixo
/// `pagina.` e ignorava as chaves soltas: toda listagem voltava a página 1 com 20 itens.
///
/// Sobe um Kestrel de verdade, só com a ação acima: é o pipeline de model binding inteiro
/// que está em teste, e não o binder chamado à mão.
/// </summary>
public class ParametrosDePaginaTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers()
            .ConfigureApplicationPartManager(partes =>
            {
                partes.ApplicationParts.Clear();
                partes.ApplicationParts.Add(new AssemblyPart(typeof(EcoDaPaginaController).Assembly));
            });

        _app = builder.Build();
        _app.MapControllers();
        await _app.StartAsync();

        var endereco = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(endereco) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<int[]> EcoAsync(string query) =>
        (await _http.GetFromJsonAsync<int[]>("eco-da-pagina" + query))!;

    [Theory]
    [InlineData("?pagina=2&tamanhoPagina=50", 2, 50)]
    [InlineData("?tamanhoPagina=50&pagina=3", 3, 50)]
    [InlineData("?pagina=4", 4, 20)]
    [InlineData("?tamanhoPagina=10", 1, 10)]
    [InlineData("?pagina.pagina=2&pagina.tamanhoPagina=30", 2, 30)]
    public async Task Chaves_soltas_e_com_prefixo_chegam_a_acao(string query, int pagina, int tamanho)
    {
        Assert.Equal(new[] { pagina, tamanho }, await EcoAsync(query));
    }

    [Fact]
    public async Task Sem_paginacao_na_query_a_acao_usa_o_padrao_dela()
    {
        Assert.Equal(new[] { 0, 0 }, await EcoAsync(string.Empty));
    }

    [Fact]
    public async Task Numero_invalido_e_400()
    {
        var resposta = await _http.GetAsync("eco-da-pagina?pagina=abc");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }
}

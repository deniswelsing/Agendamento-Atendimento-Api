using System.Reflection;
using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Controllers;
using AgendamentoAtendimento.Domain.Usuarios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// Permissão que só existe na tela não é permissão. O app desenha o que a Api manda, e
/// esconder um botão não impede ninguém de chamar a rota — então toda ação precisa ser
/// checada aqui, no servidor.
///
/// Este teste varre os controllers por reflexão: uma rota nova nasce protegida ou o
/// teste quebra. É de propósito que a lista de exceções seja explícita e curta.
/// </summary>
public class RotasProtegidasTests
{
    /// <summary>
    /// As rotas que não têm — e não devem ter — permissão. Cada uma com o motivo: quem
    /// acrescentar algo aqui está dizendo, por escrito, que a rota responde sem checar.
    /// </summary>
    private static readonly Dictionary<string, string> SemPermissaoPorProjeto = new()
    {
        ["AuthController.Login"] = "ninguém tem permissão antes de entrar",
        ["AuthController.Refresh"] = "renova a sessão de quem já entrou, pelo token",
        ["AuthController.Logout"] = "sair nunca pode depender de permissão",
        ["BootstrapController.Obter"] = "é o que DIZ ao app quais permissões o usuário tem",
        ["PerfisController.Catalogo"] = "catálogo de chaves, não dados da empresa",
        ["AssinaturaController.Recursos"] = "catálogo de recursos dos planos, não dados",
        ["PublicoController.Info"] = "página pública: responde sem conta, por definição",
        ["PublicoController.Disponibilidade"] = "página pública",
        ["PublicoController.Agendar"] = "página pública",
        ["PublicoController.Consultar"] = "página pública, protegida pelo código",
        ["PublicoController.Cancelar"] = "página pública, protegida pelo código",
        ["PublicoController.Confirmar"] = "página pública, protegida pelo código",
    };

    private static IEnumerable<(Type Controller, MethodInfo Acao)> Acoes() =>
        typeof(AgendamentosController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Select(m => (t, m)));

    [Fact]
    public void Toda_rota_exige_permissao_ou_esta_na_lista_de_excecoes()
    {
        var desprotegidas = Acoes()
            .Where(x => x.Acao.GetCustomAttribute<RequerPermissaoAttribute>() is null)
            .Select(x => $"{x.Controller.Name}.{x.Acao.Name}")
            .Where(nome => !SemPermissaoPorProjeto.ContainsKey(nome))
            .OrderBy(n => n)
            .ToList();

        Assert.True(desprotegidas.Count == 0,
            "Rota sem [RequerPermissao]: " + string.Join(", ", desprotegidas));
    }

    [Fact]
    public void A_lista_de_excecoes_nao_guarda_rota_que_deixou_de_existir()
    {
        // Exceção que sobra vira licença esquecida: no dia em que alguém criar um método
        // com esse nome, ele nasce desprotegido e ninguém percebe.
        var existentes = Acoes().Select(x => $"{x.Controller.Name}.{x.Acao.Name}").ToHashSet();
        var sobrando = SemPermissaoPorProjeto.Keys.Where(k => !existentes.Contains(k)).ToList();

        Assert.True(sobrando.Count == 0, "Exceção para rota inexistente: " + string.Join(", ", sobrando));
    }

    [Fact]
    public void Toda_permissao_exigida_existe_no_catalogo()
    {
        // Exigir uma chave que o catálogo não tem trancaria a rota para todo mundo menos
        // o admin, sem o dono da empresa ter onde ligá-la.
        var desconhecidas = Acoes()
            .Select(x => x.Acao.GetCustomAttribute<RequerPermissaoAttribute>()?.Policy)
            .Where(p => p is not null)
            .Select(p => p![RequerPermissaoAttribute.Prefixo.Length..])
            .Where(chave => !Permissoes.Existe(chave))
            .Distinct()
            .ToList();

        Assert.True(desconhecidas.Count == 0,
            "Permissão fora do catálogo: " + string.Join(", ", desconhecidas));
    }

    [Fact]
    public void Rota_publica_e_declarada_como_anonima()
    {
        // Sem [AllowAnonymous] ela só não pede permissão — continua pedindo token, e a
        // página do cliente quebraria em produção sem ninguém ver aqui.
        var publicas = Acoes().Where(x => x.Controller.Name == "PublicoController");

        Assert.All(publicas, x => Assert.True(
            x.Controller.GetCustomAttribute<AllowAnonymousAttribute>() is not null
            || x.Acao.GetCustomAttribute<AllowAnonymousAttribute>() is not null,
            $"{x.Controller.Name}.{x.Acao.Name} não é anônima"));
    }
}

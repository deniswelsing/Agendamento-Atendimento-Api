using System.Security.Claims;
using AgendamentoAtendimento.Domain.Usuarios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace AgendamentoAtendimento.Api.Autenticacao;

/// <summary>Exige uma permissão do catálogo, ex.: <c>[RequerPermissao("clientes.editar")]</c>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequerPermissaoAttribute : AuthorizeAttribute
{
    public const string Prefixo = "perm:";

    public RequerPermissaoAttribute(string permissao) => Policy = Prefixo + permissao;
}

public sealed class PermissaoRequirement : IAuthorizationRequirement
{
    public PermissaoRequirement(string permissao) => Permissao = permissao;

    public string Permissao { get; }
}

public sealed class PermissaoHandler : AuthorizationHandler<PermissaoRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissaoRequirement requirement)
    {
        var permissoes = context.User.FindAll(ClaimsApp.Permissao).Select(c => c.Value).ToList();

        // O perfil administrador carrega a permissão coringa: vê tudo e faz tudo.
        if (Permissoes.Permite(permissoes, requirement.Permissao))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Cria as policies `perm:<chave>` sob demanda, sem registrar uma a uma.</summary>
public sealed class PermissaoPolicyProvider : DefaultAuthorizationPolicyProvider
{
    public PermissaoPolicyProvider(IOptions<AuthorizationOptions> opcoes) : base(opcoes) { }

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(RequerPermissaoAttribute.Prefixo, StringComparison.Ordinal))
        {
            return await base.GetPolicyAsync(policyName);
        }

        var permissao = policyName[RequerPermissaoAttribute.Prefixo.Length..];
        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissaoRequirement(permissao))
            .Build();
    }
}

public static class ClaimsPrincipalExtensions
{
    public static long? ObterLong(this ClaimsPrincipal principal, string tipo) =>
        long.TryParse(principal.FindFirstValue(tipo), out var valor) ? valor : null;

    public static IReadOnlyList<string> Permissoes(this ClaimsPrincipal principal) =>
        principal.FindAll(ClaimsApp.Permissao).Select(c => c.Value).ToList();
}

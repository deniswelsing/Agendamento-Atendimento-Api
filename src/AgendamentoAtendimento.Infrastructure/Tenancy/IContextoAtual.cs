namespace AgendamentoAtendimento.Infrastructure.Tenancy;

/// <summary>
/// Quem está chamando a Api agora. É preenchido pelo middleware a partir do token e do
/// cabeçalho `X-Produto`, e é o que alimenta o filtro global de tenant do DbContext.
/// </summary>
public interface IContextoAtual
{
    long? TenantId { get; }
    long? UsuarioId { get; }
    string? TenantSlug { get; }
    string? Produto { get; }
    string? Ip { get; }

    /// <summary>
    /// Desliga o filtro de tenant. Usado só por rotina de sistema (webhook, job) que
    /// precisa enxergar vários tenants.
    /// </summary>
    bool IgnorarFiltroDeTenant { get; }
}

/// <summary>Implementação mutável usada pelo middleware e pelos jobs.</summary>
public sealed class ContextoAtual : IContextoAtual
{
    public long? TenantId { get; set; }
    public long? UsuarioId { get; set; }
    public string? TenantSlug { get; set; }
    public string? Produto { get; set; }
    public string? Ip { get; set; }
    public bool IgnorarFiltroDeTenant { get; set; }

    /// <summary>Assume um tenant durante o processamento de um webhook ou job.</summary>
    public void AssumirTenant(long tenantId, string? slug = null)
    {
        TenantId = tenantId;
        TenantSlug = slug;
    }
}

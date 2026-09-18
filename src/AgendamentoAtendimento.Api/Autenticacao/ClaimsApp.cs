namespace AgendamentoAtendimento.Api.Autenticacao;

/// <summary>Claims próprias do token emitido pela Api.</summary>
public static class ClaimsApp
{
    public const string TenantId = "tenant_id";
    public const string TenantSlug = "tenant_slug";
    public const string UsuarioId = "usuario_id";
    public const string Produto = "produto";
    public const string Permissao = "perm";
    public const string Perfil = "perfil";
}

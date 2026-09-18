using AgendamentoAtendimento.Domain.Common;

namespace AgendamentoAtendimento.Domain.Usuarios;

/// <summary>Usuário do tenant. Cada usuário ativo ocupa um assento da assinatura.</summary>
public class Usuario : EntidadeDeTenant
{
    public required string Nome { get; set; }
    public required string Email { get; set; }

    /// <summary>Hash PBKDF2 no formato `iteracoes.salt.hash` (base64).</summary>
    public string? SenhaHash { get; set; }

    public long PerfilId { get; set; }
    public Perfil? Perfil { get; set; }

    public bool Ativo { get; set; } = true;

    /// <summary>
    /// Usuário que ainda não aceitou o convite. Ele já ocupa assento: é assim que o
    /// PetShop.Route cobra e evita que o time estoure o plano depois do aceite.
    /// </summary>
    public bool ConvitePendente { get; set; }

    public string? TokenConvite { get; set; }
    public DateTimeOffset? ConviteExpiraEm { get; set; }

    /// <summary>Usuário técnico (integração) não ocupa assento.</summary>
    public bool OcupaAssento { get; set; } = true;

    public string? FotoUrl { get; set; }
    public DateTimeOffset? UltimoLoginEm { get; set; }
    public string? UltimoLoginIp { get; set; }

    /// <summary>Atendente: aparece como responsável na agenda.</summary>
    public bool Atendente { get; set; } = true;
}

/// <summary>Refresh token emitido no login. Vale para todos os produtos da suíte.</summary>
public class RefreshToken : EntidadeDeTenant
{
    public long UsuarioId { get; set; }
    public Usuario? Usuario { get; set; }

    /// <summary>SHA-256 do token — o valor em claro só existe na resposta do login.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset ExpiraEm { get; set; }
    public DateTimeOffset? RevogadoEm { get; set; }

    /// <summary>Produto que originou o token (`agendamento-atendimento`, `petshop-route`).</summary>
    public required string ProdutoOrigem { get; set; }

    public string? CriadoPorIp { get; set; }

    public bool Ativo => RevogadoEm is null && ExpiraEm > DateTimeOffset.UtcNow;
}

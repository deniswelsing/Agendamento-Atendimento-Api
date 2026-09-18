using AgendamentoAtendimento.Domain.Common;

namespace AgendamentoAtendimento.Domain.Usuarios;

/// <summary>Perfil de acesso (Administrador, Atendimento, Financeiro...).</summary>
public class Perfil : EntidadeDeTenant
{
    public required string Nome { get; set; }
    public string? Descricao { get; set; }

    /// <summary>Perfil de sistema não pode ser removido pelo tenant.</summary>
    public bool DeSistema { get; set; }

    /// <summary>Um perfil administrador recebe a permissão coringa.</summary>
    public bool Administrador { get; set; }

    public ICollection<PerfilPermissao> Permissoes { get; set; } = new List<PerfilPermissao>();
    public ICollection<Usuario> Usuarios { get; set; } = new List<Usuario>();
}

public class PerfilPermissao : EntidadeDeTenant
{
    public long PerfilId { get; set; }
    public Perfil? Perfil { get; set; }

    /// <summary>Chave da permissão, ex.: `clientes.editar`. Ver <see cref="Permissoes"/>.</summary>
    public required string Permissao { get; set; }
}


using AgendamentoAtendimento.Domain.Common;

namespace AgendamentoAtendimento.Domain.Auditoria;

/// <summary>
/// Trilha de auditoria gravada pelo DbContext em toda inserção, alteração e exclusão de
/// entidade de tenant. Guardar isso é requisito de qualquer operação que mexe com dinheiro.
/// </summary>
public class AuditLog : Entidade
{
    public long TenantId { get; set; }
    public long? UsuarioId { get; set; }
    public required string Entidade { get; set; }
    public required string ChaveEntidade { get; set; }

    /// <summary>INSERT, UPDATE ou DELETE.</summary>
    public required string Operacao { get; set; }

    /// <summary>Colunas alteradas e seus valores, em JSON.</summary>
    public string? Alteracoes { get; set; }

    public string? Produto { get; set; }
    public string? Ip { get; set; }
}

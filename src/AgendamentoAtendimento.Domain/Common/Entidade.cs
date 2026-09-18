namespace AgendamentoAtendimento.Domain.Common;

/// <summary>Raiz de toda entidade persistida.</summary>
public abstract class Entidade
{
    public long Id { get; set; }
    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AtualizadoEm { get; set; }
    public long? CriadoPorId { get; set; }
    public long? AtualizadoPorId { get; set; }
}

/// <summary>
/// Entidade que pertence a um tenant. O <see cref="TenantId"/> é preenchido e filtrado
/// automaticamente pelo DbContext — nenhum serviço precisa lembrar de fazer isso.
/// </summary>
public interface IEntidadeDeTenant
{
    long TenantId { get; set; }
}

/// <summary>Entidade com exclusão lógica.</summary>
public interface IExclusaoLogica
{
    bool Excluido { get; set; }
    DateTimeOffset? ExcluidoEm { get; set; }
}

public abstract class EntidadeDeTenant : Entidade, IEntidadeDeTenant, IExclusaoLogica
{
    public long TenantId { get; set; }
    public bool Excluido { get; set; }
    public DateTimeOffset? ExcluidoEm { get; set; }
}

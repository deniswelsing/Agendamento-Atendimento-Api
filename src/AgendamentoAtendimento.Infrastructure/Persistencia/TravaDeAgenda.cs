using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AgendamentoAtendimento.Infrastructure.Persistencia;

/// <summary>
/// A trava da agenda de uma empresa. Todo caminho que grava ou move um agendamento
/// confere a disponibilidade e depois grava — e, sem trava, duas requisições ao mesmo
/// tempo passavam as duas pela conferência antes de qualquer uma gravar: a mesma pessoa
/// no mesmo horário, duas vezes.
///
/// A trava é um advisory lock do PostgreSQL com escopo de transação
/// (<c>pg_advisory_xact_lock</c>), uma por tenant: quem chega depois espera o primeiro
/// confirmar, e aí a conferência dele já enxerga o que o primeiro gravou. Ela solta
/// sozinha no commit ou no rollback — não há como esquecer uma trava presa.
/// </summary>
/// <remarks>
/// Uso: pegar a trava ANTES de conferir a disponibilidade, gravar, e confirmar:
/// <code>
/// await using var trava = await _db.TravarAgendaAsync(ct);
/// // ... confere, monta, SaveChanges ...
/// await trava.ConfirmarAsync(ct);
/// </code>
/// Fora do PostgreSQL (o provedor em memória dos testes) não há SQL nem transação de
/// verdade: a trava vira um no-op, e o resto do fluxo segue igual.
/// </remarks>
public static class TravaDeAgenda
{
    /// <summary>
    /// Espaço da chave (primeiro inteiro do lock de duas chaves): "AGND". Separa esta
    /// trava de qualquer outro advisory lock que o banco venha a ter.
    /// </summary>
    public const int Espaco = 0x41474E44;

    public static async Task<TransacaoDaAgenda> TravarAgendaAsync(
        this AppDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (!db.Database.IsNpgsql())
        {
            return TransacaoDaAgenda.Nenhuma;
        }

        var tenantId = db.Contexto.TenantId
            ?? throw new InvalidOperationException("Travar a agenda exige um tenant no contexto.");

        // Já dentro de uma transação de quem chamou: a trava entra nela e solta junto.
        var propria = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        try
        {
            // Id acima de int só aconteceria com bilhões de empresas; truncar ali só faria
            // duas delas dividirem a fila, nunca furar a trava.
            var chave = unchecked((int)tenantId);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({Espaco}, {chave})", ct);
        }
        catch
        {
            if (propria is not null)
            {
                await propria.DisposeAsync();
            }
            throw;
        }

        return new TransacaoDaAgenda(propria);
    }
}

/// <summary>
/// A transação que segura a trava. Descartar sem <see cref="ConfirmarAsync"/> desfaz
/// tudo e solta a trava — é o que acontece quando a conferência recusa o horário.
/// </summary>
public sealed class TransacaoDaAgenda : IAsyncDisposable
{
    public static readonly TransacaoDaAgenda Nenhuma = new(null);

    private readonly IDbContextTransaction? _transacao;

    internal TransacaoDaAgenda(IDbContextTransaction? transacao) => _transacao = transacao;

    public Task ConfirmarAsync(CancellationToken ct = default) =>
        _transacao?.CommitAsync(ct) ?? Task.CompletedTask;

    public ValueTask DisposeAsync() => _transacao?.DisposeAsync() ?? ValueTask.CompletedTask;
}

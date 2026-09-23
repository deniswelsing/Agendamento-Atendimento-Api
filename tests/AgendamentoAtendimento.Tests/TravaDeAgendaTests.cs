using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// A trava da agenda: conferir a disponibilidade e gravar, com duas requisições ao mesmo
/// tempo, marcava a mesma pessoa duas vezes no mesmo horário. A trava é um advisory lock
/// do PostgreSQL por empresa, com escopo de transação.
/// </summary>
public class TravaDeAgendaTests
{
    /// <summary>
    /// Banco de verdade para o teste da trava. Sem a variável, o teste só confere o
    /// provedor em memória. Ex.: `AGENDAMENTO_TESTE_PG="Host=localhost;Database=agendamento_x;Username=agendamento;Password=agendamento"`.
    /// </summary>
    private static readonly string? Conexao = Environment.GetEnvironmentVariable("AGENDAMENTO_TESTE_PG");

    [Fact]
    public async Task Fora_do_postgres_a_trava_nao_atrapalha()
    {
        var contexto = new ContextoAtual { TenantId = 1 };
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"trava-{Guid.NewGuid()}").Options,
            contexto);

        await using (var trava = await db.TravarAgendaAsync())
        {
            await trava.ConfirmarAsync();
        }

        // Descartar sem confirmar também não quebra.
        await using var outra = await db.TravarAgendaAsync();
    }

    private static AppDbContext NoPostgres(long tenantId) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Conexao).Options,
            new ContextoAtual { TenantId = tenantId });

    private static Task<bool> TentarTravarAsync(AppDbContext db, long tenantId) =>
        db.Database.SqlQuery<bool>(
                $"SELECT pg_try_advisory_xact_lock({TravaDeAgenda.Espaco}, {(int)tenantId}) AS \"Value\"")
            .SingleAsync();

    [Fact]
    public async Task No_postgres_segura_a_empresa_ate_a_transacao_acabar()
    {
        if (string.IsNullOrWhiteSpace(Conexao))
        {
            return; // sem banco configurado: coberto pelo script de concorrência (ver docs)
        }

        await using var primeiro = NoPostgres(1);
        await using var segundo = NoPostgres(1);
        await using var outraEmpresa = NoPostgres(2);

        await using (var trava = await primeiro.TravarAgendaAsync())
        {
            await using (var tentativa = await segundo.Database.BeginTransactionAsync())
            {
                Assert.False(await TentarTravarAsync(segundo, 1));
            }

            // A trava é por empresa: a agenda de outra segue livre.
            await using (var tentativa = await outraEmpresa.Database.BeginTransactionAsync())
            {
                Assert.True(await TentarTravarAsync(outraEmpresa, 2));
            }

            await trava.ConfirmarAsync();
            // Confirmada, a transação sai do contexto: o que o caminho grava depois (os
            // lembretes, por exemplo) já não fica preso nela.
            Assert.Null(primeiro.Database.CurrentTransaction);
        }

        // Confirmou: a trava soltou sozinha.
        await using (var depois = await segundo.Database.BeginTransactionAsync())
        {
            Assert.True(await TentarTravarAsync(segundo, 1));
        }
    }

    [Fact]
    public async Task No_postgres_quem_chega_depois_espera_e_ve_o_que_o_primeiro_gravou()
    {
        if (string.IsNullOrWhiteSpace(Conexao))
        {
            return;
        }

        await using var primeiro = NoPostgres(1);
        await using var segundo = NoPostgres(1);

        var trava = await primeiro.TravarAgendaAsync();
        var esperando = Task.Run(async () =>
        {
            await using var dele = await segundo.TravarAgendaAsync();
            return DateTimeOffset.UtcNow;
        });

        await Task.Delay(500);
        Assert.False(esperando.IsCompleted);

        var soltouEm = DateTimeOffset.UtcNow;
        await trava.ConfirmarAsync();
        await trava.DisposeAsync();

        Assert.True(await esperando >= soltouEm);
    }
}

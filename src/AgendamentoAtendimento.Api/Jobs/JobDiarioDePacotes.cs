using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgendamentoAtendimento.Api.Jobs;

/// <summary>
/// A varredura diária dos pacotes, rodando sozinha.
///
/// Passa por CADA tenant, porque o filtro global de tenant é o que mantém uma empresa
/// invisível para a outra: uma varredura "global" ou não enxergaria nada, ou enxergaria
/// tudo — e a segunda hipótese é pior.
///
/// A hora é fixa e cedo (03:00 UTC): o aviso precisa estar pronto quando a recepção
/// abre, não no meio do expediente. Se o processo subir depois da hora, a varredura do
/// dia acontece na subida — um dia sem aviso é pior que um aviso atrasado, e a operação
/// é idempotente.
///
/// A HORA da varredura é um instante (03:00 UTC, meia-noite de Brasília), mas o "hoje"
/// com que cada empresa é varrida é o do fuso dela — é ele que diz se o ciclo já venceu.
/// </summary>
public class JobDiarioDePacotes : BackgroundService
{
    /// <summary>Hora UTC da varredura. Antes de qualquer empresa abrir.</summary>
    public static readonly TimeOnly HoraDaVarredura = new(3, 0);

    private readonly IServiceScopeFactory _escopos;
    private readonly ILogger<JobDiarioDePacotes> _log;

    public JobDiarioDePacotes(IServiceScopeFactory escopos, ILogger<JobDiarioDePacotes> log)
    {
        _escopos = escopos;
        _log = log;
    }

    /// <summary>
    /// Quanto falta para a próxima varredura. Zero nunca: dormir zero num laço seria
    /// varrer em círculo até o processo cair.
    /// </summary>
    public static TimeSpan AteAProxima(DateTimeOffset agora)
    {
        var hoje = agora.UtcDateTime.Date.Add(HoraDaVarredura.ToTimeSpan());
        var alvo = agora.UtcDateTime < hoje ? hoje : hoje.AddDays(1);
        return alvo - agora.UtcDateTime;
    }

    protected override async Task ExecuteAsync(CancellationToken parada)
    {
        // Uma varredura na subida: o processo pode ter passado a hora reiniciando, e um
        // dia sem aviso é pior que um aviso fora de hora.
        await VarrerTudoAsync(parada);

        while (!parada.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(AteAProxima(DateTimeOffset.UtcNow), parada);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await VarrerTudoAsync(parada);
        }
    }

    private async Task VarrerTudoAsync(CancellationToken ct)
    {
        try
        {
            using var escopo = _escopos.CreateScope();
            var contexto = escopo.ServiceProvider.GetRequiredService<ContextoAtual>();
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
            var servico = escopo.ServiceProvider
                .GetRequiredService<RecorrenciaDePacotesService>();
            var relogio = escopo.ServiceProvider.GetRequiredService<RelogioDoTenant>();

            contexto.IgnorarFiltroDeTenant = true;
            var tenants = await db.Tenants.AsNoTracking()
                .Select(t => new { t.Id, t.Slug }).ToListAsync(ct);
            contexto.IgnorarFiltroDeTenant = false;

            foreach (var tenant in tenants)
            {
                contexto.AssumirTenant(tenant.Id, tenant.Slug);
                try
                {
                    // "Hoje" é o de cada empresa: o relógio segue o tenant que o contexto
                    // acabou de assumir, e cada uma vira o ciclo no próprio calendário.
                    var r = await servico.VarrerAsync(relogio.Hoje(), ct);

                    if (r.Avisos.Count > 0 || r.CiclosEncerrados > 0)
                    {
                        _log.LogInformation(
                            "Pacotes {Slug}: {Avisos} a vencer, {Fechados} ciclo(s) encerrado(s), "
                            + "{Estornos} estorno(s) somando {Valor}.",
                            tenant.Slug, r.Avisos.Count, r.CiclosEncerrados,
                            r.EstornosGerados, r.ValorEstornado);
                    }
                }
                catch (Exception erro) when (erro is not OperationCanceledException)
                {
                    // Um tenant com dado estranho não pode derrubar a varredura dos outros:
                    // o erro fica no log dele, e o laço segue para o próximo.
                    _log.LogError(erro, "A varredura de pacotes falhou em {Slug}.", tenant.Slug);
                }
                finally
                {
                    // O que ficou rastreado (inclusive a alteração que falhou) é deste
                    // tenant: não pode ir junto no SaveChanges do próximo.
                    db.ChangeTracker.Clear();
                }
            }
        }
        catch (Exception erro)
        {
            // Falha fora de um tenant (a lista de tenants, o escopo) não derruba o
            // processo: o job volta amanhã, e a operação é idempotente.
            _log.LogError(erro, "A varredura diária de pacotes falhou.");
        }
    }
}

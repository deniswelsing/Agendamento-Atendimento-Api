using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgendamentoAtendimento.Api.Jobs;

/// <summary>
/// O que precisa acontecer sozinho, de minuto em minuto: mandar os lembretes que venceram
/// e fechar as cobranças que passaram do prazo.
///
/// Os lembretes eram programados e ficavam "na fila" para sempre: só saíam se alguém
/// clicasse em "Despachar a fila agora", e passada a tolerância morriam sem ter saído. E a
/// cobrança que o app largou no meio continuava "em andamento" até alguém tentar mexer.
///
/// Como a varredura dos pacotes, passa por CADA tenant: o filtro global de tenant é o que
/// mantém uma empresa invisível para a outra.
/// </summary>
public class JobDeRotina : BackgroundService
{
    /// <summary>De quanto em quanto tempo. O lembrete tem tolerância de horas; um minuto sobra.</summary>
    public static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _escopos;
    private readonly ILogger<JobDeRotina> _log;

    public JobDeRotina(IServiceScopeFactory escopos, ILogger<JobDeRotina> log)
    {
        _escopos = escopos;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken parada)
    {
        while (!parada.IsCancellationRequested)
        {
            await RodarAsync(parada);

            try
            {
                await Task.Delay(Intervalo, parada);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RodarAsync(CancellationToken ct)
    {
        try
        {
            using var escopo = _escopos.CreateScope();
            var contexto = escopo.ServiceProvider.GetRequiredService<ContextoAtual>();
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
            var lembretes = escopo.ServiceProvider.GetRequiredService<LembreteService>();
            var cobrancas = escopo.ServiceProvider.GetRequiredService<CobrancaService>();

            contexto.IgnorarFiltroDeTenant = true;
            var tenants = await db.Tenants.AsNoTracking()
                .Select(t => new { t.Id, t.Slug }).ToListAsync(ct);
            contexto.IgnorarFiltroDeTenant = false;

            foreach (var tenant in tenants)
            {
                contexto.AssumirTenant(tenant.Id, tenant.Slug);
                try
                {
                    await cobrancas.ExpirarVencidasAsync(ct);

                    // Lembrete desligado não sai sozinho, nem o que já estava na fila:
                    // quem desliga não quer mais e-mail saindo em nome da empresa.
                    if ((await lembretes.ConfiguracaoAsync(ct)).Ativo)
                    {
                        var (enviados, falharam, expirados) =
                            await lembretes.DespacharAsync(DateTimeOffset.UtcNow, ct);
                        if (enviados + falharam + expirados > 0)
                        {
                            _log.LogInformation(
                                "Lembretes {Slug}: {Enviados} enviado(s), {Falharam} com falha, "
                                + "{Expirados} vencido(s) sem envio.",
                                tenant.Slug, enviados, falharam, expirados);
                        }
                    }
                }
                catch (Exception erro) when (erro is not OperationCanceledException)
                {
                    // Um tenant com dado estranho não para a rotina dos outros.
                    _log.LogError(erro, "A rotina falhou em {Slug}.", tenant.Slug);
                }
                finally
                {
                    db.ChangeTracker.Clear();
                }
            }
        }
        catch (Exception erro) when (erro is not OperationCanceledException)
        {
            _log.LogError(erro, "A rotina de minuto falhou.");
        }
    }
}

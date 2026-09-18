using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>Números do painel. Contados no banco, nunca no aplicativo.</summary>
[Route("api/dashboard")]
public class DashboardController : ControllerBaseApi
{
    private readonly AppDbContext _db;

    public DashboardController(AppDbContext db) => _db = db;

    [HttpGet("resumo")]
    [RequerPermissao("dashboard.ver")]
    public async Task<ActionResult<ResumoDashboardDto>> Resumo(
        [FromQuery] DateOnly? data, CancellationToken ct = default)
    {
        var dia = data ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var inicioDia = new DateTimeOffset(dia.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var fimDia = inicioDia.AddDays(1);
        var inicioMes = new DateTimeOffset(
            new DateOnly(dia.Year, dia.Month, 1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var agendamentosDoDia = await _db.Agendamentos
            .AsNoTracking()
            .Where(a => a.Inicio < fimDia && a.Fim > inicioDia && a.Status != StatusAgendamento.Cancelado)
            .Select(a => a.Status)
            .ToListAsync(ct);

        var vendasDoMes = await _db.Vendas
            .AsNoTracking()
            .Where(v => v.CriadoEm >= inicioMes && v.CriadoEm < fimDia)
            .Select(v => new { v.CriadoEm, v.Status, v.TotalLiquido })
            .ToListAsync(ct);

        var pagasNoDia = vendasDoMes
            .Where(v => v.Status == StatusVenda.Paga && v.CriadoEm >= inicioDia)
            .ToList();
        var pagasNoMes = vendasDoMes.Where(v => v.Status == StatusVenda.Paga).ToList();

        var tenant = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == TenantId, ct);

        return Ok(new ResumoDashboardDto(
            Data: dia,
            AgendamentosHoje: agendamentosDoDia.Count,
            AgendamentosConfirmados: agendamentosDoDia.Count(s => s == StatusAgendamento.Confirmado),
            AtendimentosConcluidos: agendamentosDoDia.Count(s => s == StatusAgendamento.Concluido),
            ClientesAtivos: await _db.Clientes.CountAsync(c => c.Ativo, ct),
            ClientesEmpresa: await _db.Clientes.CountAsync(c => c.Ativo && c.Tipo == TipoCliente.Empresa, ct),
            FaturamentoHoje: pagasNoDia.Sum(v => v.TotalLiquido),
            FaturamentoMes: pagasNoMes.Sum(v => v.TotalLiquido),
            TicketMedio: pagasNoMes.Count == 0
                ? 0m
                : decimal.Round(pagasNoMes.Sum(v => v.TotalLiquido) / pagasNoMes.Count, 2,
                    MidpointRounding.AwayFromZero),
            VendasEmAberto: vendasDoMes.Count(v =>
                v.Status is StatusVenda.Aberta or StatusVenda.AguardandoPagamento),
            Moeda: tenant.Moeda));
    }
}

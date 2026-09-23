using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>Números do painel. Contados no banco, nunca no aplicativo.</summary>
[Route("api/dashboard")]
public class DashboardController : ControllerBaseApi
{
    private readonly AppDbContext _db;
    private readonly RelogioDoTenant _relogio;

    public DashboardController(AppDbContext db, RelogioDoTenant relogio)
    {
        _db = db;
        _relogio = relogio;
    }

    [HttpGet("resumo")]
    [RequerPermissao("dashboard.ver")]
    public async Task<ActionResult<ResumoDashboardDto>> Resumo(
        [FromQuery] DateOnly? data, CancellationToken ct = default)
    {
        // "Hoje" e "este mês" são os do calendário da empresa, com as fronteiras na
        // meia-noite de lá.
        var dia = data ?? _relogio.Hoje();
        var inicioDia = _relogio.InicioDoDia(dia);
        var fimDia = _relogio.FimDoDia(dia);
        var inicioMes = _relogio.InicioDoDia(new DateOnly(dia.Year, dia.Month, 1));

        // Os contadores seguem a mesma regra da agenda: quem só enxerga os próprios
        // atendimentos não pode ler pelo painel quantos a empresa tem hoje.
        var vejoTudo = VisibilidadeDaAgenda.VeTudo(PermissoesDoUsuario);

        var agendamentosDoDia = await VisibilidadeDaAgenda
            .Aplicar(_db.Agendamentos.AsNoTracking(), PermissoesDoUsuario, UsuarioId)
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
            // A tela precisa saber de quem são os números que está mostrando.
            AgendaDeTodoOTime: vejoTudo,
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

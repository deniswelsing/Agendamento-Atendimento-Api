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

        // Faturamento é dinheiro que entrou: os recebimentos confirmados no dia (e no mês),
        // menos o que foi estornado no mesmo período. Antes contava a venda pela data em
        // que foi CRIADA e só quando já estava quitada: um recebimento parcial não aparecia,
        // e a venda de ontem paga hoje entrava no dia de ontem.
        var recebidos = await _db.Pagamentos.AsNoTracking()
            .Where(p => (p.Status == StatusPagamento.Confirmado || p.Status == StatusPagamento.Estornado)
                        && p.ConfirmadoEm >= inicioMes && p.ConfirmadoEm < fimDia)
            .Select(p => new { p.VendaId, p.Valor, ConfirmadoEm = p.ConfirmadoEm!.Value })
            .ToListAsync(ct);
        var estornados = await _db.Pagamentos.AsNoTracking()
            .Where(p => p.Status == StatusPagamento.Estornado
                        && p.EstornadoEm >= inicioMes && p.EstornadoEm < fimDia)
            .Select(p => new { p.Valor, EstornadoEm = p.EstornadoEm!.Value })
            .ToListAsync(ct);

        var faturamentoHoje =
            recebidos.Where(p => p.ConfirmadoEm >= inicioDia).Sum(p => p.Valor)
            - estornados.Where(p => p.EstornadoEm >= inicioDia).Sum(p => p.Valor);
        var faturamentoMes = recebidos.Sum(p => p.Valor) - estornados.Sum(p => p.Valor);
        var vendasQueReceberam = recebidos.Select(p => p.VendaId).Distinct().Count();

        // Em aberto é toda venda que ainda tem o que receber — de qualquer mês: a de agosto
        // que ninguém pagou continua devendo em setembro.
        var vendasEmAberto = await _db.Vendas.AsNoTracking()
            .CountAsync(v => (v.Status == StatusVenda.Aberta || v.Status == StatusVenda.AguardandoPagamento)
                             && v.TotalLiquido > v.TotalPago, ct);

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
            FaturamentoHoje: faturamentoHoje,
            FaturamentoMes: faturamentoMes,
            // O que entrou no mês dividido pelas vendas que receberam no mês.
            TicketMedio: vendasQueReceberam == 0
                ? 0m
                : decimal.Round(faturamentoMes / vendasQueReceberam, 2, MidpointRounding.AwayFromZero),
            VendasEmAberto: vendasEmAberto,
            Moeda: tenant.Moeda));
    }
}

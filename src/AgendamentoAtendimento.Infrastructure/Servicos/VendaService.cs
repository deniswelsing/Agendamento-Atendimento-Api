using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Infrastructure.Servicos;

/// <summary>
/// Totais e recebimentos da venda. Os valores são sempre recalculados a partir dos itens —
/// o app manda quantidade e desconto, nunca o total.
/// </summary>
public class VendaService
{
    private readonly AppDbContext _db;

    public VendaService(AppDbContext db) => _db = db;

    public void RecalcularTotais(Venda venda)
    {
        ArgumentNullException.ThrowIfNull(venda);

        venda.TotalBruto = Arredondar(venda.Itens.Sum(i => i.TotalBruto));
        var descontoItens = Arredondar(venda.Itens.Sum(i => i.DescontoValor));
        venda.TotalDescontos = Arredondar(descontoItens + venda.DescontoGeral);
        venda.TotalLiquido = Arredondar(Math.Max(0m, venda.TotalBruto - venda.TotalDescontos));
        venda.TotalImpostos = Arredondar(venda.Itens.Sum(i => i.TotalLiquido * i.TaxaPercentual / 100m));
        venda.TotalPago = Arredondar(venda.Pagamentos
            .Where(p => p.Status == StatusPagamento.Confirmado)
            .Sum(p => p.Valor));
        venda.TotalEstornado = Arredondar(venda.Pagamentos
            .Where(p => p.Status == StatusPagamento.Estornado)
            .Sum(p => p.Valor));
    }

    /// <summary>
    /// Confirma um recebimento e ajusta o status da venda. A taxa da forma de pagamento é
    /// descontada aqui para que o líquido registrado seja o que realmente entra no caixa.
    /// </summary>
    public async Task<Pagamento> RegistrarPagamentoAsync(
        Venda venda, long formaPagamentoId, decimal valor, int parcelas = 1,
        string? autorizacao = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(venda);
        if (valor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(valor), "O valor do pagamento deve ser positivo.");
        }

        var forma = await _db.FormasPagamento.FirstOrDefaultAsync(f => f.Id == formaPagamentoId, ct)
            ?? throw new InvalidOperationException("Forma de pagamento não encontrada.");

        var taxa = Arredondar(valor * forma.TaxaPercentual / 100m + forma.TaxaFixa);
        var pagamento = new Pagamento
        {
            VendaId = venda.Id,
            FormaPagamentoId = forma.Id,
            Status = StatusPagamento.Confirmado,
            Valor = Arredondar(valor),
            ValorTaxa = taxa,
            ValorLiquido = Arredondar(valor - taxa),
            Parcela = 1,
            TotalParcelas = Math.Max(1, parcelas),
            ConfirmadoEm = DateTimeOffset.UtcNow,
            PrevisaoLiquidacao = DateOnly.FromDateTime(
                DateTime.UtcNow.AddDays(forma.DiasParaLiquidacao)),
            Autorizacao = autorizacao,
        };

        venda.Pagamentos.Add(pagamento);
        RecalcularTotais(venda);

        venda.Status = venda.TotalPago >= venda.TotalLiquido && venda.TotalLiquido > 0
            ? StatusVenda.Paga
            : StatusVenda.AguardandoPagamento;

        if (venda.Status == StatusVenda.Paga)
        {
            venda.FinalizadaEm = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return pagamento;
    }

    private static decimal Arredondar(decimal valor) =>
        decimal.Round(valor, 2, MidpointRounding.AwayFromZero);
}

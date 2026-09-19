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

        var pagamento = MontarPagamento(venda, forma, valor, parcelas, MeioDeCaptura.Manual);
        pagamento.Autorizacao = autorizacao;

        venda.Pagamentos.Add(pagamento);
        AtualizarStatus(venda);

        await _db.SaveChangesAsync(ct);
        return pagamento;
    }

    /// <summary>
    /// Monta o lançamento com a taxa que a configuração prevê. Enquanto a adquirente não
    /// confirmar, `TaxaConferida` fica falso: o líquido é previsão, não fato.
    /// </summary>
    public Pagamento MontarPagamento(
        Venda venda, FormaPagamento forma, decimal valor, int parcelas, MeioDeCaptura meio)
    {
        ArgumentNullException.ThrowIfNull(venda);
        ArgumentNullException.ThrowIfNull(forma);

        var estimada = Arredondar(valor * forma.TaxaPercentual / 100m + forma.TaxaFixa);
        return new Pagamento
        {
            VendaId = venda.Id,
            FormaPagamentoId = forma.Id,
            Status = StatusPagamento.Confirmado,
            Valor = Arredondar(valor),
            ValorTaxa = estimada,
            ValorTaxaEstimada = estimada,
            TaxaConferida = false,
            ValorLiquido = Arredondar(valor - estimada),
            Parcela = 1,
            TotalParcelas = Math.Max(1, parcelas),
            Meio = meio,
            ConfirmadoEm = DateTimeOffset.UtcNow,
            PrevisaoLiquidacao = DateOnly.FromDateTime(
                DateTime.UtcNow.AddDays(forma.DiasParaLiquidacao)),
        };
    }

    /// <summary>
    /// Troca a taxa estimada pela que a adquirente cobrou de verdade e refaz o líquido.
    /// A estimada continua gravada: é ela que revela a diferença.
    /// </summary>
    public void ConciliarTaxa(Pagamento pagamento, decimal taxaReal)
    {
        ArgumentNullException.ThrowIfNull(pagamento);
        if (taxaReal < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(taxaReal), "A taxa não pode ser negativa.");
        }

        pagamento.ValorTaxa = Arredondar(taxaReal);
        pagamento.ValorLiquido = Arredondar(pagamento.Valor - pagamento.ValorTaxa);
        pagamento.TaxaConferida = true;
        pagamento.ConciliadoEm = DateTimeOffset.UtcNow;
    }

    /// <summary>Ajusta o status da venda a partir do que já foi pago.</summary>
    public void AtualizarStatus(Venda venda)
    {
        ArgumentNullException.ThrowIfNull(venda);

        RecalcularTotais(venda);
        venda.Status = venda.TotalPago >= venda.TotalLiquido && venda.TotalLiquido > 0
            ? StatusVenda.Paga
            : StatusVenda.AguardandoPagamento;

        if (venda.Status == StatusVenda.Paga)
        {
            venda.FinalizadaEm = DateTimeOffset.UtcNow;
        }
    }

    private static decimal Arredondar(decimal valor) =>
        decimal.Round(valor, 2, MidpointRounding.AwayFromZero);
}

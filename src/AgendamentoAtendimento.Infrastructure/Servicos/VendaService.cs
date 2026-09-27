using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Infrastructure.Servicos;

/// <summary>
/// Totais e recebimentos da venda. Os valores são sempre recalculados a partir dos itens —
/// o app manda quantidade e desconto, nunca o total.
/// </summary>
public class VendaService
{
    private readonly AppDbContext _db;
    private readonly RelogioDoTenant _relogio;

    public VendaService(AppDbContext db, RelogioDoTenant relogio)
    {
        _db = db;
        _relogio = relogio;
    }

    /// <summary>
    /// Quem prestou cada serviço do atendimento, unidade a unidade, na ordem em que foram
    /// prestados.
    ///
    /// Uma fila por item do catálogo, e não uma pessoa por item: o mesmo serviço pode
    /// aparecer duas vezes no atendimento com pessoas diferentes — a Ana dá um banho, o
    /// Bruno dá o outro —, e cada uma recebe pelo que fez. Guardar uma pessoa por item
    /// pagaria as duas unidades para a primeira.
    /// </summary>
    /// <returns>Item do catálogo → as pessoas, uma entrada por unidade prestada.</returns>
    public static Dictionary<long, Queue<long>> QuemPrestouPorItem(Agendamento? agendamento)
    {
        var porItem = new Dictionary<long, Queue<long>>();
        if (agendamento is null)
        {
            return porItem;
        }

        foreach (var item in agendamento.Itens.OrderBy(i => i.Ordem).ThenBy(i => i.Id))
        {
            // Serviço sem pessoa própria segue quem responde pelo atendimento; sem os
            // dois, não há a quem creditar, e a unidade fica sem dono.
            var quem = item.ResponsavelId ?? agendamento.ResponsavelId;
            if (quem is not > 0)
            {
                continue;
            }

            if (!porItem.TryGetValue(item.ItemCatalogoId, out var fila))
            {
                porItem[item.ItemCatalogoId] = fila = new Queue<long>();
            }

            for (var unidade = 0; unidade < Math.Max(1, item.Quantidade); unidade++)
            {
                fila.Enqueue(quem.Value);
            }
        }

        return porItem;
    }

    /// <summary>
    /// Como dividir uma linha da venda entre quem prestou: uma parte por pessoa, na ordem
    /// da fila, com o que sobrar sem dono (cai no vendedor da venda na hora de somar).
    /// </summary>
    public static List<(long? Quem, decimal Quantidade)> DividirEntreQuemPrestou(
        Queue<long>? fila, decimal quantidade)
    {
        var partes = new List<(long? Quem, decimal Quantidade)>();
        var restante = quantidade;

        while (restante >= 1m && fila is { Count: > 0 })
        {
            var quem = fila.Dequeue();
            var unidades = 1m;
            // Junta as unidades seguidas da mesma pessoa numa linha só: quebrar em uma
            // linha por unidade encheria a venda de repetição sem dizer nada a mais.
            while (restante - unidades >= 1m && fila.Count > 0 && fila.Peek() == quem)
            {
                fila.Dequeue();
                unidades++;
            }

            partes.Add((quem, unidades));
            restante -= unidades;
        }

        if (restante > 0m)
        {
            if (restante < 1m && partes.Count > 0)
            {
                // Meia unidade não muda quem prestou: a fração fica com a última pessoa
                // da divisão, em vez de virar uma linha órfã sem comissão.
                var ultima = partes[^1];
                partes[^1] = (ultima.Quem, ultima.Quantidade + restante);
            }
            else if (restante < 1m && fila is { Count: > 0 })
            {
                // A linha inteira é menos de uma unidade (meia sessão, por exemplo): ainda
                // assim foi alguém do atendimento que prestou, e é dele a comissão.
                partes.Add((fila.Dequeue(), restante));
            }
            else
            {
                // Unidade inteira que o atendimento não cobre fica sem dono: creditar
                // quem prestou o que ela não prestou é pagar a mais.
                partes.Add((null, restante));
            }
        }

        return partes;
    }

    /// <summary>
    /// Tira da fila as unidades que uma linha com dono já cobre.
    ///
    /// Quando a venda é salva de novo, as linhas que já foram divididas voltam com o seu
    /// dono no pedido. Sem descontar essas unidades, a fila continuaria inteira e a linha
    /// sem dono tomaria de novo a vez de quem já tem a sua — a cada salvamento, mais
    /// unidades no nome da mesma pessoa.
    /// </summary>
    public static void DescontarDaFila(Queue<long>? fila, long quem, decimal quantidade)
    {
        if (fila is not { Count: > 0 })
        {
            return;
        }

        // Uma unidade da fila para cada unidade inteira da linha; uma linha só com
        // fração ainda consome a vez de quem a prestou.
        var aDescontar = Math.Max(1, (int)decimal.Floor(quantidade));
        var restantes = new List<long>(fila.Count);
        foreach (var pessoa in fila)
        {
            if (aDescontar > 0 && pessoa == quem)
            {
                aDescontar--;
                continue;
            }

            restantes.Add(pessoa);
        }

        fila.Clear();
        foreach (var pessoa in restantes)
        {
            fila.Enqueue(pessoa);
        }
    }

    /// <summary>
    /// O desconto de cada parte da linha dividida, proporcional às unidades dela.
    ///
    /// Arredonda o acumulado, e não cada parte: a parte é a diferença entre dois
    /// acumulados arredondados, então nunca fica negativa e a soma das partes é
    /// exatamente o desconto pedido.
    /// </summary>
    public static List<decimal> DividirDesconto(
        decimal descontoDaLinha, IReadOnlyList<decimal> quantidades)
    {
        var descontos = new List<decimal>(quantidades.Count);
        if (quantidades.Count == 1)
        {
            descontos.Add(descontoDaLinha);
            return descontos;
        }

        var total = quantidades.Sum();
        if (descontoDaLinha == 0m || total <= 0m)
        {
            descontos.AddRange(quantidades.Select(_ => 0m));
            return descontos;
        }

        var acumulado = 0m;
        var anterior = 0m;
        for (var i = 0; i < quantidades.Count; i++)
        {
            acumulado += quantidades[i];
            var ate = i == quantidades.Count - 1
                ? descontoDaLinha
                : decimal.Round(
                    descontoDaLinha * acumulado / total, 2, MidpointRounding.AwayFromZero);
            descontos.Add(ate - anterior);
            anterior = ate;
        }

        return descontos;
    }

    public void RecalcularTotais(Venda venda)
    {
        ArgumentNullException.ThrowIfNull(venda);

        venda.TotalBruto = Arredondar(venda.Itens.Sum(i => i.TotalBruto));
        // O desconto que valeu em cada linha, e não o pedido: o líquido da linha para em
        // zero, e somar o desconto bruto deixava o que passou de uma linha comer as outras
        // — um serviço com desconto maior que ele zerava a venda inteira, produto junto.
        var descontoItens = Arredondar(venda.Itens.Sum(i => Math.Min(i.DescontoValor, i.TotalBruto)));
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
        ValidarFormaEParcelas(forma, parcelas);

        var pagamento = MontarPagamento(venda, forma, valor, parcelas, MeioDeCaptura.Manual);
        pagamento.Autorizacao = autorizacao;

        venda.Pagamentos.Add(pagamento);
        // Dinheiro entrando numa venda aberta fecha a venda — com a baixa do estoque. Antes o
        // recebimento só trocava o status: a venda saía de "aberta" sem passar pelo
        // fechamento, e o estoque dos produtos nunca baixava.
        if (venda.Status == StatusVenda.Aberta)
        {
            await FecharAsync(venda, ct);
        }
        AtualizarStatus(venda);

        await _db.SaveChangesAsync(ct);
        return pagamento;
    }

    /// <summary>
    /// A forma aceita este recebimento? Desativada não recebe mais nada, e as parcelas vão
    /// de 1 ao máximo dela (1 quando ela não parcela). Zero ou menos é tratado como à vista,
    /// como sempre foi.
    /// </summary>
    public static void ValidarFormaEParcelas(FormaPagamento forma, int parcelas)
    {
        ArgumentNullException.ThrowIfNull(forma);

        if (!forma.Ativa)
        {
            throw new RecusaDeNegocioException(
                $"A forma de pagamento {forma.Nome} está desativada.", "FORMA_INATIVA");
        }

        var maximo = forma.PermiteParcelamento ? Math.Max(1, forma.MaximoParcelas) : 1;
        if (parcelas > maximo)
        {
            throw new RecusaDeNegocioException(
                maximo == 1
                    ? $"{forma.Nome} não aceita parcelamento."
                    : $"{forma.Nome} aceita no máximo {maximo} parcelas.",
                "PARCELAS_INVALIDAS");
        }
    }

    /// <summary>
    /// Fecha a venda: os itens param de mudar, o estoque dos produtos sai da prateleira e o
    /// status passa a seguir o que já foi pago.
    ///
    /// É o mesmo passo quer alguém clique em "Fechar venda", quer o primeiro dinheiro chegue
    /// com a venda ainda aberta (recebimento ou cobrança): em todos, depois disto a venda
    /// está fechada e o estoque, baixado — uma vez só.
    /// </summary>
    /// <param name="exigirEstoque">
    /// Falso só onde o dinheiro já foi capturado (a cobrança aprovada de uma venda que
    /// ficou aberta): recusar ali perderia de vista um pagamento de verdade, então baixa o
    /// que houver.
    /// </param>
    public async Task FecharAsync(Venda venda, CancellationToken ct = default, bool exigirEstoque = true)
    {
        ArgumentNullException.ThrowIfNull(venda);
        if (venda.Status != StatusVenda.Aberta)
        {
            throw new RecusaDeNegocioException("Só uma venda aberta pode ser fechada.", "STATUS_INVALIDO");
        }

        await BaixarEstoqueAsync(venda, exigirEstoque, ct);
        venda.FinalizadaEm = DateTimeOffset.UtcNow;
        venda.Status = StatusVenda.AguardandoPagamento;
        AtualizarStatus(venda);
    }

    /// <summary>
    /// Tira da prateleira os produtos da venda. Sem estoque suficiente, recusa: vender o que
    /// não existe deixava o estoque em zero e, no cancelamento, devolvia mais do que tinha
    /// saído.
    /// </summary>
    private async Task BaixarEstoqueAsync(Venda venda, bool exigirEstoque, CancellationToken ct)
    {
        if (venda.EstoqueBaixado)
        {
            return;
        }

        var porProduto = QuantidadesPorProduto(venda);
        if (porProduto.Count > 0)
        {
            var produtos = await ProdutosTravadosAsync(porProduto.Keys, ct);
            foreach (var produto in produtos.Where(p => p.Estoque is not null))
            {
                var sai = porProduto[produto.Id];
                if (produto.Estoque < sai && exigirEstoque)
                {
                    throw new RecusaDeNegocioException(
                        $"Estoque insuficiente de {produto.Nome}: {produto.Estoque} disponível(is).",
                        "ESTOQUE");
                }

                produto.Estoque = Math.Max(0, produto.Estoque!.Value - sai);
            }
        }

        // É isto que o cancelamento consulta para devolver o estoque.
        venda.EstoqueBaixado = true;
    }

    /// <summary>Devolve à prateleira o que o fechamento tirou. Uma vez só.</summary>
    public async Task DevolverEstoqueAsync(Venda venda, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(venda);
        if (!venda.EstoqueBaixado)
        {
            return;
        }

        var porProduto = QuantidadesPorProduto(venda);
        if (porProduto.Count > 0)
        {
            foreach (var produto in await ProdutosTravadosAsync(porProduto.Keys, ct))
            {
                if (produto.Estoque is { } estoque)
                {
                    produto.Estoque = estoque + porProduto[produto.Id];
                }
            }
        }

        venda.EstoqueBaixado = false;
    }

    /// <summary>Unidades inteiras por produto: meia unidade tira uma da prateleira.</summary>
    private static Dictionary<long, int> QuantidadesPorProduto(Venda venda) =>
        venda.Itens
            .Where(i => i.Tipo == TipoItem.Produto)
            .GroupBy(i => i.ItemCatalogoId)
            .ToDictionary(g => g.Key, g => g.Sum(i => (int)Math.Ceiling(i.Quantidade)));

    /// <summary>
    /// Os produtos, com a linha travada até o fim da transação: duas vendas diferentes
    /// fechando ao mesmo tempo liam o mesmo estoque e uma baixa apagava a outra.
    /// </summary>
    private async Task<List<ItemCatalogo>> ProdutosTravadosAsync(
        IEnumerable<long> ids, CancellationToken ct)
    {
        var lista = ids.ToArray();
        if (_db.Database.IsNpgsql() && _db.Database.CurrentTransaction is not null)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM itens_catalogo WHERE id = ANY({lista}) FOR UPDATE", ct);
        }

        return await _db.ItensCatalogo.Where(i => lista.Contains(i.Id)).ToListAsync(ct);
    }

    /// <summary>
    /// Estorna um recebimento confirmado: o lançamento fica, marcado como estornado, com
    /// data e motivo; o total pago e o saldo são refeitos; e a venda que estava paga volta
    /// a aguardar pagamento. Uma venda aberta continua aberta. É o que destrava o
    /// cancelamento de uma venda que já recebeu.
    ///
    /// Vale também para o que entrou por cobrança (maquininha, Pix, gateway): aqui é só o
    /// registro — não há integração que devolva o dinheiro na adquirente ou no PSP; a
    /// devolução em si é feita lá, por quem opera o caixa.
    /// </summary>
    public void EstornarPagamento(Venda venda, Pagamento pagamento, string? motivo)
    {
        ArgumentNullException.ThrowIfNull(venda);
        ArgumentNullException.ThrowIfNull(pagamento);

        if (pagamento.Status != StatusPagamento.Confirmado)
        {
            throw new InvalidOperationException("Só um recebimento confirmado pode ser estornado.");
        }

        pagamento.Status = StatusPagamento.Estornado;
        pagamento.EstornadoEm = DateTimeOffset.UtcNow;
        pagamento.MotivoEstorno = string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim();

        RecalcularTotais(venda);
        if (venda.Status == StatusVenda.Paga && venda.SaldoAberto > 0)
        {
            venda.Status = StatusVenda.AguardandoPagamento;
        }
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
            // Contada do dia da empresa: a venda das 22h de São Paulo é de hoje, e não de
            // amanhã como seria em UTC.
            PrevisaoLiquidacao = _relogio.Hoje().AddDays(forma.DiasParaLiquidacao),
        };
    }

    /// <summary>
    /// Troca a taxa estimada pela que a adquirente cobrou de verdade e refaz o líquido.
    /// A estimada continua gravada: é ela que revela a diferença.
    /// </summary>
    public void ConciliarTaxa(Pagamento pagamento, decimal taxaReal)
    {
        ArgumentNullException.ThrowIfNull(pagamento);
        // Taxa negativa aumentaria o líquido; maior que o valor, o deixaria negativo. As duas
        // são erro de digitação, não taxa.
        if (taxaReal < 0m || taxaReal > pagamento.Valor)
        {
            throw new RecusaDeNegocioException(
                $"A taxa precisa ficar entre 0 e o valor recebido ({pagamento.Valor:0.00}).",
                "TAXA_INVALIDA");
        }

        pagamento.ValorTaxa = Arredondar(taxaReal);
        pagamento.ValorLiquido = Arredondar(pagamento.Valor - pagamento.ValorTaxa);
        pagamento.TaxaConferida = true;
        pagamento.ConciliadoEm = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Ajusta o status de uma venda FECHADA a partir do que já foi pago. A aberta continua
    /// aberta (quem a fecha é <see cref="FecharAsync"/>, com a baixa do estoque) e a
    /// cancelada ou estornada não volta à vida por um recálculo.
    /// </summary>
    public void AtualizarStatus(Venda venda)
    {
        ArgumentNullException.ThrowIfNull(venda);

        RecalcularTotais(venda);
        if (venda.Status is not (StatusVenda.AguardandoPagamento or StatusVenda.Paga))
        {
            return;
        }

        venda.Status = venda.SaldoAberto <= 0 ? StatusVenda.Paga : StatusVenda.AguardandoPagamento;
        if (venda.Status == StatusVenda.Paga)
        {
            venda.FinalizadaEm ??= DateTimeOffset.UtcNow;
        }
    }

    private static decimal Arredondar(decimal valor) =>
        decimal.Round(valor, 2, MidpointRounding.AwayFromZero);
}

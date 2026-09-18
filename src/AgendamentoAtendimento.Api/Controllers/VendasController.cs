using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>
/// Vendas de produtos e serviços. Os totais são sempre recalculados a partir dos itens:
/// o app envia quantidade e desconto, nunca o valor final.
/// </summary>
[Route("api/vendas")]
public class VendasController : ControllerBaseApi
{
    private readonly AppDbContext _db;
    private readonly VendaService _vendas;

    public VendasController(AppDbContext db, VendaService vendas)
    {
        _db = db;
        _vendas = vendas;
    }

    [HttpGet]
    [RequerPermissao("vendas.ver")]
    public async Task<ActionResult<PaginaDto<VendaDto>>> Listar(
        [FromQuery] DateOnly? de,
        [FromQuery] DateOnly? ate,
        [FromQuery] long? clienteId,
        [FromQuery] StatusVenda? status,
        [FromQuery] ParametrosDePagina? pagina = null,
        CancellationToken ct = default)
    {
        var p = pagina ?? new ParametrosDePagina();
        var consulta = _db.Vendas
            .AsNoTracking()
            .Include(v => v.Cliente)
            .Include(v => v.Itens)
            .Include(v => v.Pagamentos).ThenInclude(x => x.FormaPagamento)
            .AsQueryable();

        if (de is { } inicio)
        {
            var limite = new DateTimeOffset(inicio.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            consulta = consulta.Where(v => v.CriadoEm >= limite);
        }

        if (ate is { } fim)
        {
            var limite = new DateTimeOffset(fim.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            consulta = consulta.Where(v => v.CriadoEm < limite);
        }

        if (clienteId is { } cliente)
        {
            consulta = consulta.Where(v => v.ClienteId == cliente);
        }

        if (status is { } st)
        {
            consulta = consulta.Where(v => v.Status == st);
        }

        var total = await consulta.CountAsync(ct);
        var itens = await consulta
            .OrderByDescending(v => v.CriadoEm)
            .Skip(p.Pular).Take(p.TamanhoSeguro)
            .ToListAsync(ct);

        return Ok(new PaginaDto<VendaDto>(
            itens.Select(v => v.ParaDto()).ToList(), p.PaginaSegura, p.TamanhoSeguro, total));
    }

    [HttpGet("{id:long}")]
    [RequerPermissao("vendas.ver")]
    public async Task<ActionResult<VendaDto>> Obter(long id, CancellationToken ct)
    {
        var venda = NaoNulo(await CarregarAsync(id, ct), "Venda não encontrada.");
        return Ok(venda.ParaDto());
    }

    [HttpPost]
    [RequerPermissao("vendas.criar")]
    public async Task<ActionResult<VendaDto>> Criar(VendaRequest req, CancellationToken ct)
    {
        if (req.Itens is null || req.Itens.Count == 0)
        {
            throw new RegraDeNegocioException("A venda precisa de ao menos um item.", "SEM_ITENS");
        }

        var cliente = NaoNulo(
            await _db.Clientes.FirstOrDefaultAsync(c => c.Id == req.ClienteId, ct),
            "Cliente não encontrado.");

        var venda = new Venda { ClienteId = cliente.Id, AgendamentoId = req.AgendamentoId };
        await PreencherItensAsync(venda, req, ct);

        _db.Vendas.Add(venda);
        await _db.SaveChangesAsync(ct);

        var completa = await CarregarAsync(venda.Id, ct);
        return CreatedAtAction(nameof(Obter), new { id = venda.Id }, completa!.ParaDto());
    }

    [HttpPut("{id:long}")]
    [RequerPermissao("vendas.editar")]
    public async Task<ActionResult<VendaDto>> Atualizar(long id, VendaRequest req, CancellationToken ct)
    {
        var venda = NaoNulo(
            await _db.Vendas.Include(v => v.Itens).Include(v => v.Pagamentos)
                .FirstOrDefaultAsync(v => v.Id == id, ct),
            "Venda não encontrada.");

        if (venda.Status is StatusVenda.Paga or StatusVenda.Cancelada or StatusVenda.Estornada)
        {
            throw new RegraDeNegocioException(
                "Venda paga ou cancelada não pode ser alterada.", "STATUS_FINAL");
        }

        venda.ClienteId = req.ClienteId;
        venda.AgendamentoId = req.AgendamentoId;
        venda.Itens.Clear();
        await PreencherItensAsync(venda, req, ct);

        await _db.SaveChangesAsync(ct);
        var completa = await CarregarAsync(id, ct);
        return Ok(completa!.ParaDto());
    }

    /// <summary>Registra um recebimento e atualiza o status da venda.</summary>
    [HttpPost("{id:long}/pagamentos")]
    [RequerPermissao("financeiro.receber")]
    public async Task<ActionResult<VendaDto>> Receber(long id, PagamentoRequest req, CancellationToken ct)
    {
        var venda = NaoNulo(
            await _db.Vendas.Include(v => v.Itens).Include(v => v.Pagamentos)
                .FirstOrDefaultAsync(v => v.Id == id, ct),
            "Venda não encontrada.");

        if (venda.Status is StatusVenda.Cancelada or StatusVenda.Estornada)
        {
            throw new RegraDeNegocioException("Venda cancelada não recebe pagamento.", "STATUS_FINAL");
        }

        _vendas.RecalcularTotais(venda);
        if (req.Valor > venda.SaldoAberto)
        {
            throw new RegraDeNegocioException(
                $"O valor excede o saldo em aberto ({venda.SaldoAberto:0.00}).", "VALOR_ACIMA_DO_SALDO");
        }

        await _vendas.RegistrarPagamentoAsync(
            venda, req.FormaPagamentoId, req.Valor, req.Parcelas, req.Autorizacao, ct);

        var completa = await CarregarAsync(id, ct);
        return Ok(completa!.ParaDto());
    }

    [HttpPost("{id:long}/finalizar")]
    [RequerPermissao("vendas.finalizar")]
    public async Task<ActionResult<VendaDto>> Finalizar(long id, CancellationToken ct)
    {
        var venda = NaoNulo(
            await _db.Vendas.Include(v => v.Itens).Include(v => v.Pagamentos)
                .FirstOrDefaultAsync(v => v.Id == id, ct),
            "Venda não encontrada.");

        _vendas.RecalcularTotais(venda);
        venda.Status = venda.SaldoAberto <= 0 ? StatusVenda.Paga : StatusVenda.AguardandoPagamento;
        venda.FinalizadaEm = DateTimeOffset.UtcNow;

        // Baixa de estoque só acontece no fechamento da venda.
        foreach (var item in venda.Itens.Where(i => i.Tipo == TipoItem.Produto))
        {
            var produto = await _db.ItensCatalogo.FirstOrDefaultAsync(i => i.Id == item.ItemCatalogoId, ct);
            if (produto?.Estoque is { } estoque)
            {
                produto.Estoque = Math.Max(0, estoque - (int)Math.Ceiling(item.Quantidade));
            }
        }

        await _db.SaveChangesAsync(ct);
        var completa = await CarregarAsync(id, ct);
        return Ok(completa!.ParaDto());
    }

    [HttpDelete("{id:long}")]
    [RequerPermissao("vendas.cancelar")]
    public async Task<IActionResult> Cancelar(long id, CancellationToken ct)
    {
        var venda = NaoNulo(
            await _db.Vendas.Include(v => v.Pagamentos).FirstOrDefaultAsync(v => v.Id == id, ct),
            "Venda não encontrada.");

        if (venda.Pagamentos.Any(p => p.Status == StatusPagamento.Confirmado))
        {
            throw new RegraDeNegocioException(
                "Estorne os recebimentos antes de cancelar a venda.", "VENDA_COM_PAGAMENTO");
        }

        venda.Status = StatusVenda.Cancelada;
        venda.CanceladaEm = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task PreencherItensAsync(Venda venda, VendaRequest req, CancellationToken ct)
    {
        var ids = req.Itens.Select(i => i.ItemId).Distinct().ToList();
        var catalogo = await _db.ItensCatalogo.Where(i => ids.Contains(i.Id)).ToListAsync(ct);

        foreach (var pedido in req.Itens)
        {
            var item = catalogo.FirstOrDefault(i => i.Id == pedido.ItemId)
                ?? throw new NaoEncontradoException($"Item {pedido.ItemId} não encontrado.");

            if (pedido.Quantidade <= 0)
            {
                throw new RegraDeNegocioException(
                    $"Quantidade inválida para {item.Nome}.", "QUANTIDADE");
            }

            if (item.Tipo == TipoItem.Produto && item.Estoque is { } estoque &&
                pedido.Quantidade > estoque)
            {
                throw new RegraDeNegocioException(
                    $"Estoque insuficiente de {item.Nome}: {estoque} disponível(is).", "ESTOQUE");
            }

            venda.Itens.Add(new VendaItem
            {
                ItemCatalogoId = item.Id,
                Tipo = item.Tipo,
                Nome = item.Nome,
                Quantidade = pedido.Quantidade,
                // O preço do catálogo vale, salvo quando quem tem permissão manda outro.
                PrecoUnitario = pedido.PrecoUnitario ?? item.Preco,
                DescontoValor = pedido.DescontoValor,
                TaxaPercentual = item.TaxaPercentual,
            });
        }

        venda.DescontoGeral = req.DescontoGeral;
        venda.Observacao = req.Observacao;
        _vendas.RecalcularTotais(venda);
    }

    private Task<Venda?> CarregarAsync(long id, CancellationToken ct) =>
        _db.Vendas
            .Include(v => v.Cliente)
            .Include(v => v.Itens)
            .Include(v => v.Pagamentos).ThenInclude(p => p.FormaPagamento)
            .FirstOrDefaultAsync(v => v.Id == id, ct);
}

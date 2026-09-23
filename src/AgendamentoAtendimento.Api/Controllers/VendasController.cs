using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Agenda;
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
            .Include(v => v.Vendedor)
            // O vendedor de cada item: é o nome que o checkout mostra ao lado do serviço.
            .Include(v => v.Itens).ThenInclude(i => i.Vendedor)
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

        await ValidarPedidoAsync(req, ct);
        var agendamento = await CarregarAgendamentoDaVendaAsync(req.AgendamentoId, ct);

        var venda = new Venda
        {
            ClienteId = cliente.Id,
            AgendamentoId = req.AgendamentoId,
            // Numa venda que nasce de atendimento, quem atendeu leva a comissão — a menos
            // que o app diga outra coisa.
            VendedorId = req.VendedorId ?? agendamento?.ResponsavelId,
        };
        await PreencherItensAsync(venda, req, ct, agendamento);

        _db.Vendas.Add(venda);
        await _db.SaveChangesAsync(ct);

        // Sem esta volta, o atendimento não sabe que já virou venda e o app oferece
        // faturar de novo — o mesmo serviço cobrado duas vezes.
        if (agendamento is not null)
        {
            agendamento.VendaId = venda.Id;
            await _db.SaveChangesAsync(ct);
        }

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

        // O cliente vem do pedido: sem conferir, um id qualquer (inclusive de outra
        // empresa) ia direto para a chave estrangeira — e a venda sumia da listagem.
        NaoNulo(
            await _db.Clientes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.ClienteId, ct),
            "Cliente não encontrado.");
        await ValidarPedidoAsync(req, ct);

        // Trocar o agendamento da venda solta o antigo e prende o novo. O antigo é
        // carregado sem a checagem de "já faturado": quem o faturou foi esta venda.
        Agendamento? agendamento;
        if (venda.AgendamentoId != req.AgendamentoId)
        {
            var anterior = await BuscarAgendamentoAsync(venda.AgendamentoId, ct);
            if (anterior is not null && anterior.VendaId == venda.Id)
            {
                anterior.VendaId = null;
            }

            agendamento = await CarregarAgendamentoDaVendaAsync(req.AgendamentoId, ct);
            if (agendamento is not null)
            {
                agendamento.VendaId = venda.Id;
            }
        }
        else
        {
            // Mesmo agendamento: carrega sem a checagem de "já faturado", porque quem o
            // faturou foi esta venda. Os itens dele decidem a comissão de cada linha.
            agendamento = await BuscarAgendamentoAsync(venda.AgendamentoId, ct);
        }

        venda.ClienteId = req.ClienteId;
        venda.AgendamentoId = req.AgendamentoId;
        // PUT troca a venda inteira: nulo aqui quer dizer "sem vendedor", e não "mantém".
        // Sem isso o checkout nunca conseguiria tirar a comissão de alguém.
        venda.VendedorId = req.VendedorId;
        venda.Itens.Clear();
        await PreencherItensAsync(venda, req, ct, agendamento);

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

        if (req.Valor <= 0)
        {
            throw new RegraDeNegocioException("O valor do pagamento deve ser positivo.", "VALOR_INVALIDO");
        }

        NaoNulo(
            await _db.FormasPagamento.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == req.FormaPagamentoId, ct),
            "Forma de pagamento não encontrada.");

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

        // Finalizar é um passo só, a partir da venda aberta. Repetir baixava o estoque de
        // novo a cada chamada, e numa venda cancelada ou estornada a trazia de volta a
        // "aguardando pagamento".
        if (venda.Status != StatusVenda.Aberta)
        {
            throw new RegraDeNegocioException(
                "Só uma venda aberta pode ser finalizada.", "STATUS_INVALIDO");
        }

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

        // Com a maquininha ou o QR ainda na mão do cliente, cancelar agora deixaria a
        // aprovação que chegar depois lançar o pagamento — e reabrir a venda cancelada.
        var agora = DateTimeOffset.UtcNow;
        if (await _db.Cobrancas.AnyAsync(
                c => c.VendaId == venda.Id
                     && (c.Status == StatusCobranca.Criada || c.Status == StatusCobranca.EmAndamento)
                     && c.ExpiraEm > agora, ct))
        {
            throw new RegraDeNegocioException(
                "Esta venda tem uma cobrança em andamento. Conclua ou cancele a cobrança antes.",
                "VENDA_COM_COBRANCA");
        }

        venda.Status = StatusVenda.Cancelada;
        venda.CanceladaEm = DateTimeOffset.UtcNow;

        // O atendimento volta para a fila de cobrança. Sem soltar o vínculo, um
        // cancelamento deixaria o serviço entregue sem poder ser cobrado nunca mais:
        // criar outra venda para ele esbarra em ATENDIMENTO_JA_FATURADO.
        if (venda.AgendamentoId is { } agendamentoId)
        {
            var agendamento = await _db.Agendamentos
                .FirstOrDefaultAsync(a => a.Id == agendamentoId && a.VendaId == venda.Id, ct);
            if (agendamento is not null)
            {
                agendamento.VendaId = null;
            }
        }

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// O agendamento que esta venda fatura. Recusa um que já tenha outra venda: dois
    /// lançamentos para o mesmo atendimento é cobrança em dobro.
    /// </summary>
    private async Task<Agendamento?> CarregarAgendamentoDaVendaAsync(
        long? agendamentoId, CancellationToken ct)
    {
        var agendamento = await BuscarAgendamentoAsync(agendamentoId, ct);
        if (agendamento is null)
        {
            return null;
        }

        if (agendamento.VendaId is { } jaFaturado)
        {
            throw new RegraDeNegocioException(
                $"Este atendimento já gerou a venda {jaFaturado}.", "ATENDIMENTO_JA_FATURADO");
        }

        return agendamento;
    }

    private async Task<Agendamento?> BuscarAgendamentoAsync(
        long? agendamentoId, CancellationToken ct)
    {
        if (agendamentoId is not { } id)
        {
            return null;
        }

        return NaoNulo(
            // Os itens vêm junto: é deles que sai quem prestou cada serviço, e é isso
            // que decide para quem vai a comissão de cada linha da venda.
            await _db.Agendamentos.Include(a => a.Itens)
                .FirstOrDefaultAsync(a => a.Id == id, ct),
            "Agendamento não encontrado.");
    }

    /// <summary>
    /// O que o pedido pode mandar sem virar dinheiro inventado ou erro de banco: valores
    /// não negativos e vendedores que existem nesta empresa.
    /// </summary>
    private async Task ValidarPedidoAsync(VendaRequest req, CancellationToken ct)
    {
        if (req.Itens is null || req.Itens.Count == 0)
        {
            throw new RegraDeNegocioException("A venda precisa de ao menos um item.", "SEM_ITENS");
        }

        if (req.DescontoGeral < 0 || req.Itens.Any(i => i.DescontoValor < 0))
        {
            throw new RegraDeNegocioException("O desconto não pode ser negativo.", "DESCONTO_INVALIDO");
        }

        if (req.Itens.Any(i => i.PrecoUnitario is < 0))
        {
            throw new RegraDeNegocioException("O preço não pode ser negativo.", "PRECO_INVALIDO");
        }

        var vendedores = req.Itens.Select(i => i.VendedorId)
            .Append(req.VendedorId)
            .OfType<long>()
            .Distinct()
            .ToList();
        if (vendedores.Count == 0)
        {
            return;
        }

        var existentes = await _db.Usuarios.AsNoTracking()
            .CountAsync(u => vendedores.Contains(u.Id), ct);
        if (existentes != vendedores.Count)
        {
            throw new RegraDeNegocioException("Vendedor não encontrado.", "VENDEDOR_INVALIDO");
        }
    }

    private async Task PreencherItensAsync(
        Venda venda, VendaRequest req, CancellationToken ct, Agendamento? agendamento = null)
    {
        var ids = req.Itens.Select(i => i.ItemId).Distinct().ToList();
        var catalogo = await _db.ItensCatalogo.Where(i => ids.Contains(i.Id)).ToListAsync(ct);

        // Quem prestou cada serviço no atendimento, unidade a unidade. É daqui que sai a
        // comissão: pagar tudo a quem abriu a venda daria o dinheiro à pessoa errada
        // quando dois funcionários atenderam o mesmo cliente.
        var quemPrestou = VendaService.QuemPrestouPorItem(agendamento);

        // Linha que já chega com dono (a venda sendo salva de novo, ou alguém escolheu
        // quem leva) já gastou a vez dessa pessoa na fila: só o que sobra se divide.
        foreach (var pedido in req.Itens)
        {
            if (pedido.VendedorId is { } dono && pedido.Quantidade > 0 &&
                quemPrestou.TryGetValue(pedido.ItemId, out var filaDoItem))
            {
                VendaService.DescontarDaFila(filaDoItem, dono, pedido.Quantidade);
            }
        }

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

            // O pedido manda quem leva a comissão, e aí é uma linha só. Sem isso, a linha
            // se divide entre quem prestou: o mesmo serviço pode ter sido prestado por
            // duas pessoas no mesmo atendimento, e cada uma recebe pelo que fez.
            var partes = pedido.VendedorId is not null
                ? new List<(long? Quem, decimal Quantidade)> { (pedido.VendedorId, pedido.Quantidade) }
                : VendaService.DividirEntreQuemPrestou(
                    quemPrestou.TryGetValue(item.Id, out var fila) ? fila : null,
                    pedido.Quantidade);

            // O desconto pedido é da linha inteira: dividida, ele acompanha as partes na
            // proporção das unidades, e a soma bate no centavo.
            var descontos = VendaService.DividirDesconto(
                pedido.DescontoValor, partes.Select(p => p.Quantidade).ToList());

            for (var indiceDaParte = 0; indiceDaParte < partes.Count; indiceDaParte++)
            {
                venda.Itens.Add(new VendaItem
                {
                    ItemCatalogoId = item.Id,
                    Tipo = item.Tipo,
                    Nome = item.Nome,
                    Quantidade = partes[indiceDaParte].Quantidade,
                    // O preço do catálogo vale, salvo quando quem tem permissão manda outro.
                    PrecoUnitario = pedido.PrecoUnitario ?? item.Preco,
                    DescontoValor = descontos[indiceDaParte],
                    TaxaPercentual = item.TaxaPercentual,
                    // Congelada aqui: mexer na comissão do catálogo amanhã não muda o que já
                    // foi vendido nem o que foi prometido a quem atendeu.
                    ComissaoPercentual = item.ComissaoPercentual,
                    // Nulo cai no vendedor da venda na hora de somar.
                    VendedorId = partes[indiceDaParte].Quem,
                });
            }
        }

        venda.DescontoGeral = req.DescontoGeral;
        venda.Observacao = req.Observacao;
        _vendas.RecalcularTotais(venda);
    }

    private Task<Venda?> CarregarAsync(long id, CancellationToken ct) =>
        _db.Vendas
            .Include(v => v.Cliente)
            .Include(v => v.Vendedor)
            // O vendedor de cada item: é o nome que o checkout mostra ao lado do serviço.
            .Include(v => v.Itens).ThenInclude(i => i.Vendedor)
            .Include(v => v.Pagamentos).ThenInclude(p => p.FormaPagamento)
            .FirstOrDefaultAsync(v => v.Id == id, ct);
}

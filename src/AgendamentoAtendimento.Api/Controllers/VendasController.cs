using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
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
    /// <summary>O máximo de unidades numa linha da venda.</summary>
    private const decimal MaximoPorLinha = 100_000m;

    private readonly AppDbContext _db;
    private readonly VendaService _vendas;
    private readonly RelogioDoTenant _relogio;

    public VendasController(AppDbContext db, VendaService vendas, RelogioDoTenant relogio)
    {
        _db = db;
        _vendas = vendas;
        _relogio = relogio;
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
        // O cliente vem à parte (ComClientesAsync): incluído aqui, o filtro de exclusão
        // lógica virava INNER JOIN e a venda de um cliente excluído sumia da lista — e da
        // contagem não, que não passa pelo join: "76 vendas" com 17 na página.
        var consulta = _db.Vendas
            .AsNoTracking()
            .Include(v => v.Vendedor)
            // O vendedor de cada item: é o nome que o checkout mostra ao lado do serviço.
            .Include(v => v.Itens).ThenInclude(i => i.Vendedor)
            .Include(v => v.Pagamentos).ThenInclude(x => x.FormaPagamento)
            .AsQueryable();

        // O período é de dias da empresa: a venda das 22h de São Paulo é daquele dia.
        if (de is { } inicio)
        {
            var limite = _relogio.InicioDoDia(inicio);
            consulta = consulta.Where(v => v.CriadoEm >= limite);
        }

        if (ate is { } fim)
        {
            var limite = _relogio.FimDoDia(fim);
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
            .ThenByDescending(v => v.Id)
            .Skip(p.Pular).Take(p.TamanhoSeguro)
            .ToListAsync(ct);
        await ComClientesAsync(itens, ct);

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

        // Faturar confere "este atendimento já virou venda?" e depois grava: sem trava, duas
        // abas (ou duas pessoas) faturando o mesmo atendimento criavam duas vendas dele. A
        // trava da agenda põe uma atrás da outra, e a segunda já enxerga a primeira.
        await using var trava = req.AgendamentoId is null
            ? TransacaoDaAgenda.Nenhuma
            : await _db.TravarAgendaAsync(ct);
        var agendamento = await CarregarAgendamentoDaVendaAsync(req.AgendamentoId, ct);
        if (agendamento is not null && agendamento.CobrancaDisponivel(null) != AcaoDeCobranca.GerarVenda)
        {
            throw new RegraDeNegocioException(
                "Só um atendimento em andamento ou concluído, com serviço, vira venda.",
                "ATENDIMENTO_NAO_ENTREGUE");
        }

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
        await trava.ConfirmarAsync(ct);

        var completa = await CarregarAsync(venda.Id, ct);
        return CreatedAtAction(nameof(Obter), new { id = venda.Id }, completa!.ParaDto());
    }

    [HttpPut("{id:long}")]
    [RequerPermissao("vendas.editar")]
    public async Task<ActionResult<VendaDto>> Atualizar(long id, VendaRequest req, CancellationToken ct)
    {
        await using var trava = await _db.TravarVendaAsync(id, ct);
        var venda = NaoNulo(
            await _db.Vendas.Include(v => v.Itens).Include(v => v.Pagamentos)
                .FirstOrDefaultAsync(v => v.Id == id, ct),
            "Venda não encontrada.");

        if (venda.Status is StatusVenda.Paga or StatusVenda.Cancelada or StatusVenda.Estornada)
        {
            throw new RegraDeNegocioException(
                "Venda paga ou cancelada não pode ser alterada.", "STATUS_FINAL");
        }

        // Fechada, os itens não mudam mais: é o que a tela diz, e agora é o que a Api faz.
        // Editar depois do fechamento baixava de novo o que já tinha saído (ou deixava de
        // devolver no cancelamento), e um total menor que o já recebido deixava a venda
        // presa, sem receber nem fechar.
        if (venda.Status != StatusVenda.Aberta)
        {
            throw new RegraDeNegocioException(
                "Esta venda já foi fechada: os itens não mudam mais.", "VENDA_FECHADA");
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
            // Prender um atendimento novo a esta venda é faturá-lo: a mesma trava da agenda
            // e a mesma regra do POST. Sem elas, um PUT e um POST ao mesmo tempo faturavam o
            // mesmo atendimento duas vezes, e um atendimento que nem começou virava venda.
            // (A trava entra na transação da trava da venda e solta junto com ela.)
            await using var travaDaAgenda = req.AgendamentoId is null
                ? TransacaoDaAgenda.Nenhuma
                : await _db.TravarAgendaAsync(ct);

            var anterior = await BuscarAgendamentoAsync(venda.AgendamentoId, ct);
            if (anterior is not null && anterior.VendaId == venda.Id)
            {
                anterior.VendaId = null;
            }

            agendamento = await CarregarAgendamentoDaVendaAsync(req.AgendamentoId, ct);
            if (agendamento is not null)
            {
                if (agendamento.CobrancaDisponivel(null) != AcaoDeCobranca.GerarVenda)
                {
                    throw new RegraDeNegocioException(
                        "Só um atendimento em andamento ou concluído, com serviço, vira venda.",
                        "ATENDIMENTO_NAO_ENTREGUE");
                }

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
        // Linha que já estava na venda continua valendo mesmo que o item tenha saído do
        // catálogo depois: sem isto, excluir um item travava toda venda aberta que o tinha.
        var jaNaVenda = venda.Itens.Select(i => i.ItemCatalogoId).ToHashSet();
        venda.Itens.Clear();
        await PreencherItensAsync(venda, req, ct, agendamento, jaNaVenda);

        await _db.SaveChangesAsync(ct);
        await trava.ConfirmarAsync(ct);
        var completa = await CarregarAsync(id, ct);
        return Ok(completa!.ParaDto());
    }

    /// <summary>Registra um recebimento e atualiza o status da venda.</summary>
    [HttpPost("{id:long}/pagamentos")]
    [RequerPermissao("financeiro.receber")]
    public async Task<ActionResult<VendaDto>> Receber(long id, PagamentoRequest req, CancellationToken ct)
    {
        // Um recebimento por vez na mesma venda: dois cliques (ou duas abas) passavam os dois
        // pela conferência do saldo e o mesmo saldo era recebido duas vezes.
        await using var trava = await _db.TravarVendaAsync(id, ct);
        var venda = NaoNulo(
            await _db.Vendas.Include(v => v.Itens).Include(v => v.Pagamentos)
                .FirstOrDefaultAsync(v => v.Id == id, ct),
            "Venda não encontrada.");

        if (venda.Status is StatusVenda.Cancelada or StatusVenda.Estornada)
        {
            throw new RegraDeNegocioException("Venda cancelada não recebe pagamento.", "STATUS_FINAL");
        }

        // O valor é gravado em centavos: conferir antes de arredondar deixava R$ 0,004
        // passar como positivo e virar um recebimento de R$ 0,00 — que fechava a venda.
        var valor = decimal.Round(req.Valor, 2, MidpointRounding.AwayFromZero);
        if (valor <= 0)
        {
            throw new RegraDeNegocioException("O valor do pagamento deve ser positivo.", "VALOR_INVALIDO");
        }

        NaoNulo(
            await _db.FormasPagamento.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == req.FormaPagamentoId, ct),
            "Forma de pagamento não encontrada.");

        // Com a maquininha ou o QR na mão do cliente, um recebimento manual do mesmo saldo
        // fazia o cliente pagar duas vezes quando a cobrança fosse aprovada.
        var agora = DateTimeOffset.UtcNow;
        if (await _db.Cobrancas.AnyAsync(
                c => c.VendaId == venda.Id
                     && (c.Status == StatusCobranca.Criada || c.Status == StatusCobranca.EmAndamento)
                     && c.ExpiraEm > agora, ct))
        {
            throw new RegraDeNegocioException(
                "Esta venda tem uma cobrança em andamento. Conclua ou cancele a cobrança antes de receber de outro jeito.",
                "VENDA_COM_COBRANCA");
        }

        _vendas.RecalcularTotais(venda);
        if (valor > venda.SaldoAberto)
        {
            throw new RegraDeNegocioException(
                $"O valor excede o saldo em aberto ({venda.SaldoAberto:0.00}).", "VALOR_ACIMA_DO_SALDO");
        }

        await _vendas.RegistrarPagamentoAsync(
            venda, req.FormaPagamentoId, valor, req.Parcelas, req.Autorizacao, ct);
        await trava.ConfirmarAsync(ct);

        var completa = await CarregarAsync(id, ct);
        return Ok(completa!.ParaDto());
    }

    /// <summary>
    /// Estorna um recebimento da venda. O pagamento continua listado, marcado como
    /// estornado; o total pago e o saldo são refeitos, e a venda paga volta a aguardar
    /// pagamento. Estornar de novo o mesmo recebimento é recusado.
    /// </summary>
    /// <remarks>
    /// O que entrou por cobrança (cartão, Pix) também se estorna aqui, mas só no registro:
    /// não há integração de estorno com adquirente ou PSP — a devolução do dinheiro é
    /// feita lá, e esta rota deixa o caixa do sistema igual ao de verdade.
    /// </remarks>
    [HttpPost("{vendaId:long}/pagamentos/{pagamentoId:long}/estorno")]
    [RequerPermissao("financeiro.estornar")]
    public async Task<ActionResult<VendaDto>> Estornar(
        long vendaId, long pagamentoId, [FromBody] EstornarPagamentoRequest? req, CancellationToken ct)
    {
        await using var trava = await _db.TravarVendaAsync(vendaId, ct);
        var venda = NaoNulo(
            await _db.Vendas.Include(v => v.Itens).Include(v => v.Pagamentos)
                .FirstOrDefaultAsync(v => v.Id == vendaId, ct),
            "Venda não encontrada.");

        var pagamento = NaoNulo(
            venda.Pagamentos.FirstOrDefault(p => p.Id == pagamentoId),
            "Pagamento não encontrado nesta venda.");

        if (pagamento.Status == StatusPagamento.Estornado)
        {
            throw new RegraDeNegocioException(
                "Este recebimento já foi estornado.", "PAGAMENTO_JA_ESTORNADO");
        }

        if (pagamento.Status != StatusPagamento.Confirmado)
        {
            throw new RegraDeNegocioException(
                "Só um recebimento confirmado pode ser estornado.", "PAGAMENTO_NAO_CONFIRMADO");
        }

        if (req?.Motivo is { Length: > 500 })
        {
            throw new RegraDeNegocioException(
                "O motivo do estorno pode ter no máximo 500 caracteres.", "MOTIVO_LONGO");
        }

        _vendas.EstornarPagamento(venda, pagamento, req?.Motivo);
        await _db.SaveChangesAsync(ct);
        await trava.ConfirmarAsync(ct);

        var completa = await CarregarAsync(vendaId, ct);
        return Ok(completa!.ParaDto());
    }

    [HttpPost("{id:long}/finalizar")]
    [RequerPermissao("vendas.finalizar")]
    public async Task<ActionResult<VendaDto>> Finalizar(long id, CancellationToken ct)
    {
        await using var trava = await _db.TravarVendaAsync(id, ct);
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

        // Baixa de estoque só acontece no fechamento da venda — aqui, ou no primeiro
        // dinheiro que chega com ela aberta. Sem estoque suficiente, recusa.
        await _vendas.FecharAsync(venda, ct);

        await _db.SaveChangesAsync(ct);
        await trava.ConfirmarAsync(ct);
        var completa = await CarregarAsync(id, ct);
        return Ok(completa!.ParaDto());
    }

    [HttpDelete("{id:long}")]
    [RequerPermissao("vendas.cancelar")]
    public async Task<IActionResult> Cancelar(long id, CancellationToken ct)
    {
        await using var trava = await _db.TravarVendaAsync(id, ct);
        var venda = NaoNulo(
            await _db.Vendas.Include(v => v.Pagamentos).Include(v => v.Itens)
                .FirstOrDefaultAsync(v => v.Id == id, ct),
            "Venda não encontrada.");

        // Cancelar de novo não muda nada — e não pode devolver o estoque duas vezes.
        if (venda.Status == StatusVenda.Cancelada)
        {
            return NoContent();
        }

        // O estorno é `POST /api/vendas/{id}/pagamentos/{pagamentoId}/estorno`.
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

        // A finalização baixou o estoque dos produtos; cancelar devolve o que saiu. Sem
        // isto, cada venda finalizada e cancelada sumia com o produto da prateleira.
        await _vendas.DevolverEstoqueAsync(venda, ct);

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
        await trava.ConfirmarAsync(ct);
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
        Venda venda, VendaRequest req, CancellationToken ct, Agendamento? agendamento = null,
        IReadOnlySet<long>? jaNaVenda = null)
    {
        var ids = req.Itens.Select(i => i.ItemId).Distinct().ToList();
        var catalogo = await _db.ItensCatalogo.Where(i => ids.Contains(i.Id)).ToListAsync(ct);
        if (jaNaVenda is { Count: > 0 } && catalogo.Count < ids.Count)
        {
            // Item excluído do catálogo depois de entrar na venda: a linha dele continua
            // valendo (nome e preço já estavam congelados). Só a empresa da venda.
            var faltam = ids.Where(id => jaNaVenda.Contains(id) && catalogo.All(c => c.Id != id)).ToList();
            catalogo.AddRange(await _db.ItensCatalogo.IgnoreQueryFilters()
                .Where(i => faltam.Contains(i.Id) && i.TenantId == TenantId)
                .ToListAsync(ct));
        }

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

            // Quantidade, preço e desconto são gravados com duas casas: calcular com a
            // terceira e gravar sem ela fazia o total da venda discordar da soma das linhas.
            var quantidade = decimal.Round(pedido.Quantidade, 2, MidpointRounding.AwayFromZero);
            var preco = decimal.Round(pedido.PrecoUnitario ?? item.Preco, 2, MidpointRounding.AwayFromZero);
            var desconto = decimal.Round(pedido.DescontoValor, 2, MidpointRounding.AwayFromZero);

            // O teto é de digitação: três bilhões de unidades eram aceitos aqui e só
            // estouravam (500) ao fechar a venda, na conta do estoque.
            if (quantidade <= 0 || quantidade > MaximoPorLinha)
            {
                throw new RegraDeNegocioException(
                    $"Quantidade inválida para {item.Nome}: de 0,01 a 100.000 por linha.",
                    "QUANTIDADE");
            }

            // O desconto é da linha: maior que ela, o que sobrava comia as outras linhas —
            // um serviço com desconto acima do preço zerava a venda com o produto junto.
            if (desconto > decimal.Round(preco * quantidade, 2, MidpointRounding.AwayFromZero))
            {
                throw new RegraDeNegocioException(
                    $"O desconto de {item.Nome} é maior que o valor da linha.", "DESCONTO_INVALIDO");
            }

            if (item.Tipo == TipoItem.Produto && item.Estoque is { } estoque &&
                quantidade > estoque)
            {
                throw new RegraDeNegocioException(
                    $"Estoque insuficiente de {item.Nome}: {estoque} disponível(is).", "ESTOQUE");
            }

            // O pedido manda quem leva a comissão, e aí é uma linha só. Sem isso, a linha
            // se divide entre quem prestou: o mesmo serviço pode ter sido prestado por
            // duas pessoas no mesmo atendimento, e cada uma recebe pelo que fez.
            var partes = pedido.VendedorId is not null
                ? new List<(long? Quem, decimal Quantidade)> { (pedido.VendedorId, quantidade) }
                : VendaService.DividirEntreQuemPrestou(
                    quemPrestou.TryGetValue(item.Id, out var fila) ? fila : null,
                    quantidade);

            // O desconto pedido é da linha inteira: dividida, ele acompanha as partes na
            // proporção das unidades, e a soma bate no centavo.
            var descontos = VendaService.DividirDesconto(
                desconto, partes.Select(p => p.Quantidade).ToList());

            for (var indiceDaParte = 0; indiceDaParte < partes.Count; indiceDaParte++)
            {
                venda.Itens.Add(new VendaItem
                {
                    ItemCatalogoId = item.Id,
                    Tipo = item.Tipo,
                    Nome = item.Nome,
                    Quantidade = partes[indiceDaParte].Quantidade,
                    // O preço do catálogo vale, salvo quando quem tem permissão manda outro.
                    PrecoUnitario = preco,
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

        venda.DescontoGeral = decimal.Round(req.DescontoGeral, 2, MidpointRounding.AwayFromZero);
        venda.Observacao = req.Observacao;
        _vendas.RecalcularTotais(venda);

        if (venda.DescontoGeral > venda.Itens.Sum(i => i.TotalLiquido))
        {
            throw new RegraDeNegocioException(
                "O desconto geral é maior que o valor dos itens.", "DESCONTO_INVALIDO");
        }
    }

    /// <summary>A venda como a Api a devolve: lida de novo, sem rastreio, com o cliente.</summary>
    private async Task<Venda?> CarregarAsync(long id, CancellationToken ct)
    {
        var venda = await _db.Vendas
            .AsNoTracking()
            .Include(v => v.Vendedor)
            // O vendedor de cada item: é o nome que o checkout mostra ao lado do serviço.
            .Include(v => v.Itens).ThenInclude(i => i.Vendedor)
            .Include(v => v.Pagamentos).ThenInclude(p => p.FormaPagamento)
            .FirstOrDefaultAsync(v => v.Id == id, ct);

        if (venda is not null)
        {
            await ComClientesAsync(new[] { venda }, ct);
        }
        return venda;
    }

    /// <summary>
    /// Põe o cliente em cada venda — inclusive o que foi excluído depois: a venda dele
    /// continua sendo venda, com dinheiro recebido e a receber. Só os da empresa.
    /// </summary>
    /// <remarks>Só para vendas lidas sem rastreio: é leitura para a resposta.</remarks>
    private async Task ComClientesAsync(IReadOnlyCollection<Venda> vendas, CancellationToken ct)
    {
        var ids = vendas.Select(v => v.ClienteId).Distinct().ToList();
        var clientes = await _db.Clientes.IgnoreQueryFilters().AsNoTracking()
            .Where(c => ids.Contains(c.Id) && c.TenantId == TenantId)
            .ToDictionaryAsync(c => c.Id, ct);

        foreach (var venda in vendas)
        {
            venda.Cliente = clientes.GetValueOrDefault(venda.ClienteId);
        }
    }
}

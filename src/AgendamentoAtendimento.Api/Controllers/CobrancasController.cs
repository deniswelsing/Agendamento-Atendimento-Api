using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>
/// Cobrança de uma venda pela maquininha, por Pix ou por gateway.
///
/// O fluxo tem três passos de propósito: abrir, enviar e concluir. Abrir grava a intenção
/// antes de o cliente ser cobrado, e é o que permite saber que uma cobrança ficou sem
/// resposta em vez de perder o dinheiro de vista.
/// </summary>
[Route("api")]
public class CobrancasController : ControllerBaseApi
{
    private readonly AppDbContext _db;
    private readonly CobrancaService _cobrancas;
    private readonly VendaService _vendas;

    public CobrancasController(AppDbContext db, CobrancaService cobrancas, VendaService vendas)
    {
        _db = db;
        _cobrancas = cobrancas;
        _vendas = vendas;
    }

    /// <summary>
    /// Abre a cobrança. Repetir a mesma `chaveIdempotencia` devolve a cobrança que já
    /// existe, com `jaExistia: true` — nunca cobra o cliente duas vezes.
    /// </summary>
    [HttpPost("vendas/{vendaId:long}/cobrancas")]
    [RequerPermissao("financeiro.receber")]
    public async Task<ActionResult<CobrancaDto>> Abrir(
        long vendaId, AbrirCobrancaRequest req, CancellationToken ct)
    {
        var venda = NaoNulo(
            await _db.Vendas.Include(v => v.Itens).Include(v => v.Pagamentos)
                .FirstOrDefaultAsync(v => v.Id == vendaId, ct),
            "Venda não encontrada.");

        if (venda.Status is StatusVenda.Cancelada or StatusVenda.Estornada)
        {
            throw new RegraDeNegocioException("Venda cancelada não recebe cobrança.", "STATUS_FINAL");
        }

        try
        {
            var (cobranca, jaExistia) = await _cobrancas.AbrirAsync(
                venda, req.FormaPagamentoId, req.Valor, req.Meio, req.ChaveIdempotencia,
                req.Parcelas, req.AdquirenteChave, req.TerminalSerie, ct);

            // Repetição devolve 200 com o mesmo corpo; criação de verdade devolve 201.
            var dto = cobranca.ParaDto(jaExistia);
            return jaExistia
                ? Ok(dto)
                : CreatedAtAction(nameof(Obter), new { id = cobranca.Id }, dto);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new RegraDeNegocioException(ex.Message, "COBRANCA_INVALIDA");
        }
    }

    [HttpGet("cobrancas/{id:long}")]
    [RequerPermissao("financeiro.ver")]
    public async Task<ActionResult<CobrancaDto>> Obter(long id, CancellationToken ct)
    {
        var cobranca = NaoNulo(await _cobrancas.ObterAsync(id, ct), "Cobrança não encontrada.");
        return Ok(cobranca.ParaDto());
    }

    /// <summary>
    /// Marca que a cobrança saiu: o terminal está com o cliente, ou o QR está na tela.
    /// </summary>
    [HttpPost("cobrancas/{id:long}/enviar")]
    [RequerPermissao("financeiro.receber")]
    public async Task<ActionResult<CobrancaDto>> Enviar(
        long id, EnviarCobrancaRequest req, CancellationToken ct)
    {
        var cobranca = NaoNulo(await _cobrancas.ObterAsync(id, ct), "Cobrança não encontrada.");
        try
        {
            await _cobrancas.MarcarEnviadaAsync(cobranca, req.PixCopiaECola, ct);
            return Ok(cobranca.ParaDto());
        }
        catch (InvalidOperationException ex)
        {
            throw new RegraDeNegocioException(ex.Message, "COBRANCA_FECHADA");
        }
    }

    /// <summary>
    /// Fecha a cobrança com o que o terminal, o PSP ou o gateway respondeu. Aprovada vira
    /// pagamento; recusada não vira nada. Repetir a chamada não duplica o lançamento.
    /// </summary>
    [HttpPost("cobrancas/{id:long}/concluir")]
    [RequerPermissao("financeiro.receber")]
    public async Task<ActionResult<CobrancaDto>> Concluir(
        long id, ConcluirCobrancaRequest req, CancellationToken ct)
    {
        var cobranca = NaoNulo(await _cobrancas.ObterAsync(id, ct), "Cobrança não encontrada.");
        try
        {
            await _cobrancas.ConcluirAsync(cobranca, new ResultadoDaCaptura(
                req.Aprovada, req.Nsu, req.CodigoAutorizacao, req.Bandeira, req.UltimosDigitos,
                req.TransacaoExternaId, req.ValorTaxaReal, req.MotivoRecusa), ct);

            var atualizada = await _cobrancas.ObterAsync(id, ct);
            return Ok(atualizada!.ParaDto());
        }
        catch (InvalidOperationException ex)
        {
            throw new RegraDeNegocioException(ex.Message, "COBRANCA_FECHADA");
        }
    }

    [HttpPost("cobrancas/{id:long}/cancelar")]
    [RequerPermissao("financeiro.receber")]
    public async Task<ActionResult<CobrancaDto>> Cancelar(
        long id, CancelarCobrancaRequest req, CancellationToken ct)
    {
        var cobranca = NaoNulo(await _cobrancas.ObterAsync(id, ct), "Cobrança não encontrada.");
        try
        {
            await _cobrancas.CancelarAsync(cobranca, req.Motivo, ct);
            return Ok(cobranca.ParaDto());
        }
        catch (InvalidOperationException ex)
        {
            throw new RegraDeNegocioException(ex.Message, "COBRANCA_FECHADA");
        }
    }

    /// <summary>Cobranças de uma venda, da mais recente para a mais antiga.</summary>
    [HttpGet("vendas/{vendaId:long}/cobrancas")]
    [RequerPermissao("financeiro.ver")]
    public async Task<ActionResult<IReadOnlyList<CobrancaDto>>> DaVenda(
        long vendaId, CancellationToken ct)
    {
        // A venda de outra empresa não existe aqui: 404, e não uma lista vazia.
        if (!await _db.Vendas.AsNoTracking().AnyAsync(v => v.Id == vendaId, ct))
        {
            throw new NaoEncontradoException("Venda não encontrada.");
        }

        var cobrancas = await _db.Cobrancas.AsNoTracking()
            .Include(c => c.FormaPagamento)
            .Where(c => c.VendaId == vendaId)
            .OrderByDescending(c => c.Id)
            .ToListAsync(ct);

        return Ok(cobrancas.Select(c => c.ParaDto()).ToList());
    }

    /// <summary>
    /// Corrige a taxa de um pagamento com o que a adquirente cobrou de verdade. É o que
    /// transforma o líquido de previsão em fato, e o que revela a divergência.
    /// </summary>
    [HttpPost("pagamentos/{id:long}/conciliar")]
    [RequerPermissao("financeiro.receber")]
    public async Task<ActionResult<PagamentoDto>> Conciliar(
        long id, ConciliarPagamentoRequest req, CancellationToken ct)
    {
        var pagamento = NaoNulo(
            await _db.Pagamentos.Include(p => p.FormaPagamento)
                .FirstOrDefaultAsync(p => p.Id == id, ct),
            "Pagamento não encontrado.");

        if (pagamento.Status != StatusPagamento.Confirmado)
        {
            throw new RegraDeNegocioException(
                "Só um pagamento confirmado é conciliado.", "STATUS_INVALIDO");
        }

        try
        {
            _vendas.ConciliarTaxa(pagamento, req.ValorTaxaReal);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new RegraDeNegocioException(ex.Message, "TAXA_INVALIDA");
        }

        await _db.SaveChangesAsync(ct);
        return Ok(pagamento.ParaDto());
    }

    /// <summary>
    /// Pagamentos cujo líquido ainda é previsão, ou cuja taxa real veio diferente do
    /// previsto. É a fila de conferência do caixa.
    /// </summary>
    [HttpGet("pagamentos/divergencias")]
    [RequerPermissao("financeiro.ver")]
    public async Task<ActionResult<IReadOnlyList<PagamentoDto>>> Divergencias(CancellationToken ct)
    {
        var pagamentos = await _db.Pagamentos.AsNoTracking()
            .Include(p => p.FormaPagamento)
            .Where(p => p.Status == StatusPagamento.Confirmado
                        && (!p.TaxaConferida || p.ValorTaxa != p.ValorTaxaEstimada))
            .OrderByDescending(p => p.Id)
            .ToListAsync(ct);

        return Ok(pagamentos.Select(p => p.ParaDto()).ToList());
    }
}

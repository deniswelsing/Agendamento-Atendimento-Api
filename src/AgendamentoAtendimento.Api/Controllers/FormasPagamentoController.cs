using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>Formas de pagamento e suas taxas. O líquido da venda sai daqui.</summary>
[Route("api/formas-pagamento")]
public class FormasPagamentoController : ControllerBaseApi
{
    private readonly AppDbContext _db;

    public FormasPagamentoController(AppDbContext db) => _db = db;

    [HttpGet]
    [RequerPermissao("financeiro.ver")]
    public async Task<ActionResult<IReadOnlyList<FormaPagamentoDto>>> Listar(
        [FromQuery] bool somenteAtivas = true, CancellationToken ct = default)
    {
        var formas = await _db.FormasPagamento
            .AsNoTracking()
            .Where(f => !somenteAtivas || f.Ativa)
            .OrderBy(f => f.Nome)
            .ToListAsync(ct);

        return Ok(formas.Select(f => f.ParaDto()).ToList());
    }

    [HttpGet("{id:long}")]
    [RequerPermissao("financeiro.ver")]
    public async Task<ActionResult<FormaPagamentoDto>> Obter(long id, CancellationToken ct)
    {
        var forma = NaoNulo(
            await _db.FormasPagamento.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct),
            "Forma de pagamento não encontrada.");

        return Ok(forma.ParaDto());
    }

    [HttpPost]
    [RequerPermissao("financeiro.formas")]
    public async Task<ActionResult<FormaPagamentoDto>> Criar(FormaPagamentoRequest req, CancellationToken ct)
    {
        var codigo = Validar(req);
        if (await _db.FormasPagamento.AnyAsync(f => f.Codigo == codigo, ct))
        {
            throw new RegraDeNegocioException($"Já existe uma forma com o código {codigo}.", "CODIGO_DUPLICADO");
        }

        var forma = new FormaPagamento { Nome = req.Nome.Trim(), Codigo = codigo };
        Aplicar(forma, req);

        _db.FormasPagamento.Add(forma);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Obter), new { id = forma.Id }, forma.ParaDto());
    }

    [HttpPut("{id:long}")]
    [RequerPermissao("financeiro.formas")]
    public async Task<ActionResult<FormaPagamentoDto>> Atualizar(
        long id, FormaPagamentoRequest req, CancellationToken ct)
    {
        var codigo = Validar(req);
        var forma = NaoNulo(
            await _db.FormasPagamento.FirstOrDefaultAsync(f => f.Id == id, ct),
            "Forma de pagamento não encontrada.");

        if (await _db.FormasPagamento.AnyAsync(f => f.Codigo == codigo && f.Id != id, ct))
        {
            throw new RegraDeNegocioException($"Já existe uma forma com o código {codigo}.", "CODIGO_DUPLICADO");
        }

        forma.Nome = req.Nome.Trim();
        forma.Codigo = codigo;
        Aplicar(forma, req);

        await _db.SaveChangesAsync(ct);
        return Ok(forma.ParaDto());
    }

    [HttpDelete("{id:long}")]
    [RequerPermissao("financeiro.formas")]
    public async Task<IActionResult> Remover(long id, CancellationToken ct)
    {
        var forma = NaoNulo(
            await _db.FormasPagamento.FirstOrDefaultAsync(f => f.Id == id, ct),
            "Forma de pagamento não encontrada.");

        if (await _db.Pagamentos.AnyAsync(p => p.FormaPagamentoId == id, ct))
        {
            // Já foi usada em recebimento: desativa em vez de apagar, para não furar o histórico.
            forma.Ativa = false;
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }

        _db.FormasPagamento.Remove(forma);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static void Aplicar(FormaPagamento forma, FormaPagamentoRequest req)
    {
        forma.Ativa = req.Ativa;
        forma.PermiteParcelamento = req.PermiteParcelamento;
        forma.MaximoParcelas = req.PermiteParcelamento ? Math.Max(1, req.MaximoParcelas) : 1;
        forma.TaxaPercentual = req.TaxaPercentual;
        forma.TaxaFixa = req.TaxaFixa;
        forma.DiasParaLiquidacao = Math.Max(0, req.DiasParaLiquidacao);
    }

    private static string Validar(FormaPagamentoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nome) || string.IsNullOrWhiteSpace(req.Codigo))
        {
            throw new RegraDeNegocioException("Informe nome e código.", "DADOS_OBRIGATORIOS");
        }

        if (req.TaxaPercentual is < 0 or > 100)
        {
            throw new RegraDeNegocioException("A taxa percentual deve ficar entre 0 e 100.", "TAXA");
        }

        return req.Codigo.Trim().ToUpperInvariant();
    }
}

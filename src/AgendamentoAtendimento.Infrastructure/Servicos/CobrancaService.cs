using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Infrastructure.Servicos;

/// <summary>Resultado que o terminal, o PSP ou o gateway devolveu.</summary>
public sealed record ResultadoDaCaptura(
    bool Aprovada,
    string? Nsu = null,
    string? CodigoAutorizacao = null,
    string? Bandeira = null,
    string? UltimosDigitos = null,
    string? TransacaoExternaId = null,
    decimal? ValorTaxaReal = null,
    string? MotivoRecusa = null);

/// <summary>
/// O ciclo de uma cobrança, igual para maquininha, Pix e gateway online.
///
/// A ordem importa: <see cref="AbrirAsync"/> grava a intenção ANTES de qualquer coisa ser
/// cobrada do cliente. Se o app cair no meio, a cobrança fica aberta e pendente em vez de
/// virar dinheiro cobrado e não lançado. Repetir a mesma chave de idempotência devolve a
/// cobrança que já existe — nunca cria uma segunda.
/// </summary>
public class CobrancaService
{
    /// <summary>
    /// Uma cobrança presencial não pode ficar aberta para sempre: o cliente vai embora.
    /// Depois disto ela expira e precisa ser reaberta, nunca confirmada às cegas.
    /// </summary>
    public static readonly TimeSpan JanelaPadrao = TimeSpan.FromMinutes(10);

    private readonly AppDbContext _db;
    private readonly VendaService _vendas;

    public CobrancaService(AppDbContext db, VendaService vendas)
    {
        _db = db;
        _vendas = vendas;
    }

    public async Task<Cobranca?> ObterAsync(long id, CancellationToken ct = default) =>
        await _db.Cobrancas
            .Include(c => c.FormaPagamento)
            .Include(c => c.Pagamento)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

    /// <summary>
    /// Abre a intenção de cobrar. Devolve a cobrança existente quando a chave se repete —
    /// é isto que impede o cliente de ser cobrado duas vezes por um toque repetido.
    /// </summary>
    public async Task<(Cobranca Cobranca, bool JaExistia)> AbrirAsync(
        Venda venda,
        long formaPagamentoId,
        decimal valor,
        MeioDeCaptura meio,
        string chaveIdempotencia,
        int parcelas = 1,
        string? adquirenteChave = null,
        string? terminalSerie = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(venda);
        if (string.IsNullOrWhiteSpace(chaveIdempotencia))
        {
            throw new ArgumentException("A chave de idempotência é obrigatória.", nameof(chaveIdempotencia));
        }

        // A chave é gravada sem espaços nas pontas; procurar sem o mesmo corte deixava
        // " toque-1" passar pela idempotência e esbarrar no índice único ao gravar.
        var chave = chaveIdempotencia.Trim();
        var existente = await _db.Cobrancas
            .Include(c => c.FormaPagamento)
            .Include(c => c.Pagamento)
            .FirstOrDefaultAsync(c => c.ChaveIdempotencia == chave, ct);

        if (existente is not null)
        {
            return (existente, true);
        }

        if (valor <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(valor), "O valor da cobrança deve ser positivo.");
        }

        _vendas.RecalcularTotais(venda);
        if (valor > venda.SaldoAberto)
        {
            throw new InvalidOperationException(
                $"O valor excede o saldo em aberto ({venda.SaldoAberto:0.00}).");
        }

        // Duas cobranças abertas na mesma venda cobrariam o cliente duas vezes pelo mesmo
        // saldo. Só uma de cada vez.
        //
        // A que passou do prazo não conta: ela nunca mais vira pagamento (concluir e
        // cancelar recusam cobrança expirada), e contá-la prendia a venda para sempre
        // quando o app caía no meio e ninguém rodava a expiração.
        var agora = DateTimeOffset.UtcNow;
        var vencidas = await _db.Cobrancas
            .Where(c => c.VendaId == venda.Id
                        && (c.Status == StatusCobranca.Criada || c.Status == StatusCobranca.EmAndamento)
                        && c.ExpiraEm <= agora)
            .ToListAsync(ct);
        foreach (var vencida in vencidas)
        {
            vencida.Status = StatusCobranca.Expirada;
            vencida.RespondidaEm = agora;
        }

        var jaAberta = await _db.Cobrancas.AnyAsync(
            c => c.VendaId == venda.Id
                 && (c.Status == StatusCobranca.Criada || c.Status == StatusCobranca.EmAndamento)
                 && c.ExpiraEm > agora, ct);
        if (jaAberta)
        {
            throw new InvalidOperationException(
                "Esta venda já tem uma cobrança em andamento. Conclua ou cancele antes de abrir outra.");
        }

        var forma = await _db.FormasPagamento.FirstOrDefaultAsync(f => f.Id == formaPagamentoId, ct)
            ?? throw new InvalidOperationException("Forma de pagamento não encontrada.");

        var cobranca = new Cobranca
        {
            VendaId = venda.Id,
            ChaveIdempotencia = chave,
            Meio = meio,
            Status = StatusCobranca.Criada,
            FormaPagamentoId = forma.Id,
            Valor = decimal.Round(valor, 2, MidpointRounding.AwayFromZero),
            Parcelas = Math.Max(1, parcelas),
            AdquirenteChave = adquirenteChave,
            TerminalSerie = terminalSerie,
            ExpiraEm = DateTimeOffset.UtcNow.Add(JanelaPadrao),
        };

        _db.Cobrancas.Add(cobranca);
        await _db.SaveChangesAsync(ct);
        return (cobranca, false);
    }

    /// <summary>
    /// Marca que a cobrança saiu para o mundo: o terminal está com o cliente, ou o QR
    /// está na tela. A partir daqui só o resultado da captura muda o estado.
    /// </summary>
    public async Task<Cobranca> MarcarEnviadaAsync(
        Cobranca cobranca, string? pixCopiaECola = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cobranca);
        ExigirAberta(cobranca);

        cobranca.Status = StatusCobranca.EmAndamento;
        cobranca.EnviadaEm = DateTimeOffset.UtcNow;
        cobranca.PixCopiaECola = pixCopiaECola ?? cobranca.PixCopiaECola;

        await _db.SaveChangesAsync(ct);
        return cobranca;
    }

    /// <summary>
    /// Fecha a cobrança com o que o terminal respondeu. Aprovada vira pagamento; recusada
    /// não vira nada. Chamar de novo numa cobrança já fechada não duplica o lançamento.
    /// </summary>
    public async Task<Cobranca> ConcluirAsync(
        Cobranca cobranca, ResultadoDaCaptura resultado, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cobranca);
        ArgumentNullException.ThrowIfNull(resultado);

        // Idempotente: a mesma resposta chegando duas vezes devolve o mesmo pagamento.
        if (!cobranca.EstaAberta)
        {
            return cobranca;
        }

        ExigirAberta(cobranca);

        if (!resultado.Aprovada)
        {
            cobranca.Status = StatusCobranca.Recusada;
            cobranca.MotivoRecusa = resultado.MotivoRecusa ?? "Recusada pela adquirente.";
            cobranca.RespondidaEm = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return cobranca;
        }

        var venda = await _db.Vendas
            .Include(v => v.Itens).Include(v => v.Pagamentos)
            .FirstAsync(v => v.Id == cobranca.VendaId, ct);

        var forma = cobranca.FormaPagamento
            ?? await _db.FormasPagamento.FirstAsync(f => f.Id == cobranca.FormaPagamentoId, ct);

        var pagamento = _vendas.MontarPagamento(
            venda, forma, cobranca.Valor, cobranca.Parcelas, cobranca.Meio);

        pagamento.Nsu = resultado.Nsu;
        pagamento.Autorizacao = resultado.CodigoAutorizacao;
        pagamento.Bandeira = resultado.Bandeira;
        pagamento.UltimosDigitos = resultado.UltimosDigitos;
        pagamento.AdquirenteChave = cobranca.AdquirenteChave;

        // A adquirente informou a taxa: o líquido deixa de ser previsão.
        if (resultado.ValorTaxaReal is { } taxaReal)
        {
            _vendas.ConciliarTaxa(pagamento, taxaReal);
        }

        venda.Pagamentos.Add(pagamento);
        _vendas.AtualizarStatus(venda);

        cobranca.Status = StatusCobranca.Aprovada;
        cobranca.Nsu = resultado.Nsu;
        cobranca.CodigoAutorizacao = resultado.CodigoAutorizacao;
        cobranca.Bandeira = resultado.Bandeira;
        cobranca.UltimosDigitos = resultado.UltimosDigitos;
        cobranca.TransacaoExternaId = resultado.TransacaoExternaId;
        cobranca.ValorTaxaReal = resultado.ValorTaxaReal;
        cobranca.RespondidaEm = DateTimeOffset.UtcNow;
        cobranca.Pagamento = pagamento;

        await _db.SaveChangesAsync(ct);
        return cobranca;
    }

    /// <summary>Desiste da cobrança antes de ela virar dinheiro.</summary>
    public async Task<Cobranca> CancelarAsync(
        Cobranca cobranca, string? motivo = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cobranca);
        ExigirAberta(cobranca);

        cobranca.Status = StatusCobranca.Cancelada;
        cobranca.MotivoRecusa = motivo;
        cobranca.RespondidaEm = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return cobranca;
    }

    /// <summary>
    /// Fecha o que ficou aberto além da janela. Uma cobrança expirada nunca vira pagamento:
    /// se o dinheiro entrou mesmo assim, aparece na conciliação como transação sem lançamento,
    /// que é um problema visível — melhor que um lançamento inventado.
    /// </summary>
    public async Task<int> ExpirarVencidasAsync(CancellationToken ct = default)
    {
        var agora = DateTimeOffset.UtcNow;
        var vencidas = await _db.Cobrancas
            .Where(c => (c.Status == StatusCobranca.Criada || c.Status == StatusCobranca.EmAndamento)
                        && c.ExpiraEm <= agora)
            .ToListAsync(ct);

        foreach (var cobranca in vencidas)
        {
            cobranca.Status = StatusCobranca.Expirada;
            cobranca.RespondidaEm = agora;
        }

        if (vencidas.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }
        return vencidas.Count;
    }

    private static void ExigirAberta(Cobranca cobranca)
    {
        if (!cobranca.EstaAberta)
        {
            throw new InvalidOperationException(
                $"Esta cobrança já está {cobranca.Status.ToString().ToLowerInvariant()}.");
        }

        if (cobranca.Expirou(DateTimeOffset.UtcNow))
        {
            throw new InvalidOperationException(
                "Esta cobrança expirou. Abra outra em vez de confirmar às cegas.");
        }
    }
}

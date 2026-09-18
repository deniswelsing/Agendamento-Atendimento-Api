using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>
/// Assinatura da plataforma, compartilhada com o PetShop.Route. Todo preço sai daqui em
/// USD: o app exibe o número que este controller devolve.
/// </summary>
[Route("api/assinatura")]
public class AssinaturaController : ControllerBaseApi
{
    private readonly AppDbContext _db;
    private readonly AssinaturaService _assinaturas;
    private readonly IConfiguration _config;

    public AssinaturaController(AppDbContext db, AssinaturaService assinaturas, IConfiguration config)
    {
        _db = db;
        _assinaturas = assinaturas;
        _config = config;
    }

    [HttpGet("planos")]
    [RequerPermissao("assinatura.ver")]
    public async Task<ActionResult<IReadOnlyList<PlanoDto>>> Planos(CancellationToken ct)
    {
        var planos = await _db.Planos
            .AsNoTracking().Where(p => p.Ativo).OrderBy(p => p.Ordem).ToListAsync(ct);

        return Ok(planos.Select(p => p.ParaDto()).ToList());
    }

    [HttpGet("atual")]
    [RequerPermissao("assinatura.ver")]
    public async Task<ActionResult<AssinaturaDto>> Atual(CancellationToken ct)
    {
        var assinatura = NaoNulo(
            await _assinaturas.ObterAtualAsync(ct),
            "Esta empresa ainda não tem assinatura.");

        var emUso = await _assinaturas.AssentosEmUsoAsync(ct);
        return Ok(assinatura.ParaDto(emUso));
    }

    /// <summary>
    /// Cotação oficial: preço do plano + US$ 10 por usuário além dos inclusos
    /// (US$ 120 no ciclo anual).
    /// </summary>
    [HttpPost("cotacao")]
    [RequerPermissao("assinatura.ver")]
    public async Task<ActionResult<CotacaoDto>> Cotar(CotacaoRequest req, CancellationToken ct)
    {
        var detalhe = await _assinaturas.CotarAsync(req.PlanoId, req.Ciclo, req.Assentos, ct)
            ?? throw new NaoEncontradoException("Plano não encontrado.");

        return Ok(detalhe.ParaDto());
    }

    /// <summary>
    /// Cria a transação no Paddle com o plano e a quantidade de assentos extras, e devolve
    /// a URL do checkout. A ativação só acontece quando o webhook chega.
    /// </summary>
    [HttpPost("paddle/checkout")]
    [RequerPermissao("assinatura.alterar")]
    public async Task<ActionResult<PaddleCheckoutDto>> CheckoutPaddle(
        PaddleCheckoutRequest req, CancellationToken ct)
    {
        var plano = NaoNulo(
            await _db.Planos.FirstOrDefaultAsync(p => p.Id == req.PlanoId, ct),
            "Plano não encontrado.");

        var emUso = await _assinaturas.AssentosEmUsoAsync(ct);
        var assentos = Math.Max(req.Assentos, emUso);

        if (!plano.Suporta(assentos))
        {
            throw new RegraDeNegocioException(
                $"O plano {plano.Nome} aceita no máximo {plano.LimiteUsuarios} usuários.",
                "ACIMA_DO_LIMITE");
        }

        var precoId = req.Ciclo == CicloCobranca.Mensal
            ? plano.PaddlePriceIdMensal : plano.PaddlePriceIdAnual;

        if (string.IsNullOrWhiteSpace(precoId))
        {
            throw new RegraDeNegocioException(
                $"O plano {plano.Nome} não está publicado no Paddle.", "PLANO_SEM_PADDLE");
        }

        var detalhe = PrecificacaoAssinatura.Calcular(plano, req.Ciclo, assentos);
        var apiKey = _config["Paddle:ApiKey"];

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Sem credencial configurada a Api não inventa um checkout.
            throw new RegraDeNegocioException(
                "Integração com o Paddle não está configurada nesta instalação.", "PADDLE_NAO_CONFIGURADO");
        }

        // A transação é criada pelo backend; o app só abre a URL devolvida.
        var transacao = await CriarTransacaoPaddleAsync(plano, req, assentos, detalhe, ct);
        return Ok(transacao);
    }

    /// <summary>
    /// Recebe a compra feita no Google Play, valida com a Google Play Developer API e só
    /// então libera. O app confirma a compra no Play depois desta resposta.
    /// </summary>
    [HttpPost("google-play/confirmar")]
    [RequerPermissao("assinatura.alterar")]
    public async Task<ActionResult<AssinaturaDto>> ConfirmarPlay(
        PlayPurchaseRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.PurchaseToken))
        {
            throw new RegraDeNegocioException("purchaseToken é obrigatório.", "TOKEN_OBRIGATORIO");
        }

        if (string.IsNullOrWhiteSpace(_config["GooglePlay:ContaDeServicoJson"]))
        {
            throw new RegraDeNegocioException(
                "Integração com o Google Play não está configurada nesta instalação.",
                "PLAY_NAO_CONFIGURADO");
        }

        // O token só vale depois de validado com o Google; ver docs/INTEGRACOES.md.
        var validado = await ValidarCompraPlayAsync(req, ct);
        if (!validado)
        {
            throw new RegraDeNegocioException(
                "O Google não reconheceu esta compra.", "COMPRA_INVALIDA");
        }

        var assinatura = NaoNulo(await _assinaturas.ObterAtualAsync(ct), "Assinatura não encontrada.");
        assinatura.Gateway = GatewayPagamento.GooglePlay;

        if (string.Equals(req.TipoCompra, "ASSENTO", StringComparison.OrdinalIgnoreCase))
        {
            assinatura.PlayPurchaseTokenAssentos = req.PurchaseToken;
            var incluidos = assinatura.Plano?.UsuariosIncluidos ?? 1;
            assinatura.AssentosContratados = incluidos + Math.Max(0, req.Quantidade);
        }
        else
        {
            assinatura.PlayPurchaseTokenPlano = req.PurchaseToken;
            if (req.PlanoId is { } planoId)
            {
                assinatura.PlanoId = planoId;
            }
            if (req.Ciclo is { } ciclo)
            {
                assinatura.Ciclo = ciclo;
            }
        }

        assinatura.Status = StatusAssinatura.Ativa;
        await _db.SaveChangesAsync(ct);

        var recarregada = await _assinaturas.ObterAtualAsync(ct);
        var emUso = await _assinaturas.AssentosEmUsoAsync(ct);
        return Ok(recarregada!.ParaDto(emUso));
    }

    [HttpPut("assentos")]
    [RequerPermissao("assinatura.alterar")]
    public async Task<ActionResult<AssinaturaDto>> AlterarAssentos(
        AlterarAssentosRequest req, CancellationToken ct)
    {
        var (resultado, assinatura) = await _assinaturas.AlterarAssentosAsync(req.Assentos, ct);
        if (!resultado.Ok || assinatura is null)
        {
            throw new RegraDeNegocioException(
                resultado.Mensagem ?? "Não foi possível alterar os assentos.",
                resultado.Motivo.ToString());
        }

        var emUso = await _assinaturas.AssentosEmUsoAsync(ct);
        return Ok(assinatura.ParaDto(emUso));
    }

    /// <summary>
    /// Chamada real ao Paddle. Fica isolada para que o resto do fluxo seja testável sem
    /// rede; a configuração vive em `Paddle:*`.
    /// </summary>
    private Task<PaddleCheckoutDto> CriarTransacaoPaddleAsync(
        Plano plano, PaddleCheckoutRequest req, int assentos, DetalhePreco detalhe, CancellationToken ct)
    {
        _ = plano;
        _ = assentos;
        _ = detalhe;
        _ = ct;

        throw new RegraDeNegocioException(
            "A criação da transação no Paddle ainda não está implementada nesta instalação. " +
            "Configure Paddle:ApiKey e implemente CriarTransacaoPaddleAsync.",
            "PADDLE_NAO_IMPLEMENTADO");
    }

    private Task<bool> ValidarCompraPlayAsync(PlayPurchaseRequest req, CancellationToken ct)
    {
        _ = req;
        _ = ct;

        throw new RegraDeNegocioException(
            "A validação da compra no Google Play ainda não está implementada nesta instalação. " +
            "Configure GooglePlay:ContaDeServicoJson e implemente ValidarCompraPlayAsync.",
            "PLAY_NAO_IMPLEMENTADO");
    }
}

using System.Text;
using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authorization;
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
    private readonly ContextoAtual _contexto;

    public AssinaturaController(
        AppDbContext db, AssinaturaService assinaturas, IConfiguration config, ContextoAtual contexto)
    {
        _db = db;
        _assinaturas = assinaturas;
        _config = config;
        _contexto = contexto;
    }

    [HttpGet("planos")]
    [RequerPermissao("assinatura.ver")]
    public async Task<ActionResult<IReadOnlyList<PlanoDto>>> Planos(CancellationToken ct)
    {
        var planos = await _db.Planos
            .AsNoTracking().Where(p => p.Ativo).OrderBy(p => p.Ordem).ToListAsync(ct);

        // Cada plano já sai com o catálogo inteiro resolvido: o app mostra a mesma lista de
        // recursos em todos e só varia o visto e o cadeado.
        return Ok(planos.Select(p => p.ParaDto(planos)).ToList());
    }

    /// <summary>
    /// Recursos do catálogo resolvidos contra o plano atual: o que está incluso, o que está
    /// no cadeado e a partir de qual plano cada coisa entra.
    /// </summary>
    [HttpGet("recursos")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<GrupoRecursosDto>>> Recursos(CancellationToken ct)
    {
        var planos = await _db.Planos
            .AsNoTracking().Where(p => p.Ativo).OrderBy(p => p.Ordem).ToListAsync(ct);

        var assinatura = await _assinaturas.ObterAtualAsync(ct);
        var plano = assinatura?.Plano ?? planos.FirstOrDefault();
        if (plano is null)
        {
            return Ok(new List<GrupoRecursosDto>());
        }

        var grupos = Mapeamentos.CatalogoPara(plano, planos)
            .GroupBy(r => r.Grupo)
            .Select(g => new GrupoRecursosDto(g.Key, g.ToList()))
            .ToList();

        return Ok(grupos);
    }

    [HttpGet("atual")]
    [RequerPermissao("assinatura.ver")]
    public async Task<ActionResult<AssinaturaDto>> Atual(CancellationToken ct)
    {
        var assinatura = NaoNulo(
            await _assinaturas.ObterAtualAsync(ct),
            "Esta empresa ainda não tem assinatura.");

        var emUso = await _assinaturas.AssentosEmUsoAsync(ct);
        var planos = await PlanosAtivosAsync(ct);
        return Ok(assinatura.ParaDto(emUso, planos));
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

        // A compra de assento no Play é a quantidade de ADICIONAIS: o total contratado é
        // o que o plano inclui mais o que foi comprado. É um dos dois caminhos (o outro é
        // o webhook do Paddle) que aumentam assentos.
        ResultadoAssinatura resultado;
        if (string.Equals(req.TipoCompra, "ASSENTO", StringComparison.OrdinalIgnoreCase))
        {
            assinatura.PlayPurchaseTokenAssentos = req.PurchaseToken;
            var incluidos = assinatura.Plano?.UsuariosIncluidos ?? 1;
            (resultado, _) = await _assinaturas.AplicarCompraConfirmadaAsync(
                GatewayPagamento.GooglePlay, null, null, incluidos + Math.Max(0, req.Quantidade), ct);
        }
        else
        {
            assinatura.PlayPurchaseTokenPlano = req.PurchaseToken;
            (resultado, _) = await _assinaturas.AplicarCompraConfirmadaAsync(
                GatewayPagamento.GooglePlay, req.PlanoId, req.Ciclo, null, ct);
        }

        if (!resultado.Ok)
        {
            throw new RegraDeNegocioException(
                resultado.Mensagem ?? "Não foi possível aplicar a compra.", resultado.Motivo.ToString());
        }

        var recarregada = await _assinaturas.ObterAtualAsync(ct);
        var emUso = await _assinaturas.AssentosEmUsoAsync(ct);
        return Ok(recarregada!.ParaDto(emUso, await PlanosAtivosAsync(ct)));
    }

    /// <summary>
    /// Webhook do Paddle. É por aqui — e só por aqui, do lado do Paddle — que a compra
    /// feita no checkout vira plano, ciclo e assentos. A assinatura do cabeçalho
    /// `Paddle-Signature` é conferida com `Paddle:WebhookSecret`; sem segredo configurado a
    /// rota recusa tudo. Evento repetido (mesmo `event_id`) responde 200 sem reaplicar.
    /// </summary>
    [HttpPost("paddle/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> ReceberWebhookPaddle(CancellationToken ct)
    {
        using var leitor = new StreamReader(Request.Body, Encoding.UTF8);
        var corpo = await leitor.ReadToEndAsync(ct);

        var segredo = _config["Paddle:WebhookSecret"];
        if (!WebhookPaddle.AssinaturaValida(
                Request.Headers["Paddle-Signature"].FirstOrDefault(), corpo, segredo, DateTimeOffset.UtcNow))
        {
            return Unauthorized(new ErroApi("Assinatura do webhook inválida.", "WEBHOOK_INVALIDO"));
        }

        var evento = WebhookPaddle.Ler(corpo);
        if (evento is null)
        {
            return BadRequest(new ErroApi("Evento do Paddle ilegível.", "WEBHOOK_ILEGIVEL"));
        }

        // O webhook não tem usuário nem tenant no token: o tenant vem do `custom_data` que o
        // próprio backend pôs na transação, ou da assinatura já ligada ao Paddle.
        var tenantId = evento.TenantId;
        if (tenantId is null && evento.SubscriptionId is { } sub)
        {
            _contexto.IgnorarFiltroDeTenant = true;
            tenantId = await _db.Assinaturas.AsNoTracking()
                .Where(a => a.PaddleSubscriptionId == sub).Select(a => (long?)a.TenantId)
                .FirstOrDefaultAsync(ct);
            _contexto.IgnorarFiltroDeTenant = false;
        }

        if (!await _assinaturas.RegistrarEventoAsync(
                GatewayPagamento.Paddle, evento.EventoId, evento.Tipo, corpo, tenantId, ct))
        {
            return Ok(); // já recebido: o Paddle só precisa saber que chegou
        }

        var registro = await _db.EventosGateway
            .FirstAsync(e => e.Gateway == GatewayPagamento.Paddle && e.EventoExternoId == evento.EventoId, ct);

        if (!evento.ConfirmaCompra || tenantId is not { } tenant)
        {
            // Outros eventos (falha de cobrança, cancelamento…) ficam gravados, sem
            // processamento automático por enquanto.
            return Ok();
        }

        _contexto.AssumirTenant(tenant);
        var (resultado, assinatura) = await _assinaturas.AplicarCompraConfirmadaAsync(
            GatewayPagamento.Paddle, evento.PlanoId, evento.Ciclo, evento.Assentos, ct);

        if (assinatura is not null)
        {
            assinatura.PaddleSubscriptionId = evento.SubscriptionId ?? assinatura.PaddleSubscriptionId;
            assinatura.PaddleCustomerId = evento.CustomerId ?? assinatura.PaddleCustomerId;
        }

        registro.AssinaturaId = assinatura?.Id;
        registro.Processado = resultado.Ok;
        registro.ProcessadoEm = DateTimeOffset.UtcNow;
        registro.Erro = resultado.Ok ? null : resultado.Mensagem;
        await _db.SaveChangesAsync(ct);

        return Ok();
    }

    /// <summary>
    /// Reduz os assentos contratados (nunca abaixo dos em uso nem dos inclusos no plano).
    /// Aumentar responde 402 `ASSENTOS_EXIGEM_PAGAMENTO`: assento a mais é compra, e entra
    /// pelo checkout do Paddle ou pelo Google Play.
    /// </summary>
    [HttpPut("assentos")]
    [RequerPermissao("assinatura.alterar")]
    public async Task<ActionResult<AssinaturaDto>> AlterarAssentos(
        AlterarAssentosRequest req, CancellationToken ct)
    {
        var (resultado, assinatura) = await _assinaturas.AlterarAssentosAsync(req.Assentos, ct);
        if (resultado.Motivo == MotivoRecusa.AssentosExigemPagamento)
        {
            throw new AssinaturaExigidaException(
                resultado.Mensagem ?? "Assentos adicionais precisam ser comprados.",
                resultado.Motivo, "ASSENTOS_EXIGEM_PAGAMENTO");
        }

        if (!resultado.Ok || assinatura is null)
        {
            throw new RegraDeNegocioException(
                resultado.Mensagem ?? "Não foi possível alterar os assentos.",
                resultado.Motivo.ToString());
        }

        var emUso = await _assinaturas.AssentosEmUsoAsync(ct);
        return Ok(assinatura.ParaDto(emUso, await PlanosAtivosAsync(ct)));
    }

    private Task<List<Plano>> PlanosAtivosAsync(CancellationToken ct) =>
        _db.Planos.AsNoTracking().Where(p => p.Ativo).OrderBy(p => p.Ordem).ToListAsync(ct);

    /// <summary>
    /// Chamada real ao Paddle. Fica isolada para que o resto do fluxo seja testável sem
    /// rede; a configuração vive em `Paddle:*`.
    /// </summary>
    /// <remarks>
    /// Quem implementar: a transação TEM de levar o `custom_data` de
    /// <see cref="WebhookPaddle.DadosDaCompra"/> (tenant, plano, ciclo e o total de
    /// assentos). É dele que o webhook tira o que aplicar — sem ele, a compra é paga e os
    /// assentos não sobem.
    /// </remarks>
    private Task<PaddleCheckoutDto> CriarTransacaoPaddleAsync(
        Plano plano, PaddleCheckoutRequest req, int assentos, DetalhePreco detalhe, CancellationToken ct)
    {
        _ = WebhookPaddle.DadosDaCompra(TenantId, plano.Id, req.Ciclo, assentos);
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

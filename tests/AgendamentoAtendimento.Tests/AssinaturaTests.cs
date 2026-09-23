using System.Security.Claims;
using System.Text;
using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Api.Controllers;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.MultiTenancy;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// Assentos e assinatura em dia. Três buracos fechados aqui: o PUT de assentos aumentava
/// o contratado sem pagar; a assinatura só era conferida no bootstrap (o resto da Api
/// seguia aberto com ela cancelada); e a assinatura lida era a da primeira empresa do
/// banco, para todas as empresas.
/// </summary>
public class AssinaturaTests : IAsyncLifetime
{
    private const string Segredo = "segredo-do-webhook";

    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;
    private CacheDeAssinatura _cache = null!;

    private Plano _plano = null!;
    private Assinatura _daEmpresaUm = null!;
    private Assinatura _daEmpresaDois = null!;

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"assinatura-{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(opcoes, _contexto);
        _cache = new CacheDeAssinatura();

        _db.Tenants.AddRange(
            new Tenant { Id = 1, Slug = "um", NomeEmpresa = "Um" },
            new Tenant { Id = 2, Slug = "dois", NomeEmpresa = "Dois" });
        _plano = new Plano
        {
            Id = 1, Codigo = "PLATINUM", Nome = "Platinum", UsuariosIncluidos = 5,
            LimiteUsuarios = 25, PrecoMensalUsd = 49m, Ativo = true,
        };
        _db.Planos.Add(_plano);
        _daEmpresaUm = new Assinatura
        {
            TenantId = 1, PlanoId = 1, Status = StatusAssinatura.Ativa, AssentosContratados = 8,
        };
        _daEmpresaDois = new Assinatura
        {
            TenantId = 2, PlanoId = 1, Status = StatusAssinatura.Ativa, AssentosContratados = 12,
        };
        _db.Assinaturas.AddRange(_daEmpresaUm, _daEmpresaDois);

        var perfil = new Perfil { TenantId = 1, Nome = "Administrador", Administrador = true };
        _db.Perfis.Add(perfil);
        await _db.SaveChangesAsync();

        // Seis assentos em uso na empresa um.
        for (var i = 0; i < 6; i++)
        {
            _db.Usuarios.Add(new Usuario
            {
                TenantId = 1, Nome = $"U{i}", Email = $"u{i}@um.com", PerfilId = perfil.Id,
            });
        }
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private AssinaturaService Servico() => new(_db, _cache);

    private AssinaturaController Controller(IDictionary<string, string?>? config = null)
    {
        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(config ?? new Dictionary<string, string?>())
            .Build();
        return new AssinaturaController(_db, Servico(), configuracao, _contexto)
        {
            ControllerContext = ContextoDoController.Com("*"),
        };
    }

    private async Task<int> ContratadosAsync(long tenantId) =>
        (await _db.Assinaturas.AsNoTracking().FirstAsync(a => a.TenantId == tenantId)).AssentosContratados;

    // ---------------------------------------------------------------- assentos

    [Fact]
    public async Task Aumentar_assentos_pelo_put_responde_402_e_nao_grava()
    {
        var erro = await Assert.ThrowsAsync<AssinaturaExigidaException>(
            () => Controller().AlterarAssentos(new AlterarAssentosRequest(20), default));

        Assert.Equal("ASSENTOS_EXIGEM_PAGAMENTO", erro.Codigo);
        Assert.Contains("checkout", erro.Message);
        Assert.Equal(8, await ContratadosAsync(1));
    }

    [Fact]
    public async Task Reduzir_assentos_e_direto()
    {
        var resposta = await Controller().AlterarAssentos(new AlterarAssentosRequest(7), default);

        var dto = Assert.IsType<AssinaturaDto>(Assert.IsType<OkObjectResult>(resposta.Result).Value);
        Assert.Equal(7, dto.AssentosContratados);
        Assert.Equal(7, await ContratadosAsync(1));
    }

    [Fact]
    public async Task Reduzir_nunca_fica_abaixo_dos_assentos_em_uso()
    {
        // Seis em uso; pedir dois reduz até os seis.
        await Controller().AlterarAssentos(new AlterarAssentosRequest(2), default);

        Assert.Equal(6, await ContratadosAsync(1));
    }

    [Fact]
    public async Task Reduzir_nunca_fica_abaixo_dos_inclusos_no_plano()
    {
        // Só um usuário em uso: o piso passa a ser os cinco que o plano inclui.
        foreach (var u in await _db.Usuarios.Skip(1).ToListAsync())
        {
            u.Ativo = false;
        }
        await _db.SaveChangesAsync();

        await Controller().AlterarAssentos(new AlterarAssentosRequest(1), default);

        Assert.Equal(5, await ContratadosAsync(1));
    }

    [Fact]
    public async Task Cada_empresa_le_e_altera_so_a_propria_assinatura()
    {
        _contexto.TenantId = 2;
        var dois = await Servico().ObterAtualAsync();
        Assert.Equal(2, dois!.TenantId);

        await Servico().AlterarAssentosAsync(10);

        Assert.Equal(10, await ContratadosAsync(2));
        Assert.Equal(8, await ContratadosAsync(1));
    }

    [Fact]
    public async Task Compra_confirmada_e_o_que_aumenta_assentos()
    {
        var (resultado, _) = await Servico().AplicarCompraConfirmadaAsync(
            GatewayPagamento.Paddle, null, null, 15);

        Assert.True(resultado.Ok);
        Assert.Equal(15, await ContratadosAsync(1));
    }

    [Fact]
    public async Task Compra_acima_do_limite_do_plano_e_recusada()
    {
        var (resultado, _) = await Servico().AplicarCompraConfirmadaAsync(
            GatewayPagamento.Paddle, null, null, 26);

        Assert.Equal(MotivoRecusa.AcimaDoLimiteDoPlano, resultado.Motivo);
        Assert.Equal(8, await ContratadosAsync(1));
    }

    // ----------------------------------------------------------- webhook Paddle

    private static string EventoPaddle(string id, string tipo, long tenantId, int assentos) =>
        "{\"event_id\":\"" + id + "\",\"event_type\":\"" + tipo + "\",\"occurred_at\":\"2026-09-23T10:00:00Z\"," +
        "\"data\":{\"id\":\"txn_1\",\"status\":\"completed\",\"subscription_id\":\"sub_1\",\"customer_id\":\"ctm_1\"," +
        "\"custom_data\":{\"tenant_id\":\"" + tenantId + "\",\"plano_id\":\"1\",\"ciclo\":\"Mensal\",\"assentos\":" + assentos + "}}}";

    private async Task<IActionResult> ChamarWebhookAsync(string corpo, string? assinatura)
    {
        _contexto.TenantId = null; // o webhook chega sem token
        var controller = Controller(new Dictionary<string, string?> { ["Paddle:WebhookSecret"] = Segredo });
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(corpo));
        if (assinatura is not null)
        {
            http.Request.Headers["Paddle-Signature"] = assinatura;
        }
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return await controller.ReceberWebhookPaddle(default);
    }

    [Fact]
    public async Task Webhook_confirmado_aplica_os_assentos_comprados_uma_vez_so()
    {
        var corpo = EventoPaddle("evt_1", "transaction.completed", 1, 12);
        var assinatura = WebhookPaddle.Assinar(corpo, Segredo, DateTimeOffset.UtcNow);

        Assert.IsType<OkResult>(await ChamarWebhookAsync(corpo, assinatura));
        Assert.Equal(12, await ContratadosAsync(1));
        Assert.Equal(12, _daEmpresaDois.AssentosContratados); // a outra empresa não muda
        Assert.Equal(12, await ContratadosAsync(2));

        var um = await _db.Assinaturas.AsNoTracking().FirstAsync(a => a.TenantId == 1);
        Assert.Equal("sub_1", um.PaddleSubscriptionId);
        Assert.True((await _db.EventosGateway.SingleAsync()).Processado);

        // Reentregue: responde 200, não reaplica.
        _daEmpresaUm.AssentosContratados = 9;
        await _db.SaveChangesAsync();
        Assert.IsType<OkResult>(await ChamarWebhookAsync(corpo, assinatura));
        Assert.Equal(9, await ContratadosAsync(1));
    }

    [Fact]
    public async Task Webhook_sem_assinatura_valida_e_recusado()
    {
        var corpo = EventoPaddle("evt_2", "transaction.completed", 1, 20);

        Assert.IsType<UnauthorizedObjectResult>(await ChamarWebhookAsync(corpo, null));
        Assert.IsType<UnauthorizedObjectResult>(await ChamarWebhookAsync(
            corpo, WebhookPaddle.Assinar(corpo, "outro-segredo", DateTimeOffset.UtcNow)));
        // Assinado certo, mas velho: é replay.
        Assert.IsType<UnauthorizedObjectResult>(await ChamarWebhookAsync(
            corpo, WebhookPaddle.Assinar(corpo, Segredo, DateTimeOffset.UtcNow.AddHours(-1))));

        Assert.Equal(8, await ContratadosAsync(1));
    }

    [Fact]
    public void Leitura_do_evento_tira_o_que_o_checkout_mandou()
    {
        var evento = WebhookPaddle.Ler(EventoPaddle("evt_3", "transaction.completed", 7, 11))!;

        Assert.Equal("evt_3", evento.EventoId);
        Assert.Equal(7, evento.TenantId);
        Assert.Equal(1, evento.PlanoId);
        Assert.Equal(CicloCobranca.Mensal, evento.Ciclo);
        Assert.Equal(11, evento.Assentos);
        Assert.True(evento.ConfirmaCompra);
        Assert.False(WebhookPaddle.Ler(EventoPaddle("evt_4", "transaction.payment_failed", 7, 11))!.ConfirmaCompra);
    }

    // ------------------------------------------------ assinatura em toda requisição

    private static HttpContext Requisicao(string caminho)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = caminho;
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimsApp.TenantId, "1"), new Claim(ClaimsApp.UsuarioId, "1") }, "teste"));
        return http;
    }

    private async Task<bool> PassaAsync(string caminho, AssinaturaService? servico = null)
    {
        var chegou = false;
        var middleware = new AssinaturaEmDiaMiddleware(_ => { chegou = true; return Task.CompletedTask; }, TimeProvider.System);
        await middleware.InvokeAsync(Requisicao(caminho), _contexto, servico ?? Servico());
        return chegou;
    }

    private async Task MudarStatusAsync(StatusAssinatura status, DateTimeOffset? fimDaGraca = null)
    {
        _daEmpresaUm.Status = status;
        _daEmpresaUm.FimPeriodoDeGraca = fimDaGraca;
        await _db.SaveChangesAsync();
        _cache.Invalidar(1);
    }

    [Theory]
    [InlineData(StatusAssinatura.Cancelada)]
    [InlineData(StatusAssinatura.Expirada)]
    [InlineData(StatusAssinatura.Suspensa)]
    [InlineData(StatusAssinatura.Pendente)]
    public async Task Assinatura_parada_recusa_as_rotas_com_402(StatusAssinatura status)
    {
        await MudarStatusAsync(status);

        var erro = await Assert.ThrowsAsync<AssinaturaExigidaException>(() => PassaAsync("/api/clientes"));
        Assert.Equal("ASSINATURA_INATIVA", erro.Codigo);
        await Assert.ThrowsAsync<AssinaturaExigidaException>(() => PassaAsync("/api/agendamentos"));
    }

    [Theory]
    [InlineData("/api/auth/refresh")]
    [InlineData("/api/bootstrap")]
    [InlineData("/api/assinatura/planos")]
    [InlineData("/api/assinatura/paddle/checkout")]
    [InlineData("/api/publico/empresa/agendamentos")]
    [InlineData("/health")]
    [InlineData("/swagger/index.html")]
    public async Task Assinatura_parada_ainda_deixa_entrar_e_pagar(string caminho)
    {
        await MudarStatusAsync(StatusAssinatura.Cancelada);

        Assert.True(await PassaAsync(caminho));
    }

    [Fact]
    public async Task Periodo_de_graca_libera_enquanto_nao_acaba()
    {
        await MudarStatusAsync(StatusAssinatura.EmPeriodoDeGraca, DateTimeOffset.UtcNow.AddDays(3));
        Assert.True(await PassaAsync("/api/clientes"));

        await MudarStatusAsync(StatusAssinatura.EmPeriodoDeGraca, DateTimeOffset.UtcNow.AddMinutes(-1));
        await Assert.ThrowsAsync<AssinaturaExigidaException>(() => PassaAsync("/api/clientes"));
    }

    [Fact]
    public async Task Ativa_ou_sem_assinatura_nenhuma_passa()
    {
        Assert.True(await PassaAsync("/api/clientes"));

        _db.Assinaturas.Remove(_daEmpresaUm);
        await _db.SaveChangesAsync();
        _cache.Invalidar(1);
        Assert.True(await PassaAsync("/api/clientes"));
    }

    [Fact]
    public async Task Situacao_fica_em_cache_ate_alguem_mudar_a_assinatura()
    {
        Assert.True(await PassaAsync("/api/clientes"));

        // Mudou por fora, sem invalidar: dentro da validade o cache ainda responde.
        _daEmpresaUm.Status = StatusAssinatura.Cancelada;
        await _db.SaveChangesAsync();
        Assert.True(await PassaAsync("/api/clientes"));

        // Quem muda pela Api invalida: a próxima requisição já vê.
        Servico().Invalidar();
        await Assert.ThrowsAsync<AssinaturaExigidaException>(() => PassaAsync("/api/clientes"));
    }

    [Fact]
    public async Task Sem_token_quem_responde_e_a_autenticacao()
    {
        await MudarStatusAsync(StatusAssinatura.Cancelada);
        _contexto.TenantId = null;

        Assert.True(await PassaAsync("/api/clientes"));
    }
}

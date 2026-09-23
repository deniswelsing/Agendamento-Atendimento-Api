using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Api.Controllers;
using AgendamentoAtendimento.Domain.MultiTenancy;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Seguranca;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// O mesmo e-mail e a mesma senha em duas empresas. Sem a empresa, o login entrava na
/// primeira que o banco devolvesse — e a pessoa trabalhava no lugar errado sem saber.
/// </summary>
public class LoginEmVariasEmpresasTests : IAsyncLifetime
{
    private const string Email = "ana@teste.com";
    private const string Senha = "Senha@2026";

    private readonly ContextoAtual _contexto = new();
    private AppDbContext _db = null!;

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"login-{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(opcoes, _contexto);

        _contexto.IgnorarFiltroDeTenant = true;
        foreach (var slug in new[] { "empresa-a", "empresa-b" })
        {
            var tenant = new Tenant { Slug = slug, NomeEmpresa = slug };
            _db.Tenants.Add(tenant);
            await _db.SaveChangesAsync();

            var perfil = new Perfil { TenantId = tenant.Id, Nome = "Administrador", Administrador = true };
            _db.Perfis.Add(perfil);
            await _db.SaveChangesAsync();

            _db.Usuarios.Add(new Usuario
            {
                TenantId = tenant.Id, Nome = "Ana", Email = Email, PerfilId = perfil.Id,
                SenhaHash = HashSenha.Gerar(Senha),
            });
        }
        await _db.SaveChangesAsync();
        _contexto.IgnorarFiltroDeTenant = false;
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Sem_a_empresa_pede_a_empresa_em_vez_de_escolher_uma()
    {
        var resposta = await Controller().Login(new LoginRequest(Email, Senha, null, null), default);

        var recusa = Assert.IsType<BadRequestObjectResult>(resposta.Result);
        Assert.Equal("EMPRESA_OBRIGATORIA", Assert.IsType<ErroApi>(recusa.Value).Code);
    }

    [Theory]
    [InlineData("empresa-a")]
    [InlineData("empresa-b")]
    public async Task Com_a_empresa_entra_nela(string slug)
    {
        var resposta = await Controller().Login(new LoginRequest(Email, Senha, slug, null), default);

        var login = Assert.IsType<LoginResponse>(Assert.IsType<OkObjectResult>(resposta.Result).Value);
        Assert.Equal(slug, login.Tenant.Slug);
    }

    [Fact]
    public async Task Senha_errada_continua_sendo_so_credencial_invalida()
    {
        var resposta = await Controller().Login(new LoginRequest(Email, "errada", null, null), default);

        Assert.IsType<UnauthorizedObjectResult>(resposta.Result);
    }

    private AuthController Controller()
    {
        var opcoes = Options.Create(new OpcoesJwt { Chave = new string('k', 64) });
        return new AuthController(_db, _contexto, new ServicoDeToken(opcoes), opcoes)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }
}

using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Api.Controllers;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.MultiTenancy;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Seguranca;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// Convite e sessão do app irmão. O convite criava o usuário com token e validade, e
/// nada o aceitava: quem era convidado nunca conseguia entrar. E o refresh token dividido
/// entre o app e o PetShop.Route rodava a cada uso — quem renovava primeiro derrubava o
/// outro.
/// </summary>
public class ConviteESessaoIrmaTests : IAsyncLifetime
{
    private const string Senha = "Senha@2026";

    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;
    private Perfil _admin = null!;
    private Perfil _atendimento = null!;
    private Usuario _dono = null!;

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"convite-{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(opcoes, _contexto);

        _db.Tenants.Add(new Tenant { Id = 1, Slug = "empresa", NomeEmpresa = "Empresa Um" });
        _db.Planos.Add(new Plano
        {
            Id = 1, Codigo = "PRO", Nome = "Pro", UsuariosIncluidos = 5, LimiteUsuarios = 25,
            Recursos = CatalogoRecursos.Tudo, Ativo = true,
        });
        _db.Assinaturas.Add(new Assinatura
        {
            TenantId = 1, PlanoId = 1, Status = StatusAssinatura.Ativa, AssentosContratados = 5,
        });
        _admin = new Perfil { TenantId = 1, Nome = "Administrador", Administrador = true };
        _atendimento = new Perfil { TenantId = 1, Nome = "Atendimento" };
        _atendimento.Permissoes.Add(new PerfilPermissao { Permissao = "agenda.ver" });
        _db.Perfis.AddRange(_admin, _atendimento);
        await _db.SaveChangesAsync();

        _dono = new Usuario
        {
            TenantId = 1, Nome = "Dono", Email = "dono@empresa.com", PerfilId = _admin.Id,
            SenhaHash = HashSenha.Gerar(Senha),
        };
        _db.Usuarios.Add(_dono);
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private TimeController Time(params string[] permissoes)
    {
        _contexto.TenantId = 1;
        _contexto.UsuarioId = _dono.Id;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Web:BaseUrl"] = "https://painel.exemplo.com/" })
            .Build();
        return new TimeController(_db, new AssinaturaService(_db), config)
        {
            ControllerContext = ContextoDoController.Com(_dono.Id, permissoes.Length == 0 ? new[] { "*" } : permissoes),
        };
    }

    private AuthController Auth(string? produto = null)
    {
        _contexto.TenantId = null; // rotas anônimas: sem tenant no token
        _contexto.UsuarioId = null;
        var opcoes = Options.Create(new OpcoesJwt { Chave = new string('k', 64) });
        var http = new DefaultHttpContext();
        if (produto is not null)
        {
            http.Request.Headers[ContextoMiddleware.CabecalhoProduto] = produto;
        }
        return new AuthController(_db, _contexto, new ServicoDeToken(opcoes), opcoes)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private async Task<(MembroTimeDto Membro, string Token)> ConvidarAsync(long? perfilId = null)
    {
        var criado = await Time().Convidar(
            new ConvidarMembroRequest("Ana Souza", "Ana@Empresa.com", perfilId ?? _atendimento.Id), default);
        var membro = (MembroTimeDto)((ObjectResult)criado.Result!).Value!;
        var token = Uri.UnescapeDataString(membro.UrlConvite!.Split("/convite/")[1]);
        return (membro, token);
    }

    private static T Ok<T>(ActionResult<T> r) => (T)Assert.IsType<OkObjectResult>(r.Result).Value!;

    // ------------------------------------------------------------------- convite

    [Fact]
    public async Task Convidar_devolve_o_link_e_nao_guarda_o_token_em_claro()
    {
        var (membro, token) = await ConvidarAsync();

        Assert.StartsWith("https://painel.exemplo.com/convite/", membro.UrlConvite);
        Assert.True(membro.ConvitePendente);
        Assert.NotNull(membro.ConviteExpiraEm);

        var gravado = await _db.Usuarios.AsNoTracking().FirstAsync(u => u.Id == membro.UsuarioId);
        Assert.NotEqual(token, gravado.TokenConvite);
        Assert.Equal(HashSenha.HashDeToken(token), gravado.TokenConvite);

        // A listagem não mostra o link.
        var obtido = Ok(await Time().Obter(membro.UsuarioId, default));
        Assert.Null(obtido.UrlConvite);
    }

    [Fact]
    public async Task Convite_mostra_quem_e_para_qual_empresa()
    {
        var (_, token) = await ConvidarAsync();

        var convite = Ok(await Auth().Convite(token, default));

        Assert.Equal("Ana Souza", convite.Nome);
        Assert.Equal("ana@empresa.com", convite.Email);
        Assert.Equal("Empresa Um", convite.Empresa);
        Assert.NotNull(convite.ExpiraEm);
    }

    [Fact]
    public async Task Aceitar_define_a_senha_entra_e_gasta_o_token()
    {
        var (membro, token) = await ConvidarAsync();

        var login = Ok(await Auth().AceitarConvite(new AceitarConviteRequest(token, "novaSenha1"), default));

        Assert.Equal(membro.UsuarioId, login.Usuario.UsuarioId);
        Assert.Equal("empresa", login.Tenant.Slug);
        Assert.False(string.IsNullOrWhiteSpace(login.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(login.RefreshToken));

        var usuario = await _db.Usuarios.AsNoTracking().FirstAsync(u => u.Id == membro.UsuarioId);
        Assert.False(usuario.ConvitePendente);
        Assert.True(usuario.Ativo);
        Assert.Null(usuario.TokenConvite);
        Assert.True(HashSenha.Confere("novaSenha1", usuario.SenhaHash));

        // Uso único: o mesmo link não serve de novo, nem para consultar.
        Assert.IsType<NotFoundObjectResult>(
            (await Auth().AceitarConvite(new AceitarConviteRequest(token, "outraSenha2"), default)).Result);
        Assert.IsType<NotFoundObjectResult>((await Auth().Convite(token, default)).Result);

        // E o login normal passa a funcionar com a senha escolhida.
        var normal = await Auth().Login(new LoginRequest("ana@empresa.com", "novaSenha1", "empresa", null), default);
        Assert.IsType<OkObjectResult>(normal.Result);
    }

    [Fact]
    public async Task Convite_vencido_ou_desconhecido_e_recusado()
    {
        var (membro, token) = await ConvidarAsync();
        var usuario = await _db.Usuarios.FirstAsync(u => u.Id == membro.UsuarioId);
        usuario.ConviteExpiraEm = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        Assert.IsType<NotFoundObjectResult>((await Auth().Convite(token, default)).Result);
        Assert.IsType<NotFoundObjectResult>(
            (await Auth().AceitarConvite(new AceitarConviteRequest(token, "novaSenha1"), default)).Result);
        Assert.IsType<NotFoundObjectResult>((await Auth().Convite("nao-existe", default)).Result);
    }

    [Fact]
    public async Task Token_e_comparado_exato()
    {
        var (_, token) = await ConvidarAsync();

        Assert.IsType<NotFoundObjectResult>((await Auth().Convite(token.ToUpperInvariant(), default)).Result);
        Assert.IsType<NotFoundObjectResult>((await Auth().Convite(token[..^1], default)).Result);
    }

    [Fact]
    public async Task Senha_curta_e_recusada_e_o_convite_continua_valendo()
    {
        var (_, token) = await ConvidarAsync();

        var resposta = await Auth().AceitarConvite(new AceitarConviteRequest(token, "curta"), default);

        var erro = Assert.IsType<BadRequestObjectResult>(resposta.Result);
        Assert.Equal("SENHA_FRACA", Assert.IsType<ErroApi>(erro.Value).Code);
        Assert.IsType<OkObjectResult>((await Auth().Convite(token, default)).Result);
    }

    [Fact]
    public async Task Renovar_troca_o_link_e_o_antigo_para_de_valer()
    {
        var (membro, antigo) = await ConvidarAsync();

        var renovado = Ok(await Time().RenovarConvite(membro.UsuarioId, default));
        var novo = Uri.UnescapeDataString(renovado.UrlConvite!.Split("/convite/")[1]);

        Assert.NotEqual(antigo, novo);
        Assert.IsType<NotFoundObjectResult>((await Auth().Convite(antigo, default)).Result);
        Assert.IsType<OkObjectResult>((await Auth().Convite(novo, default)).Result);
    }

    [Fact]
    public async Task Renovar_convite_de_administrador_exige_administrador()
    {
        var (membro, _) = await ConvidarAsync(_admin.Id);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Time("time.ver", "time.convidar").RenovarConvite(membro.UsuarioId, default));

        Assert.Equal("SO_ADMINISTRADOR", erro.Codigo);
    }

    [Fact]
    public async Task Quem_ja_aceitou_nao_tem_convite_para_renovar()
    {
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Time().RenovarConvite(_dono.Id, default));

        Assert.Equal("CONVITE_NAO_PENDENTE", erro.Codigo);
    }

    // --------------------------------------------------------------- sessão irmã

    private async Task<string> EntrarAsync(string? produto = null)
    {
        var login = Ok(await Auth().Login(
            new LoginRequest(_dono.Email, Senha, "empresa", produto), default));
        return login.RefreshToken!;
    }

    [Fact]
    public async Task Sessao_irma_nao_gasta_o_token_de_quem_compartilhou()
    {
        var doApp = await EntrarAsync();

        var irma = Ok(await Auth().SessaoIrma(new SessaoIrmaRequest(doApp, Produtos.PetShopRoute), default));

        Assert.NotEqual(doApp, irma.RefreshToken);
        var emitido = await _db.RefreshTokens.AsNoTracking()
            .FirstAsync(t => t.TokenHash == HashSenha.HashDeToken(irma.RefreshToken!));
        Assert.Equal(Produtos.PetShopRoute, emitido.ProdutoOrigem);

        // O token original continua ativo: o app que o tem renova normalmente...
        var original = await _db.RefreshTokens.AsNoTracking()
            .FirstAsync(t => t.TokenHash == HashSenha.HashDeToken(doApp));
        Assert.Null(original.RevogadoEm);
        Assert.IsType<OkObjectResult>((await Auth().Refresh(new RefreshRequest(doApp, null), default)).Result);

        // ...e o irmão renova o seu sem derrubar ninguém.
        Assert.IsType<OkObjectResult>((await Auth().Refresh(new RefreshRequest(irma.RefreshToken!, null), default)).Result);
    }

    [Fact]
    public async Task Sessao_irma_usa_o_produto_do_cabecalho()
    {
        var doApp = await EntrarAsync();

        var irma = Ok(await Auth(Produtos.PetShopRoute).SessaoIrma(new SessaoIrmaRequest(doApp, null), default));

        var emitido = await _db.RefreshTokens.AsNoTracking()
            .FirstAsync(t => t.TokenHash == HashSenha.HashDeToken(irma.RefreshToken!));
        Assert.Equal(Produtos.PetShopRoute, emitido.ProdutoOrigem);
    }

    [Fact]
    public async Task Sessao_irma_recusa_token_revogado_vencido_ou_desconhecido()
    {
        var revogado = await EntrarAsync();
        await Auth().Refresh(new RefreshRequest(revogado, null), default); // roda: o antigo morre

        var vencido = await EntrarAsync();
        var registro = await _db.RefreshTokens.FirstAsync(t => t.TokenHash == HashSenha.HashDeToken(vencido));
        registro.ExpiraEm = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        foreach (var token in new[] { revogado, vencido, "inventado" })
        {
            var resposta = await Auth().SessaoIrma(new SessaoIrmaRequest(token, null), default);
            var erro = Assert.IsType<UnauthorizedObjectResult>(resposta.Result);
            Assert.Equal("REFRESH_INVALIDO", Assert.IsType<ErroApi>(erro.Value).Code);
        }
    }
}

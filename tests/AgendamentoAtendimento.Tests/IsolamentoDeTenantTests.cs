using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Api.Controllers;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Pacotes;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// Uma empresa não grava referência à outra. O filtro global de tenant esconde o que é
/// da outra na leitura, mas não impede gravar um id dela — estes são os lugares onde isso
/// acontecia, achados pela varredura de isolamento entre dois tenants.
/// </summary>
public class IsolamentoDeTenantTests : IAsyncLifetime
{
    // A requisição é sempre da empresa 2; a empresa 1 é a "outra".
    private readonly ContextoAtual _contexto = new() { TenantId = 2, UsuarioId = 20 };
    private AppDbContext _db = null!;

    private long _servicoDaOutra, _servicoDaMinha, _pessoaDaOutra, _vinculoDaOutra, _vendaDaOutra;
    private long _pacoteDaMinha, _clienteDaMinha;

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"isolamento-{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(opcoes, _contexto);

        // Os dados da outra empresa entram com o filtro desligado, como faria a semente.
        _contexto.IgnorarFiltroDeTenant = true;
        foreach (var tenant in new long[] { 1, 2 })
        {
            var perfil = new Perfil { TenantId = tenant, Nome = "Atendimento" };
            _db.Perfis.Add(perfil);
            await _db.SaveChangesAsync();

            _db.Usuarios.Add(new Usuario
            {
                TenantId = tenant, Nome = $"Pessoa {tenant}", Email = $"p{tenant}@teste.com",
                PerfilId = perfil.Id, Atendente = true,
            });
            _db.ItensCatalogo.Add(new ItemCatalogo
            {
                TenantId = tenant, Nome = $"Serviço {tenant}", Tipo = TipoItem.Servico,
                Preco = 100m, DuracaoMinutos = 30, Ativo = true,
            });
            _db.Clientes.Add(new Cliente { TenantId = tenant, Tipo = TipoCliente.Pessoa, Nome = $"Cliente {tenant}" });
        }
        await _db.SaveChangesAsync();

        _servicoDaOutra = (await _db.ItensCatalogo.FirstAsync(i => i.TenantId == 1)).Id;
        _servicoDaMinha = (await _db.ItensCatalogo.FirstAsync(i => i.TenantId == 2)).Id;
        _pessoaDaOutra = (await _db.Usuarios.FirstAsync(u => u.TenantId == 1)).Id;
        _clienteDaMinha = (await _db.Clientes.FirstAsync(c => c.TenantId == 2)).Id;
        var clienteDaOutra = (await _db.Clientes.FirstAsync(c => c.TenantId == 1)).Id;

        var hoje = new DateOnly(2026, 9, 23);
        Pacote NovoPacote(long tenant) => new()
        {
            TenantId = tenant, Nome = $"Pacote {tenant}", QuantidadePorCliente = 4, PrecoPorCliente = 100m,
            Recorrencia = RecorrenciaDePacote.Mensal, InicioDoCicloAtual = hoje, FimDoCicloAtual = hoje.AddMonths(1),
        };
        var pacoteDaOutra = NovoPacote(1);
        var pacoteDaMinha = NovoPacote(2);
        _db.Pacotes.AddRange(pacoteDaOutra, pacoteDaMinha);
        await _db.SaveChangesAsync();
        _pacoteDaMinha = pacoteDaMinha.Id;

        var vinculo = new PacoteCliente { TenantId = 1, PacoteId = pacoteDaOutra.Id, ClienteId = clienteDaOutra, Ativo = true };
        _db.PacoteClientes.Add(vinculo);
        var venda = new Venda { TenantId = 1, ClienteId = clienteDaOutra };
        _db.Vendas.Add(venda);
        await _db.SaveChangesAsync();
        _vinculoDaOutra = vinculo.Id;
        _vendaDaOutra = venda.Id;

        _contexto.IgnorarFiltroDeTenant = false;
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Modelo_de_pacote_nao_grava_servico_de_outra_empresa()
    {
        var req = new PacoteModeloRequest("Mensal", null, 4, 100m, RecorrenciaDePacote.Mensal, [_servicoDaOutra]);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() => Pacotes().CriarModelo(req, default));

        Assert.Equal("SERVICO_INVALIDO", erro.Codigo);
        _contexto.IgnorarFiltroDeTenant = true;
        Assert.Empty(await _db.PacoteModeloItens.ToListAsync());
        Assert.Empty(await _db.PacoteModelos.ToListAsync());
    }

    [Fact]
    public async Task Pacote_nao_grava_servico_de_outra_empresa()
    {
        var req = new PacoteRequest("Avulso", 4, 100m, RecorrenciaDePacote.Mensal, [_servicoDaMinha, _servicoDaOutra]);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() => Pacotes().Criar(req, default));

        Assert.Equal("SERVICO_INVALIDO", erro.Codigo);
        _contexto.IgnorarFiltroDeTenant = true;
        Assert.Empty(await _db.PacoteItens.ToListAsync());
    }

    [Fact]
    public async Task Cliente_no_pacote_nao_prefere_profissional_de_outra_empresa()
    {
        var req = new EntrarNoPacoteRequest(_clienteDaMinha, null, null, _pessoaDaOutra);

        await Assert.ThrowsAsync<NaoEncontradoException>(() => Pacotes().Entrar(_pacoteDaMinha, req, default));

        _contexto.IgnorarFiltroDeTenant = true;
        Assert.DoesNotContain(await _db.PacoteClientes.ToListAsync(), c => c.ResponsavelPreferidoId == _pessoaDaOutra);
    }

    [Fact]
    public async Task Tirar_do_pacote_um_cliente_de_outra_empresa_e_404_e_nao_204()
    {
        await Assert.ThrowsAsync<NaoEncontradoException>(() => Pacotes().Sair(_vinculoDaOutra, default));
        await Assert.ThrowsAsync<NaoEncontradoException>(() => Pacotes().Propostas(_vinculoDaOutra, null));

        _contexto.IgnorarFiltroDeTenant = true;
        Assert.True((await _db.PacoteClientes.FirstAsync(c => c.Id == _vinculoDaOutra)).Ativo);
    }

    [Fact]
    public async Task Fila_nao_grava_profissional_de_outra_empresa()
    {
        var relogio = RelogioDeTeste.Utc;
        var controller = new ListaDeEsperaController(_db, new ListaDeEsperaService(_db, relogio), relogio)
        {
            ControllerContext = ContextoDoController.Com("*"),
        };
        var req = new NovaEsperaRequest(_clienteDaMinha, _servicoDaMinha, null, _pessoaDaOutra);

        await Assert.ThrowsAsync<NaoEncontradoException>(() => controller.Entrar(req, default));

        _contexto.IgnorarFiltroDeTenant = true;
        Assert.Empty(await _db.ListaDeEspera.ToListAsync());
    }

    [Fact]
    public async Task Cobrancas_da_venda_de_outra_empresa_sao_404_e_nao_lista_vazia()
    {
        var vendas = new VendaService(_db, RelogioDeTeste.Utc);
        var controller = new CobrancasController(_db, new CobrancaService(_db, vendas), vendas)
        {
            ControllerContext = ContextoDoController.Com("*"),
        };

        await Assert.ThrowsAsync<NaoEncontradoException>(() => controller.DaVenda(_vendaDaOutra, default));
    }

    private PacotesController Pacotes()
    {
        var relogio = RelogioDeTeste.Utc;
        var disponibilidade = new DisponibilidadeService(_db, _contexto, relogio);
        return new PacotesController(
            _db, new PacoteAgendaService(_db, disponibilidade, relogio),
            new RecorrenciaDePacotesService(_db, relogio), disponibilidade, relogio)
        {
            ControllerContext = ContextoDoController.Com("*"),
        };
    }
}

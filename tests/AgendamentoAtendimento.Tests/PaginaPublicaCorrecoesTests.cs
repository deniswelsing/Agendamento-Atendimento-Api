using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Api.Controllers;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.MultiTenancy;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// O que a porta aberta fazia de errado com dado de verdade: o combo de duas pessoas que a
/// página oferecia e sempre recusava, o anônimo que trocava o celular de um cliente, o
/// cadastro duplicado por causa de uma maiúscula no e-mail, e o time inteiro saindo nos
/// horários de uma página que não deixa escolher o profissional.
/// </summary>
public class PaginaPublicaCorrecoesTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new();
    private AppDbContext _db = null!;

    // 21/09/2026 é uma segunda-feira; a empresa abre das 8 às 12.
    private static readonly DateOnly Segunda = new(2026, 9, 21);
    private static readonly DateTimeOffset SextaAnterior = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    private long _brunaId;
    private long _caioId;
    private long _xId;
    private long _yId;

    public async Task InitializeAsync()
    {
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"publico2-{Guid.NewGuid()}").Options,
            _contexto);
        _contexto.IgnorarFiltroDeTenant = true;

        _db.Tenants.Add(new Tenant { Id = 1, Slug = "empresa-um", NomeEmpresa = "Empresa Um" });
        _db.Planos.Add(new Plano
        {
            Id = 1, Codigo = "PRO", Nome = "Pro", Ordem = 1, Ativo = true, UsuariosIncluidos = 5,
            PrecoMensalUsd = 99m, Recursos = CatalogoRecursos.Tudo,
        });
        _db.Assinaturas.Add(new Assinatura
        {
            Id = 1, TenantId = 1, PlanoId = 1, Status = StatusAssinatura.Ativa, AssentosContratados = 5,
        });
        _db.PaginasPublicas.Add(new ConfiguracaoPaginaPublica
        {
            TenantId = 1, Ativa = true, Slug = "empresa-um", AntecedenciaMinimaHoras = 2,
            JanelaMaximaDias = 60, LimiteDiarioPorCliente = 5, PermiteEscolherProfissional = false,
        });
        _db.HorariosFuncionamento.Add(new HorarioFuncionamento
        {
            TenantId = 1, DiaDaSemana = DayOfWeek.Monday, Aberto = true,
            Abertura = new TimeOnly(8, 0), Fechamento = new TimeOnly(12, 0), IntervaloSlotMinutos = 30,
        });
        var perfil = new Perfil { TenantId = 1, Nome = "Atendimento" };
        _db.Perfis.Add(perfil);
        await _db.SaveChangesAsync();

        var bruna = new Usuario { TenantId = 1, Nome = "Bruna", Email = "bruna@x.com", PerfilId = perfil.Id };
        var caio = new Usuario { TenantId = 1, Nome = "Caio", Email = "caio@x.com", PerfilId = perfil.Id };
        var x = new ItemCatalogo { TenantId = 1, Nome = "X", Tipo = TipoItem.Servico, Preco = 60m, DuracaoMinutos = 30, VisivelOnline = true };
        var y = new ItemCatalogo { TenantId = 1, Nome = "Y", Tipo = TipoItem.Servico, Preco = 40m, DuracaoMinutos = 30, VisivelOnline = true };
        _db.AddRange(bruna, caio, x, y);
        await _db.SaveChangesAsync();
        (_brunaId, _caioId, _xId, _yId) = (bruna.Id, caio.Id, x.Id, y.Id);

        foreach (var pessoa in new[] { bruna, caio })
        {
            _db.HorariosStaff.Add(new HorarioStaff
            {
                TenantId = 1, UsuarioId = pessoa.Id, DiaDaSemana = DayOfWeek.Monday,
                Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(12, 0), Trabalha = true,
            });
        }
        _db.ExecutoresDeServico.AddRange(
            new ExecutorDeServico { TenantId = 1, ItemCatalogoId = _xId, UsuarioId = _brunaId },
            new ExecutorDeServico { TenantId = 1, ItemCatalogoId = _yId, UsuarioId = _caioId });
        _db.Clientes.Add(new Cliente
        {
            TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Marina", Email = "Marina@Exemplo.com",
            Celular = "11911111111",
        });
        await _db.SaveChangesAsync();

        _contexto.IgnorarFiltroDeTenant = false;
        _contexto.TenantId = null;
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private PaginaPublicaService Servico() => new(
        _db, _contexto, new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc),
        new AssinaturaService(_db), RelogioDeTeste.Utc);

    private static DateTimeOffset As(int hora) =>
        new(Segunda.ToDateTime(new TimeOnly(hora, 0)), TimeSpan.Zero);

    [Fact]
    public async Task Combo_de_duas_pessoas_e_marcado_com_cada_uma_no_seu_servico()
    {
        var servico = Servico();
        var pagina = (await servico.AssumirPorSlugAsync("empresa-um"))!;

        var (resultado, agendamento) = await servico.AgendarAsync(
            pagina, "João Silva", "joao@exemplo.com", "11999998888", new[] { _xId, _yId }, As(9),
            null, null, SextaAnterior);

        Assert.True(resultado.Ok, resultado.Mensagem);
        Assert.Equal(new long?[] { _brunaId, _caioId },
            agendamento!.Itens.OrderBy(i => i.Ordem).Select(i => i.ResponsavelId).ToArray());
    }

    [Fact]
    public async Task Anonimo_nao_troca_o_celular_do_cliente_nem_duplica_o_cadastro()
    {
        var servico = Servico();
        var pagina = (await servico.AssumirPorSlugAsync("empresa-um"))!;

        var (resultado, agendamento) = await servico.AgendarAsync(
            pagina, "Marina", "marina@exemplo.com", "11900000000", new[] { _xId }, As(10),
            null, null, SextaAnterior);

        Assert.True(resultado.Ok, resultado.Mensagem);
        var clientes = await _db.Clientes.AsNoTracking().ToListAsync();
        var marina = Assert.Single(clientes);
        Assert.Equal("11911111111", marina.Celular);
        Assert.Equal(marina.Id, agendamento!.ClienteId);
        Assert.Contains("11900000000", agendamento.Observacoes);
    }

    [Fact]
    public async Task Sem_escolha_de_profissional_os_horarios_nao_levam_o_time()
    {
        var controller = new PublicoController(_db, Servico())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        // O controller usa o relógio de verdade: uma segunda à frente, dentro da janela.
        var dia0 = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var proximaSegunda = dia0.AddDays(((int)DayOfWeek.Monday - (int)dia0.DayOfWeek + 7) % 7);
        var resposta = await controller.Disponibilidade("empresa-um", proximaSegunda, new[] { _xId }, null, default);

        var dia = Assert.IsType<DiaDaAgendaDto>(Assert.IsType<OkObjectResult>(resposta.Result).Value);
        Assert.NotEmpty(dia.Livres);
        Assert.All(dia.Livres, slot =>
        {
            Assert.Null(slot.ResponsavelId);
            Assert.Null(slot.ResponsavelNome);
            Assert.All(slot.Atribuicoes, a => Assert.Empty(a.Candidatos));
        });
        Assert.Equal(0, dia.TotalAgendamentos);
    }

    [Fact]
    public async Task Pedido_pendente_nao_tira_da_fila_de_espera_e_aprovar_tira()
    {
        // A página pede aprovação, e a Marina espera pelo serviço X.
        _contexto.TenantId = 1;
        (await _db.PaginasPublicas.FirstAsync()).ExigeAprovacao = true;
        await _db.SaveChangesAsync();
        var marinaId = (await _db.Clientes.FirstAsync()).Id;
        var fila = new ListaDeEsperaService(_db, RelogioDeTeste.Utc);
        var (espera, _) = await fila.EntrarAsync(marinaId, _xId, null, null, null, default);
        _contexto.TenantId = null;

        var publico = new PublicoController(_db, Servico(), fila: fila)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        // O controller usa o relógio de verdade: uma segunda à frente, dentro da janela.
        var dia0 = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var segunda = dia0.AddDays(((int)DayOfWeek.Monday - (int)dia0.DayOfWeek + 7) % 7);
        var pedido = await publico.Agendar("empresa-um", new NovoAgendamentoPublicoRequest(
            "Marina", "marina@exemplo.com", "11911111111", new[] { _xId },
            new DateTimeOffset(segunda.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero), null, null), default);
        Assert.True(pedido.Result is OkObjectResult, System.Text.Json.JsonSerializer.Serialize((pedido.Result as ObjectResult)?.Value));

        // Pendente não é compromisso: a espera continua — recusado, ela seguiria valendo.
        Assert.Equal(StatusNaEspera.Aguardando,
            (await _db.ListaDeEspera.AsNoTracking().FirstAsync(e => e.Id == espera.Id)).Status);

        _contexto.TenantId = 1;
        var agendamentoId = (await _db.Agendamentos.AsNoTracking().SingleAsync()).Id;
        var pagina = new PaginaOnlineController(
            _db, Servico(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            fila: fila)
        {
            ControllerContext = ContextoDoController.Com("*"),
        };
        await pagina.Aprovar(agendamentoId, default);

        Assert.Equal(StatusNaEspera.Convertido,
            (await _db.ListaDeEspera.AsNoTracking().FirstAsync(e => e.Id == espera.Id)).Status);
    }
}

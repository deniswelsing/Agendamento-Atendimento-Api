using AgendamentoAtendimento.Api.Comum;
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
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// A página pública é a única porta da Api sem token. O que se testa aqui é o que não
/// pode dar errado numa porta aberta: vazar dado de outra empresa, aceitar serviço que
/// ninguém publicou, deixar marcar para daqui a cinco minutos, ou virar fila de spam.
/// </summary>
public class PaginaPublicaTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new();
    private AppDbContext _db = null!;
    private PaginaPublicaService _servico = null!;

    // 21/09/2026 é uma segunda-feira; a empresa abre das 8 às 12.
    private static readonly DateOnly Segunda = new(2026, 9, 21);
    private static readonly DateTimeOffset SextaAnterior =
        new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    private long CorteId { get; set; }
    private long InternoId { get; set; }
    private long BrunaId { get; set; }
    private ConfiguracaoPaginaPublica Pagina { get; set; } = null!;

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"publico-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        _contexto.IgnorarFiltroDeTenant = true;

        _db.Tenants.Add(new Tenant { Id = 1, Slug = "empresa-um", NomeEmpresa = "Empresa Um" });
        _db.Tenants.Add(new Tenant { Id = 2, Slug = "empresa-dois", NomeEmpresa = "Empresa Dois" });

        // Plano que libera a página, assinado pelas duas empresas.
        var plano = new Plano
        {
            Id = 1, Codigo = "PRO", Nome = "Pro", Ordem = 1, Ativo = true,
            UsuariosIncluidos = 5, PrecoMensalUsd = 99m,
            Recursos = CatalogoRecursos.Tudo,
        };
        _db.Planos.Add(plano);
        _db.Assinaturas.Add(new Assinatura
        {
            Id = 1, TenantId = 1, PlanoId = 1, Status = StatusAssinatura.Ativa,
            AssentosContratados = 5, Ciclo = CicloCobranca.Mensal,
        });
        await _db.SaveChangesAsync();

        _db.PaginasPublicas.Add(new ConfiguracaoPaginaPublica
        {
            TenantId = 1, Ativa = true, Slug = "empresa-um",
            AntecedenciaMinimaHoras = 2, JanelaMaximaDias = 60, LimiteDiarioPorCliente = 2,
        });

        _db.HorariosFuncionamento.Add(new HorarioFuncionamento
        {
            TenantId = 1, DiaDaSemana = DayOfWeek.Monday, Aberto = true,
            Abertura = new TimeOnly(8, 0), Fechamento = new TimeOnly(12, 0),
            IntervaloSlotMinutos = 30,
        });

        var perfil = new Perfil { TenantId = 1, Nome = "Atendimento" };
        _db.Perfis.Add(perfil);
        await _db.SaveChangesAsync();

        var bruna = new Usuario
        {
            TenantId = 1, Nome = "Bruna", Email = "bruna@teste.com",
            PerfilId = perfil.Id, Atendente = true,
        };
        _db.Usuarios.Add(bruna);

        var corte = new ItemCatalogo
        {
            TenantId = 1, Nome = "Corte", Tipo = TipoItem.Servico,
            Preco = 60m, DuracaoMinutos = 30, VisivelOnline = true,
        };
        var interno = new ItemCatalogo
        {
            TenantId = 1, Nome = "Retorno de garantia", Tipo = TipoItem.Servico,
            Preco = 0m, DuracaoMinutos = 30, VisivelOnline = false,
        };
        // Serviço de OUTRA empresa, com o mesmo formato. Nada dele pode escapar.
        var deOutraEmpresa = new ItemCatalogo
        {
            TenantId = 2, Nome = "Serviço da concorrente", Tipo = TipoItem.Servico,
            Preco = 999m, DuracaoMinutos = 30, VisivelOnline = true,
        };
        _db.ItensCatalogo.AddRange(corte, interno, deOutraEmpresa);
        await _db.SaveChangesAsync();

        BrunaId = bruna.Id;
        CorteId = corte.Id;
        InternoId = interno.Id;

        _db.HorariosStaff.Add(new HorarioStaff
        {
            TenantId = 1, UsuarioId = BrunaId, DiaDaSemana = DayOfWeek.Monday,
            Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(12, 0), Trabalha = true,
        });
        await _db.SaveChangesAsync();

        _contexto.IgnorarFiltroDeTenant = false;
        _contexto.TenantId = null;

        _servico = new PaginaPublicaService(
            _db, _contexto, new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc),
            new AssinaturaService(_db), RelogioDeTeste.Utc);

        Pagina = (await _servico.AssumirPorSlugAsync("empresa-um"))!;
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private Task<(ResultadoPublico Resultado, Agendamento? Agendamento)> AgendarAsync(
        DateTimeOffset inicio,
        string email = "cliente@teste.com",
        long? item = null,
        string nome = "João da Silva",
        string? telefone = "11999998888") =>
        _servico.AgendarAsync(Pagina, nome, email, telefone,
            new[] { item ?? CorteId }, inicio, null, null, SextaAnterior);

    // ------------------------------------------------------------------ o slug

    [Fact]
    public async Task Slug_resolve_a_empresa_e_assume_o_tenant()
    {
        Assert.NotNull(Pagina);
        Assert.Equal(1, _contexto.TenantId);
    }

    [Fact]
    public async Task Slug_que_nao_existe_nao_assume_tenant_nenhum()
    {
        _contexto.TenantId = null;

        var pagina = await _servico.AssumirPorSlugAsync("nao-existe");

        Assert.Null(pagina);
        Assert.Null(_contexto.TenantId);
    }

    [Fact]
    public async Task Pagina_desligada_responde_como_inexistente()
    {
        // Quem desliga não quer que o endereço antigo continue confirmando que existe.
        _contexto.IgnorarFiltroDeTenant = true;
        var pagina = await _db.PaginasPublicas.FirstAsync();
        pagina.Ativa = false;
        await _db.SaveChangesAsync();
        _contexto.IgnorarFiltroDeTenant = false;
        _contexto.TenantId = null;

        Assert.Null(await _servico.AssumirPorSlugAsync("empresa-um"));
        Assert.Null(_contexto.TenantId);
    }

    [Fact]
    public async Task Empresa_sem_o_recurso_no_plano_nao_tem_pagina()
    {
        // O plano que vale é o da empresa dona da página, não o de quem está chamando —
        // quem chama não tem plano nenhum.
        _contexto.IgnorarFiltroDeTenant = true;
        _db.Assinaturas.RemoveRange(_db.Assinaturas);
        var plano = await _db.Planos.FirstAsync();
        plano.Recursos = CatalogoRecursos.Clientes;
        await _db.SaveChangesAsync();
        _contexto.IgnorarFiltroDeTenant = false;
        _contexto.TenantId = null;

        Assert.Null(await _servico.AssumirPorSlugAsync("empresa-um"));
    }

    [Theory]
    [InlineData("Salão da Ana", "salao-da-ana")]
    [InlineData("  ESPAÇO   Zen  ", "espaco-zen")]
    [InlineData("a//b__c", "a-b-c")]
    [InlineData("---", "")]
    public void Slug_vira_endereco_limpo(string bruto, string esperado) =>
        Assert.Equal(esperado, PaginaPublicaService.NormalizarSlug(bruto));

    [Fact]
    public async Task Slug_de_outra_empresa_nao_esta_livre()
    {
        Assert.False(await _servico.SlugLivreAsync("empresa-um", ignorarTenantId: 2));
        // Mas é livre para quem já é dono dele: renomear para o próprio nome não conflita.
        Assert.True(await _servico.SlugLivreAsync("empresa-um", ignorarTenantId: 1));
    }

    // -------------------------------------------------------------- o que aparece

    [Fact]
    public async Task So_aparece_servico_publicado_desta_empresa()
    {
        var servicos = await _servico.ServicosPublicosAsync();

        Assert.Equal(new[] { "Corte" }, servicos.Select(s => s.Nome));
    }

    [Fact]
    public async Task Servico_interno_nao_pode_ser_agendado_nem_pelo_id()
    {
        // Não basta sumir da lista: quem adivinhar o id também não passa.
        var (resultado, agendamento) = await AgendarAsync(
            new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero), item: InternoId);

        Assert.False(resultado.Ok);
        Assert.Equal(RecusaPublica.ServicoIndisponivel, resultado.Motivo);
        Assert.Null(agendamento);
    }

    [Fact]
    public async Task Disponibilidade_recusa_servico_que_nao_e_publico()
    {
        var (resultado, dia) = await _servico.DisponibilidadeAsync(
            Pagina, Segunda, new[] { InternoId }, null, SextaAnterior);

        Assert.False(resultado.Ok);
        Assert.Null(dia);
    }

    [Fact]
    public async Task Quem_nao_aceitou_o_convite_nao_e_oferecido_ao_cliente()
    {
        // Ela é atendente e está ativa, mas nunca entrou no sistema. Oferecê-la seria
        // prometer atendimento que ninguém vai prestar.
        _contexto.TenantId = 1;
        var perfilId = (await _db.Perfis.FirstAsync()).Id;
        _db.Usuarios.Add(new Usuario
        {
            TenantId = 1, Nome = "Convidada", Email = "convidada@teste.com",
            PerfilId = perfilId, Atendente = true, ConvitePendente = true,
        });
        await _db.SaveChangesAsync();

        var oferecidos = await _db.Usuarios.AsNoTracking()
            .Where(u => u.Ativo && u.Atendente && !u.ConvitePendente)
            .Select(u => u.Nome).ToListAsync();

        Assert.Contains("Bruna", oferecidos);
        Assert.DoesNotContain("Convidada", oferecidos);
    }

    // ------------------------------------------------------------ a janela aberta

    [Fact]
    public async Task Antecedencia_minima_come_os_horarios_perto_demais()
    {
        // Agora são 9h de segunda e a antecedência é de 2h: as 8h, 9h e 10h já foram.
        var agora = new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

        var (resultado, dia) = await _servico.DisponibilidadeAsync(
            Pagina, Segunda, new[] { CorteId }, null, agora);

        Assert.True(resultado.Ok);
        Assert.All(dia!.Livres, s => Assert.True(s.Inicio >= agora.AddHours(2)));
        Assert.DoesNotContain(dia.Livres, s => s.Inicio.Hour < 11);
    }

    [Fact]
    public async Task Marcar_dentro_da_antecedencia_e_recusado()
    {
        var agora = new DateTimeOffset(2026, 9, 21, 7, 30, 0, TimeSpan.Zero);

        var (resultado, _) = await _servico.AgendarAsync(
            Pagina, "João", "joao@teste.com", "11999998888", new[] { CorteId },
            new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero), null, null, agora);

        Assert.False(resultado.Ok);
        Assert.Equal(RecusaPublica.AntecedenciaInsuficiente, resultado.Motivo);
    }

    [Fact]
    public async Task Marcar_depois_da_janela_e_recusado()
    {
        var longe = SextaAnterior.AddDays(Pagina.JanelaMaximaDias + 5);

        var (resultado, _) = await AgendarAsync(longe);

        Assert.False(resultado.Ok);
        Assert.Equal(RecusaPublica.ForaDaJanela, resultado.Motivo);
    }

    // ----------------------------------------------------------------- o pedido

    [Fact]
    public async Task Cliente_marca_e_recebe_um_codigo()
    {
        var (resultado, agendamento) = await AgendarAsync(
            new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero));

        Assert.True(resultado.Ok, resultado.Mensagem);
        Assert.NotNull(agendamento);
        Assert.Equal(OrigemAgendamento.Online, agendamento!.Origem);
        Assert.False(string.IsNullOrWhiteSpace(agendamento.CodigoPublico));
        Assert.Equal(10, agendamento.CodigoPublico!.Length);
        Assert.Equal(BrunaId, agendamento.ResponsavelId);
        Assert.Equal(StatusAgendamento.Agendado, agendamento.Status);
    }

    [Fact]
    public async Task Sem_email_nao_marca()
    {
        var (resultado, _) = await AgendarAsync(
            new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero), email: "");

        Assert.False(resultado.Ok);
        Assert.Equal(RecusaPublica.DadosIncompletos, resultado.Motivo);
    }

    [Fact]
    public async Task Sem_telefone_nao_marca_quando_a_pagina_exige()
    {
        var (resultado, _) = await AgendarAsync(
            new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero), telefone: null);

        Assert.False(resultado.Ok);
        Assert.Equal(RecusaPublica.DadosIncompletos, resultado.Motivo);
    }

    [Fact]
    public async Task O_mesmo_email_nao_vira_cliente_novo()
    {
        // Quem marca pela segunda vez continua sendo a mesma pessoa, com o mesmo histórico.
        await AgendarAsync(new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero));
        await AgendarAsync(new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero));

        _contexto.TenantId = 1;
        var clientes = await _db.Clientes.Where(c => c.Email == "cliente@teste.com").ToListAsync();

        Assert.Single(clientes);
    }

    [Fact]
    public async Task Horario_ja_ocupado_nao_e_vendido_duas_vezes()
    {
        var hora = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        var (primeiro, _) = await AgendarAsync(hora);
        Assert.True(primeiro.Ok);

        var (segundo, _) = await AgendarAsync(hora, email: "outro@teste.com");

        Assert.False(segundo.Ok);
        Assert.Equal(RecusaPublica.HorarioIndisponivel, segundo.Motivo);
    }

    [Fact]
    public async Task Limite_diario_segura_o_abuso_da_porta_aberta()
    {
        // A página deste teste aceita 2 por dia por e-mail.
        Assert.True((await AgendarAsync(new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero)))
            .Resultado.Ok);
        Assert.True((await AgendarAsync(new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero)))
            .Resultado.Ok);

        var (terceiro, _) = await AgendarAsync(
            new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero));

        Assert.False(terceiro.Ok);
        Assert.Equal(RecusaPublica.LimiteDiario, terceiro.Motivo);
    }

    // -------------------------------------------------------------- a aprovação

    [Fact]
    public async Task Com_aprovacao_ligada_o_pedido_nasce_pendente_e_segura_o_horario()
    {
        _contexto.IgnorarFiltroDeTenant = true;
        var config = await _db.PaginasPublicas.FirstAsync();
        config.ExigeAprovacao = true;
        await _db.SaveChangesAsync();
        _contexto.IgnorarFiltroDeTenant = false;
        var pagina = (await _servico.AssumirPorSlugAsync("empresa-um"))!;

        var hora = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        var (resultado, agendamento) = await _servico.AgendarAsync(
            pagina, "João", "joao@teste.com", "11999998888", new[] { CorteId },
            hora, null, null, SextaAnterior);

        Assert.True(resultado.Ok, resultado.Mensagem);
        Assert.Equal(StatusAgendamento.PendenteAprovacao, agendamento!.Status);

        // E o horário já está preso: soltá-lo deixaria dois clientes pedirem o mesmo.
        var (segundo, _) = await _servico.AgendarAsync(
            pagina, "Maria", "maria@teste.com", "11988887777", new[] { CorteId },
            hora, null, null, SextaAnterior);

        Assert.False(segundo.Ok);
        Assert.Equal(RecusaPublica.HorarioIndisponivel, segundo.Motivo);
    }

    // ---------------------------------------------------------------- o código

    [Fact]
    public async Task O_codigo_devolve_o_agendamento_de_quem_o_tem()
    {
        var (_, agendamento) = await AgendarAsync(
            new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero));

        var achado = await _servico.PorCodigoAsync(agendamento!.CodigoPublico!);

        Assert.NotNull(achado);
        Assert.Equal(agendamento.Id, achado!.Id);
        Assert.Equal("Corte", achado.Itens.Single().Nome);
    }

    [Fact]
    public async Task Codigo_errado_nao_devolve_nada()
    {
        await AgendarAsync(new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero));

        Assert.Null(await _servico.PorCodigoAsync("ZZZZZZZZZZ"));
        Assert.Null(await _servico.PorCodigoAsync(""));
    }

    [Fact]
    public async Task Dois_pedidos_nao_recebem_o_mesmo_codigo()
    {
        var (_, um) = await AgendarAsync(new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero));
        var (_, dois) = await AgendarAsync(
            new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero), email: "outro@teste.com");

        Assert.NotEqual(um!.CodigoPublico, dois!.CodigoPublico);
    }

    /// <summary>
    /// Dois serviços marcados pela página acontecem um depois do outro, com a mesma
    /// pessoa. Gravados sem etapa, os dois caíam "ao mesmo tempo": o atendimento ia até
    /// 10:00, mas a Bruna só ficava presa até 09:30 — e outro cliente marcava 09:30 com
    /// ela por cima do primeiro.
    /// </summary>
    [Fact]
    public async Task Varios_servicos_pela_pagina_ficam_em_sequencia_e_seguram_a_pessoa_ate_o_fim()
    {
        var barba = new ItemCatalogo
        {
            TenantId = 1, Nome = "Barba", Tipo = TipoItem.Servico,
            Preco = 40m, DuracaoMinutos = 30, VisivelOnline = true,
        };
        _db.ItensCatalogo.Add(barba);
        await _db.SaveChangesAsync();

        var (primeiro, agendamento) = await _servico.AgendarAsync(
            Pagina, "João da Silva", "joao@teste.com", "11999998888",
            new[] { barba.Id, CorteId },
            new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero), null, null, SextaAnterior);

        Assert.True(primeiro.Ok);
        var janelas = agendamento!.Janelas().ToList();
        // Na ordem pedida, uma depois da outra, com quem responde pelo atendimento.
        Assert.Equal(new[] { "Barba", "Corte" }, janelas.Select(j => j.Item.Nome));
        Assert.Equal(new TimeOnly(9, 30), TimeOnly.FromDateTime(janelas[1].Inicio.UtcDateTime));
        Assert.All(agendamento.Itens, i => Assert.Equal(BrunaId, i.ResponsavelId));

        var (segundo, _) = await AgendarAsync(
            new DateTimeOffset(2026, 9, 21, 9, 30, 0, TimeSpan.Zero), email: "outro@teste.com");

        Assert.False(segundo.Ok);
        Assert.Equal(RecusaPublica.HorarioIndisponivel, segundo.Motivo);
    }

    // ------------------------------------------------- assinatura da empresa parada

    /// <summary>
    /// Com a assinatura da empresa parada, a página não mostra horários nem aceita pedido
    /// novo (503 `PAGINA_INDISPONIVEL` — quem abre é o cliente, não é ele quem paga). Quem
    /// já marcou continua consultando pelo código.
    /// </summary>
    [Fact]
    public async Task Assinatura_parada_fecha_a_pagina_mas_nao_o_comprovante()
    {
        var (marcado, agendamento) = await AgendarAsync(new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero));
        Assert.True(marcado.Ok);

        _contexto.IgnorarFiltroDeTenant = true;
        var assinatura = await _db.Assinaturas.FirstAsync(a => a.TenantId == 1);
        assinatura.Status = StatusAssinatura.Cancelada;
        await _db.SaveChangesAsync();
        _contexto.IgnorarFiltroDeTenant = false;

        var controller = new PublicoController(_db, new PaginaPublicaService(
            _db, _contexto, new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc),
            new AssinaturaService(_db), RelogioDeTeste.Utc))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        AssertIndisponivel((await controller.Info("empresa-um", default)).Result);
        AssertIndisponivel((await controller.Disponibilidade(
            "empresa-um", Segunda, new[] { CorteId }, null, default)).Result);
        AssertIndisponivel((await controller.Agendar("empresa-um", new NovoAgendamentoPublicoRequest(
            "Maria", "maria@teste.com", "11999990000", new[] { CorteId },
            DateTimeOffset.UtcNow.AddDays(3), null, null), default)).Result);

        var comprovante = await controller.Consultar("empresa-um", agendamento!.CodigoPublico!, default);
        Assert.IsType<OkObjectResult>(comprovante.Result);
    }

    [Fact]
    public async Task Periodo_de_graca_mantem_a_pagina_aberta()
    {
        _contexto.IgnorarFiltroDeTenant = true;
        var assinatura = await _db.Assinaturas.FirstAsync(a => a.TenantId == 1);
        assinatura.Status = StatusAssinatura.EmPeriodoDeGraca;
        assinatura.FimPeriodoDeGraca = DateTimeOffset.UtcNow.AddDays(2);
        await _db.SaveChangesAsync();
        _contexto.IgnorarFiltroDeTenant = false;

        var controller = new PublicoController(_db, new PaginaPublicaService(
            _db, _contexto, new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc),
            new AssinaturaService(_db), RelogioDeTeste.Utc));

        Assert.IsType<OkObjectResult>((await controller.Info("empresa-um", default)).Result);
    }

    private static void AssertIndisponivel(ActionResult? resultado)
    {
        var objeto = Assert.IsType<ObjectResult>(resultado);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objeto.StatusCode);
        Assert.Equal("PAGINA_INDISPONIVEL", Assert.IsType<ErroApi>(objeto.Value).Code);
    }
}

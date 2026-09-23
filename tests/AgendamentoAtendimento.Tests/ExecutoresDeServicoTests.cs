using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// Nem todo mundo sabe fazer tudo. Oferecer encaixe com quem não presta o serviço é
/// prometer ao cliente um horário que não existe.
/// </summary>
public class ExecutoresDeServicoTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;
    private DisponibilidadeService _servico = null!;

    // 21/09/2026 é uma segunda-feira.
    private static readonly DateOnly Segunda = new(2026, 9, 21);

    private long BrunaId { get; set; }
    private long CaioId { get; set; }
    private long CorteId { get; set; }
    private long BarbaId { get; set; }

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"executores-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        _servico = new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc);

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
        var caio = new Usuario
        {
            TenantId = 1, Nome = "Caio", Email = "caio@teste.com",
            PerfilId = perfil.Id, Atendente = true,
        };
        _db.Usuarios.AddRange(bruna, caio);
        _db.Clientes.Add(new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Cliente" });

        var corte = new ItemCatalogo
        {
            TenantId = 1, Nome = "Corte", Tipo = TipoItem.Servico,
            Preco = 60m, DuracaoMinutos = 30,
        };
        var barba = new ItemCatalogo
        {
            TenantId = 1, Nome = "Barba", Tipo = TipoItem.Servico,
            Preco = 40m, DuracaoMinutos = 30,
        };
        _db.ItensCatalogo.AddRange(corte, barba);
        await _db.SaveChangesAsync();

        BrunaId = bruna.Id;
        CaioId = caio.Id;
        CorteId = corte.Id;
        BarbaId = barba.Id;

        foreach (var id in new[] { BrunaId, CaioId })
        {
            _db.HorariosStaff.Add(new HorarioStaff
            {
                TenantId = 1, UsuarioId = id, DiaDaSemana = DayOfWeek.Monday,
                Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(12, 0), Trabalha = true,
            });
        }
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private async Task MarcarExecutorAsync(long itemId, params long[] usuarios)
    {
        foreach (var usuarioId in usuarios)
        {
            _db.ExecutoresDeServico.Add(new ExecutorDeServico
            {
                TenantId = 1, ItemCatalogoId = itemId, UsuarioId = usuarioId,
            });
        }
        await _db.SaveChangesAsync();
    }


    /// <summary>
    /// Quem a grade oferece para o primeiro serviço num horário. Agora um encaixe é uma
    /// cadeia com um escolhido por serviço, e os outros que poderiam pegar vêm junto —
    /// é isso que deixa trocar a pessoa na hora de marcar.
    /// </summary>
    private static IEnumerable<long> QuemPodeNoPrimeiro(DiaDaAgenda dia) =>
        dia.Livres.SelectMany(s => s.Atribuicoes[0].Candidatos).Select(c => c.UsuarioId).Distinct();

    private static IEnumerable<long> QuemPodeAs(DiaDaAgenda dia, DateTimeOffset hora) =>
        dia.Livres.Where(s => s.Inicio == hora)
            .SelectMany(s => s.Atribuicoes[0].Candidatos).Select(c => c.UsuarioId).Distinct();

    private Task<DiaDaAgenda> EncaixesAsync(params long[] itens) =>
        _servico.ObterDiaAsync(Segunda, 30, null, default, itens);

    [Fact]
    public async Task Servico_sem_executor_continua_aberto_a_todos()
    {
        // É o padrão, e o que mantém agendável tudo que existia antes desta regra.
        var dia = await EncaixesAsync(CorteId);

        Assert.Contains(BrunaId, QuemPodeNoPrimeiro(dia));
        Assert.Contains(CaioId, QuemPodeNoPrimeiro(dia));
    }

    [Fact]
    public async Task Marcar_alguem_fecha_a_lista_para_os_outros()
    {
        await MarcarExecutorAsync(CorteId, BrunaId);

        var dia = await EncaixesAsync(CorteId);

        Assert.Contains(BrunaId, QuemPodeNoPrimeiro(dia));
        Assert.DoesNotContain(CaioId, QuemPodeNoPrimeiro(dia));
    }

    [Fact]
    public async Task Varias_pessoas_podem_prestar_o_mesmo_servico()
    {
        await MarcarExecutorAsync(CorteId, BrunaId, CaioId);

        var dia = await EncaixesAsync(CorteId);

        Assert.Contains(BrunaId, QuemPodeNoPrimeiro(dia));
        Assert.Contains(CaioId, QuemPodeNoPrimeiro(dia));
    }

    [Fact]
    public async Task Dois_servicos_podem_ser_prestados_por_pessoas_diferentes()
    {
        // Os serviços são sequenciais, então não precisa existir alguém que dê conta dos
        // dois: basta haver quem pegue cada um na sua janela.
        await MarcarExecutorAsync(CorteId, BrunaId);
        await MarcarExecutorAsync(BarbaId, CaioId);

        var dia = await _servico.ObterDiaAsync(Segunda, 0, null, default, new[] { CorteId, BarbaId });

        var encaixe = Assert.Single(dia.Livres, s => s.Inicio.Hour == 8 && s.Inicio.Minute == 0);
        Assert.Equal(2, encaixe.Atribuicoes.Count);
        Assert.Equal(BrunaId, encaixe.Atribuicoes[0].ResponsavelId);
        Assert.Equal(CaioId, encaixe.Atribuicoes[1].ResponsavelId);

        // E as janelas são encadeadas: um serviço começa quando o outro acaba.
        Assert.Equal(encaixe.Atribuicoes[0].Fim, encaixe.Atribuicoes[1].Inicio);
        Assert.Equal(encaixe.Fim, encaixe.Atribuicoes[1].Fim);
    }

    /// <summary>
    /// Com um só candidato não há escolha a fazer: a tela marca e segue. É a regra de
    /// "se só existe um funcionário que atende, ele é escolhido automaticamente".
    /// </summary>
    [Fact]
    public async Task Um_unico_candidato_nao_deixa_escolha()
    {
        await MarcarExecutorAsync(CorteId, BrunaId);

        var dia = await EncaixesAsync(CorteId);
        var encaixe = dia.Livres.First();

        Assert.Equal(1, encaixe.Atribuicoes[0].Alternativas);
        Assert.Equal(BrunaId, encaixe.Atribuicoes[0].ResponsavelId);
    }

    /// <summary>
    /// Com mais de um, a grade devolve todos: é assim que a tela deixa trocar a pessoa
    /// sem perguntar de novo ao servidor.
    /// </summary>
    [Fact]
    public async Task Com_varios_candidatos_a_grade_devolve_todos()
    {
        await MarcarExecutorAsync(CorteId, BrunaId, CaioId);

        var dia = await EncaixesAsync(CorteId);
        var encaixe = dia.Livres.First();

        Assert.Equal(2, encaixe.Atribuicoes[0].Alternativas);
        Assert.Equal(
            new[] { BrunaId, CaioId }.OrderBy(x => x),
            encaixe.Atribuicoes[0].Candidatos.Select(c => c.UsuarioId).OrderBy(x => x));
    }

    /// <summary>
    /// Quem presta só o segundo serviço de um atendimento continua livre durante o
    /// primeiro. Contar a janela inteira como ocupada perderia esse encaixe.
    /// </summary>
    [Fact]
    public async Task Quem_presta_so_o_segundo_servico_fica_livre_no_primeiro()
    {
        await MarcarExecutorAsync(CorteId, BrunaId);
        await MarcarExecutorAsync(BarbaId, CaioId);

        // Bruna faz o Corte das 8h às 8h30; Caio faz a Barba das 8h30 às 9h.
        var agendamento = new Agendamento
        {
            TenantId = 1, ClienteId = 1, ResponsavelId = BrunaId,
            Inicio = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero),
            Fim = new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero),
            Status = StatusAgendamento.Confirmado,
        };
        agendamento.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = CorteId, Nome = "Corte",
            DuracaoMinutos = 30, Ordem = 0, ResponsavelId = BrunaId,
        });
        agendamento.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = BarbaId, Nome = "Barba",
            DuracaoMinutos = 30, Ordem = 1, ResponsavelId = CaioId,
        });
        _db.Agendamentos.Add(agendamento);
        await _db.SaveChangesAsync();

        var oitoHoras = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        var dia = await _servico.ObterDiaAsync(Segunda, 0, null, default, new[] { BarbaId });

        // Caio está comprometido só das 8h30 às 9h, então as 8h continuam dele.
        Assert.Contains(CaioId, QuemPodeAs(dia, oitoHoras));
        Assert.DoesNotContain(
            CaioId,
            QuemPodeAs(dia, new DateTimeOffset(2026, 9, 21, 8, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Servico_restrito_convive_com_servico_aberto()
    {
        // Só o serviço que declarou executores restringe; o outro segue aberto — e cada
        // um pode ir para uma pessoa diferente, porque acontecem em sequência.
        await MarcarExecutorAsync(BarbaId, CaioId);

        var dia = await _servico.ObterDiaAsync(Segunda, 0, null, default, new[] { CorteId, BarbaId });

        Assert.NotEmpty(dia.Livres);
        Assert.All(dia.Livres, s =>
        {
            // O Corte é aberto: os dois podem pegá-lo.
            Assert.Equal(2, s.Atribuicoes[0].Alternativas);
            // A Barba é só do Caio, sempre.
            Assert.Equal(CaioId, s.Atribuicoes[1].ResponsavelId);
            Assert.Equal(1, s.Atribuicoes[1].Alternativas);
        });
    }

    [Fact]
    public async Task Ninguem_marcado_para_o_servico_pedido_deixa_o_dia_sem_encaixe()
    {
        // Um serviço cujos executores todos saíram do time não tem como ser agendado —
        // melhor o dia vazio do que oferecer alguém que não sabe fazer.
        await MarcarExecutorAsync(CorteId, 999);

        var dia = await EncaixesAsync(CorteId);

        Assert.Empty(dia.Livres);
        Assert.True(dia.Aberto);
    }

    [Fact]
    public async Task Quem_esta_ocupado_nao_aparece_mesmo_sabendo_fazer()
    {
        await MarcarExecutorAsync(CorteId, BrunaId, CaioId);

        // Bruna atende das 8 às 9; nesse intervalo só Caio pode ser oferecido.
        _db.Agendamentos.Add(new Agendamento
        {
            TenantId = 1, ClienteId = 1, ResponsavelId = BrunaId,
            Inicio = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero),
            Fim = new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero),
            Status = StatusAgendamento.Confirmado,
        });
        await _db.SaveChangesAsync();

        var dia = await EncaixesAsync(CorteId);
        var oitoHoras = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

        Assert.DoesNotContain(dia.Livres, s => s.Inicio == oitoHoras && s.ResponsavelId == BrunaId);
        Assert.Contains(dia.Livres, s => s.Inicio == oitoHoras && s.ResponsavelId == CaioId);
    }

    /// <summary>
    /// A semana e o mês passam pelo período. Se ele não levasse os itens, a semana
    /// prometeria encaixes que o dia recusa — e o cliente só descobriria ao abrir o dia.
    /// </summary>
    [Fact]
    public async Task O_periodo_filtra_igual_ao_dia()
    {
        await MarcarExecutorAsync(CorteId, BrunaId);

        var dias = await _servico.ObterPeriodoAsync(
            Segunda, Segunda, 30, null, default, new[] { CorteId });
        var semana = Assert.Single(dias);

        Assert.Contains(semana.Livres, s => s.ResponsavelId == BrunaId);
        Assert.DoesNotContain(semana.Livres, s => s.ResponsavelId == CaioId);

        var dia = await EncaixesAsync(CorteId);
        Assert.Equal(dia.Livres.Count, semana.Livres.Count);
    }

    /// <summary>Sem itens, o período segue mostrando o time inteiro.</summary>
    [Fact]
    public async Task O_periodo_sem_itens_mostra_todo_mundo()
    {
        await MarcarExecutorAsync(CorteId, BrunaId);

        var dias = await _servico.ObterPeriodoAsync(Segunda, Segunda, 30, null, default);
        var semana = Assert.Single(dias);

        Assert.Contains(BrunaId, QuemPodeNoPrimeiro(semana));
        Assert.Contains(CaioId, QuemPodeNoPrimeiro(semana));
    }

    [Fact]
    public async Task Filtrar_por_responsavel_ainda_respeita_a_habilidade()
    {
        await MarcarExecutorAsync(CorteId, CaioId);

        // O app pediu a agenda da Bruna, mas ela não presta este serviço.
        var dia = await _servico.ObterDiaAsync(Segunda, 30, BrunaId, default, new[] { CorteId });

        Assert.Empty(dia.Livres);
    }

    [Fact]
    public async Task Agendar_com_quem_nao_presta_o_servico_e_recusado()
    {
        await MarcarExecutorAsync(CorteId, CaioId);

        var livre = await _servico.EstaLivreAsync(
            new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 21, 8, 30, 0, TimeSpan.Zero),
            BrunaId, null, default, new[] { CorteId });

        Assert.False(livre);
    }
}

/// <summary>
/// Da agenda para a venda: quem prestou cada serviço vira quem leva a comissão daquela
/// linha. O mesmo serviço prestado por duas pessoas vira duas linhas — uma para cada.
/// </summary>
public class ComissaoDaAgendaParaAVendaTests
{
    private static Agendamento ComItens(params (long ItemId, long? Quem, int Quantidade)[] itens)
    {
        var agendamento = new Agendamento
        {
            TenantId = 1, ClienteId = 1, ResponsavelId = 9,
            Inicio = DateTimeOffset.UtcNow, Fim = DateTimeOffset.UtcNow.AddHours(1),
        };

        var ordem = 0;
        foreach (var item in itens)
        {
            agendamento.Itens.Add(new AgendamentoItem
            {
                TenantId = 1, ItemCatalogoId = item.ItemId, Nome = "Serviço " + item.ItemId,
                DuracaoMinutos = 30, Quantidade = item.Quantidade, ResponsavelId = item.Quem,
                Ordem = ordem++,
            });
        }

        return agendamento;
    }

    [Fact]
    public void Cada_servico_leva_quem_o_prestou()
    {
        var fila = VendaService.QuemPrestouPorItem(ComItens((1, 2, 1), (2, 3, 1)));

        Assert.Equal(2, fila[1].Peek());
        Assert.Equal(3, fila[2].Peek());
    }

    [Fact]
    public void Servico_sem_pessoa_fica_com_quem_responde_pelo_atendimento()
    {
        var fila = VendaService.QuemPrestouPorItem(ComItens((1, null, 1)));

        Assert.Equal(9, fila[1].Peek());
    }

    [Fact]
    public void O_mesmo_servico_com_duas_pessoas_vira_duas_linhas()
    {
        // A Ana dá um banho, o Bruno dá o outro: uma linha para cada, e não duas
        // unidades no nome da primeira.
        var fila = VendaService.QuemPrestouPorItem(ComItens((1, 2, 1), (1, 3, 1)));
        var partes = VendaService.DividirEntreQuemPrestou(fila[1], 2m);

        Assert.Equal(2, partes.Count);
        Assert.Equal((2L, 1m), (partes[0].Quem, partes[0].Quantidade));
        Assert.Equal((3L, 1m), (partes[1].Quem, partes[1].Quantidade));
    }

    [Fact]
    public void Unidades_seguidas_da_mesma_pessoa_ficam_numa_linha_so()
    {
        var fila = VendaService.QuemPrestouPorItem(ComItens((1, 2, 2)));
        var partes = VendaService.DividirEntreQuemPrestou(fila[1], 2m);

        Assert.Equal((2L, 2m), (Assert.Single(partes).Quem, partes[0].Quantidade));
    }

    [Fact]
    public void O_que_a_agenda_nao_cobre_fica_sem_dono()
    {
        // Vendeu três, o atendimento tinha um: o resto cai no vendedor da venda na hora
        // de somar, em vez de creditar quem prestou o que não prestou.
        var fila = VendaService.QuemPrestouPorItem(ComItens((1, 2, 1)));
        var partes = VendaService.DividirEntreQuemPrestou(fila[1], 3m);

        Assert.Equal(2, partes.Count);
        Assert.Equal((2L, 1m), (partes[0].Quem, partes[0].Quantidade));
        Assert.Null(partes[1].Quem);
        Assert.Equal(2m, partes[1].Quantidade);
    }

    [Fact]
    public void Meia_unidade_fica_com_quem_prestou_a_outra_metade()
    {
        var fila = VendaService.QuemPrestouPorItem(ComItens((1, 2, 1)));
        var partes = VendaService.DividirEntreQuemPrestou(fila[1], 1.5m);

        var parte = Assert.Single(partes);
        Assert.Equal(2L, parte.Quem);
        Assert.Equal(1.5m, parte.Quantidade);
    }

    [Fact]
    public void Sem_atendimento_a_venda_nao_divide_nada()
    {
        Assert.Empty(VendaService.QuemPrestouPorItem(null));
        var parte = Assert.Single(VendaService.DividirEntreQuemPrestou(null, 2m));
        Assert.Null(parte.Quem);
        Assert.Equal(2m, parte.Quantidade);
    }

    [Fact]
    public void Salvar_de_novo_nao_passa_a_sobra_para_quem_ja_tem_a_sua_linha()
    {
        // O atendimento tinha um banho da Ana; a venda cobra três. Salva de novo, a linha
        // da Ana volta com dono e já gastou a vez dela: a sobra continua sem dono.
        var fila = VendaService.QuemPrestouPorItem(ComItens((1, 2, 1)));
        VendaService.DescontarDaFila(fila[1], 2, 1m);
        var partes = VendaService.DividirEntreQuemPrestou(fila[1], 2m);

        var parte = Assert.Single(partes);
        Assert.Null(parte.Quem);
        Assert.Equal(2m, parte.Quantidade);
    }

    [Fact]
    public void Linha_de_outra_pessoa_nao_gasta_a_vez_de_quem_prestou()
    {
        var fila = VendaService.QuemPrestouPorItem(ComItens((1, 2, 1), (1, 3, 1)));
        VendaService.DescontarDaFila(fila[1], 3, 1m);
        var partes = VendaService.DividirEntreQuemPrestou(fila[1], 1m);

        Assert.Equal((2L, 1m), (Assert.Single(partes).Quem, partes[0].Quantidade));
    }

    [Fact]
    public void Linha_de_menos_de_uma_unidade_fica_com_quem_prestou()
    {
        var fila = VendaService.QuemPrestouPorItem(ComItens((1, 2, 1)));
        var partes = VendaService.DividirEntreQuemPrestou(fila[1], 0.5m);

        var parte = Assert.Single(partes);
        Assert.Equal(2L, parte.Quem);
        Assert.Equal(0.5m, parte.Quantidade);
    }

    [Fact]
    public void Desconto_dividido_nunca_fica_negativo_e_soma_o_pedido()
    {
        // Quatro partes e dois centavos: arredondar parte a parte daria 0,01 às três
        // primeiras e -0,01 à última.
        var descontos = VendaService.DividirDesconto(0.02m, new[] { 1m, 1m, 1m, 1m });

        Assert.All(descontos, d => Assert.True(d >= 0m));
        Assert.Equal(0.02m, descontos.Sum());
    }

    [Fact]
    public void Desconto_acompanha_as_unidades_de_cada_parte()
    {
        var descontos = VendaService.DividirDesconto(10m, new[] { 1m, 3m });

        Assert.Equal(new[] { 2.50m, 7.50m }, descontos);
    }
}

/// <summary>
/// A comissão segue quem prestou o serviço, não quem abriu a venda. Com dois
/// funcionários no mesmo atendimento, somar tudo num nome só pagaria a pessoa errada.
/// </summary>
public class ComissaoPorQuemPrestouTests
{
    private static VendaItem Item(string nome, decimal preco, decimal comissao, long? vendedor) =>
        new()
        {
            TenantId = 1, ItemCatalogoId = 1, Nome = nome, Tipo = TipoItem.Servico,
            Quantidade = 1, PrecoUnitario = preco, ComissaoPercentual = comissao,
            VendedorId = vendedor,
        };

    [Fact]
    public void Cada_item_paga_quem_prestou()
    {
        var venda = new Venda { TenantId = 1, ClienteId = 1, VendedorId = 2 };
        venda.Itens.Add(Item("Consultoria", 320m, 10m, vendedor: 2));
        venda.Itens.Add(Item("Revisão", 410m, 8m, vendedor: 3));

        var porPessoa = venda.ComissoesPorVendedor();

        Assert.Equal(2, porPessoa.Count);
        Assert.Equal(32.00m, porPessoa.Single(c => c.VendedorId == 2).Valor);
        Assert.Equal(32.80m, porPessoa.Single(c => c.VendedorId == 3).Valor);
        Assert.Equal(64.80m, venda.TotalComissao);
    }

    [Fact]
    public void Item_sem_vendedor_cai_no_vendedor_da_venda()
    {
        var venda = new Venda { TenantId = 1, ClienteId = 1, VendedorId = 5 };
        venda.Itens.Add(Item("Consultoria", 100m, 10m, vendedor: null));

        var porPessoa = venda.ComissoesPorVendedor();

        Assert.Equal(5, Assert.Single(porPessoa).VendedorId);
        Assert.Equal(10.00m, venda.TotalComissao);
    }

    /// <summary>Sem ninguém em lugar nenhum, a empresa não deve comissão.</summary>
    [Fact]
    public void Venda_sem_vendedor_nenhum_nao_gera_comissao()
    {
        var venda = new Venda { TenantId = 1, ClienteId = 1, VendedorId = null };
        venda.Itens.Add(Item("Consultoria", 100m, 10m, vendedor: null));

        Assert.Empty(venda.ComissoesPorVendedor());
        Assert.Equal(0m, venda.TotalComissao);
    }

    /// <summary>
    /// Metade com dono, metade sem: a empresa paga só o que tem a quem pagar. Contar a
    /// venda inteira pela presença de um vendedor daria comissão por item órfão.
    /// </summary>
    [Fact]
    public void So_o_que_tem_dono_entra_na_conta()
    {
        var venda = new Venda { TenantId = 1, ClienteId = 1, VendedorId = null };
        venda.Itens.Add(Item("Com dono", 200m, 10m, vendedor: 3));
        venda.Itens.Add(Item("Órfão", 200m, 10m, vendedor: null));

        Assert.Equal(20.00m, venda.TotalComissao);
        Assert.Equal(3, Assert.Single(venda.ComissoesPorVendedor()).VendedorId);
    }

    /// <summary>Desconto no item reduz a comissão: ela é sobre o líquido.</summary>
    [Fact]
    public void Desconto_reduz_a_comissao_de_quem_prestou()
    {
        var venda = new Venda { TenantId = 1, ClienteId = 1, VendedorId = null };
        var item = Item("Consultoria", 200m, 10m, vendedor: 4);
        item.DescontoValor = 50m;
        venda.Itens.Add(item);

        Assert.Equal(15.00m, venda.TotalComissao);
    }
}

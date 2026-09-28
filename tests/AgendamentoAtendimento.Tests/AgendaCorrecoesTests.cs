using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Api.Controllers;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Pacotes;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// O que a agenda deixava passar, achado ao usar o painel de ponta a ponta: o atendimento
/// de duas pessoas que a grade oferecia e o gravar recusava, a sessão de pacote que virava
/// cobrança cheia ao ser remarcada, o mesmo cliente em dois lugares, a fila de espera que
/// nunca "virava agendamento" e a troca de responsável que punha alguém em dois lugares.
/// </summary>
public class AgendaCorrecoesTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;

    // Uma segunda-feira bem à frente: a grade não oferece horário no passado.
    private static readonly DateOnly Segunda = new(2030, 9, 23);

    private long _brunaId;
    private long _caioId;
    private long _anaId;
    private long _clienteId;
    private long _outroClienteId;
    private long _xId; // só a Bruna presta
    private long _yId; // só o Caio presta
    private long _livreId; // qualquer um presta

    public async Task InitializeAsync()
    {
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"agenda-{Guid.NewGuid()}").Options,
            _contexto);

        _db.HorariosFuncionamento.Add(new HorarioFuncionamento
        {
            TenantId = 1, DiaDaSemana = DayOfWeek.Monday, Aberto = true,
            Abertura = new TimeOnly(8, 0), Fechamento = new TimeOnly(18, 0), IntervaloSlotMinutos = 30,
        });
        var perfil = new Perfil { TenantId = 1, Nome = "Atendimento" };
        _db.Perfis.Add(perfil);
        await _db.SaveChangesAsync();

        var pessoas = new[] { "Bruna", "Caio", "Ana" }.Select(nome => new Usuario
        {
            TenantId = 1, Nome = nome, Email = $"{nome.ToLowerInvariant()}@x.com", PerfilId = perfil.Id,
        }).ToList();
        var cliente = new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Marina" };
        var outro = new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Otávio" };
        var x = new ItemCatalogo { TenantId = 1, Nome = "X", Tipo = TipoItem.Servico, Preco = 100m, DuracaoMinutos = 30 };
        var y = new ItemCatalogo { TenantId = 1, Nome = "Y", Tipo = TipoItem.Servico, Preco = 80m, DuracaoMinutos = 30 };
        var livre = new ItemCatalogo { TenantId = 1, Nome = "Livre", Tipo = TipoItem.Servico, Preco = 50m, DuracaoMinutos = 30 };
        _db.Usuarios.AddRange(pessoas);
        _db.AddRange(cliente, outro, x, y, livre);
        await _db.SaveChangesAsync();

        (_brunaId, _caioId, _anaId) = (pessoas[0].Id, pessoas[1].Id, pessoas[2].Id);
        (_clienteId, _outroClienteId) = (cliente.Id, outro.Id);
        (_xId, _yId, _livreId) = (x.Id, y.Id, livre.Id);

        foreach (var pessoa in pessoas)
        {
            _db.HorariosStaff.Add(new HorarioStaff
            {
                TenantId = 1, UsuarioId = pessoa.Id, DiaDaSemana = DayOfWeek.Monday,
                Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(18, 0), Trabalha = true,
            });
        }
        _db.ExecutoresDeServico.AddRange(
            new ExecutorDeServico { TenantId = 1, ItemCatalogoId = _xId, UsuarioId = _brunaId },
            new ExecutorDeServico { TenantId = 1, ItemCatalogoId = _yId, UsuarioId = _caioId });
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private static DateTimeOffset As(int hora, int minuto = 0) =>
        new(Segunda.ToDateTime(new TimeOnly(hora, minuto)), TimeSpan.Zero);

    private AgendamentosController Agenda() => new(
        _db, new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc),
        new LembreteService(_db, new EnviadorDeTeste(), RelogioDeTeste.Utc),
        new ListaDeEsperaService(_db, RelogioDeTeste.Utc), RelogioDeTeste.Utc)
    {
        ControllerContext = ContextoDoController.Com("*"),
    };

    private static T Corpo<T>(ActionResult<T> r) => (T)((ObjectResult)r.Result!).Value!;

    private async Task<AgendamentoDto> MarcarAsync(
        long clienteId, int hora, long[] itens, long? responsavel = null, long?[]? porItem = null) =>
        Corpo(await Agenda().Criar(
            new NovoAgendamentoRequest(clienteId, As(hora), itens, responsavel, null, null, porItem), default));

    [Fact]
    public async Task Atendimento_de_duas_pessoas_grava_mesmo_com_o_responsavel_da_primeira_linha()
    {
        // O painel mandava o responsável da primeira linha junto com a escolha por serviço.
        var marcado = await MarcarAsync(_clienteId, 9, new[] { _xId, _yId }, _brunaId,
            new long?[] { _brunaId, _caioId });

        Assert.Equal(new long?[] { _brunaId, _caioId }, marcado.Itens.Select(i => i.ResponsavelId).ToArray());
    }

    [Fact]
    public async Task O_mesmo_cliente_nao_fica_em_dois_lugares_ao_mesmo_tempo()
    {
        await MarcarAsync(_clienteId, 10, new[] { _livreId }, _anaId);

        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => MarcarAsync(_clienteId, 10, new[] { _livreId }, _caioId));
        Assert.Equal("CLIENTE_JA_AGENDADO", recusa.Codigo);

        // Outro cliente, no mesmo horário, com outra pessoa: pode.
        await MarcarAsync(_outroClienteId, 10, new[] { _livreId }, _caioId);
    }

    [Fact]
    public async Task Remarcar_volta_o_confirmado_para_agendado()
    {
        var marcado = await MarcarAsync(_clienteId, 11, new[] { _livreId }, _anaId);
        await Agenda().AlterarStatus(marcado.AgendamentoId,
            new AlterarStatusRequest(StatusAgendamento.Confirmado, null), default);

        var movido = Corpo(await Agenda().Reagendar(marcado.AgendamentoId,
            new NovoAgendamentoRequest(_clienteId, As(14), new[] { _livreId }, _anaId, null, null), default));

        Assert.Equal(StatusAgendamento.Agendado, movido.Status);
    }

    [Fact]
    public async Task Remarcar_nao_reprecifica_o_servico()
    {
        var marcado = await MarcarAsync(_clienteId, 9, new[] { _livreId }, _anaId);
        var item = await _db.ItensCatalogo.FirstAsync(i => i.Id == _livreId);
        item.Preco = 999m; // o catálogo mudou depois de marcar
        await _db.SaveChangesAsync();

        var movido = Corpo(await Agenda().Reagendar(marcado.AgendamentoId,
            new NovoAgendamentoRequest(_clienteId, As(15), new[] { _livreId }, _anaId, null, null), default));

        Assert.Equal(50m, movido.Itens.Single().PrecoUnitario);
    }

    [Fact]
    public async Task Sessao_de_pacote_remarcada_continua_pre_paga_e_dentro_do_ciclo()
    {
        var pacote = new Pacote { TenantId = 1, Nome = "Mensal", QuantidadePorCliente = 4, PrecoPorCliente = 200m };
        _db.Pacotes.Add(pacote);
        await _db.SaveChangesAsync();
        var vinculo = new PacoteCliente { TenantId = 1, PacoteId = pacote.Id, ClienteId = _clienteId };
        _db.PacoteClientes.Add(vinculo);
        await _db.SaveChangesAsync();
        _db.CiclosDePacote.Add(new CicloDoCliente
        {
            TenantId = 1, PacoteClienteId = vinculo.Id, Ciclo = 1,
            Inicio = Segunda.AddDays(-7), Fim = Segunda.AddDays(7), QuantidadeContratada = 4,
        });
        var sessao = new Agendamento
        {
            TenantId = 1, ClienteId = _clienteId, Inicio = As(9), Fim = As(9, 30), ResponsavelId = _anaId,
            PacoteClienteId = vinculo.Id, PacoteCiclo = 1,
        };
        sessao.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = _livreId, Nome = "Livre", DuracaoMinutos = 30, PrecoUnitario = 0m, ResponsavelId = _anaId,
        });
        _db.Agendamentos.Add(sessao);
        await _db.SaveChangesAsync();

        var movida = Corpo(await Agenda().Reagendar(sessao.Id,
            new NovoAgendamentoRequest(_clienteId, As(16), new[] { _livreId }, _anaId, null, null), default));
        Assert.Equal(0m, movida.Itens.Single().PrecoUnitario);

        var foraDoCiclo = await Assert.ThrowsAsync<RegraDeNegocioException>(() => Agenda().Reagendar(sessao.Id,
            new NovoAgendamentoRequest(_clienteId, As(9).AddDays(14), new[] { _livreId }, _anaId, null, null), default));
        Assert.Equal("FORA_DO_CICLO", foraDoCiclo.Codigo);

        var outroServico = await Assert.ThrowsAsync<RegraDeNegocioException>(() => Agenda().Reagendar(sessao.Id,
            new NovoAgendamentoRequest(_clienteId, As(16), new[] { _xId }, _brunaId, null, null), default));
        Assert.Equal("SESSAO_DE_PACOTE", outroServico.Codigo);
    }

    [Fact]
    public async Task Marcar_o_cliente_que_esperava_tira_ele_da_fila()
    {
        var espera = new EntradaListaDeEspera
        {
            TenantId = 1, ClienteId = _clienteId, ItemCatalogoId = _livreId, DataDesejada = Segunda,
            Status = StatusNaEspera.Avisado,
        };
        _db.ListaDeEspera.Add(espera);
        await _db.SaveChangesAsync();

        var marcado = await MarcarAsync(_clienteId, 13, new[] { _livreId }, _anaId);

        var depois = await _db.ListaDeEspera.AsNoTracking().FirstAsync(e => e.Id == espera.Id);
        Assert.Equal(StatusNaEspera.Convertido, depois.Status);
        Assert.Equal(marcado.AgendamentoId, depois.AgendamentoId);
    }

    [Fact]
    public async Task Deixar_sem_responsavel_confere_quem_assume()
    {
        // Atendimento da Ana (dona) com um serviço livre prestado pelo Caio às 09:30.
        var marcado = await MarcarAsync(_clienteId, 9, new[] { _livreId, _livreId }, null,
            new long?[] { _anaId, _caioId });
        // A Ana ocupada às 09:30 com outro cliente.
        await MarcarAsync(_outroClienteId, 9, new[] { _livreId }, null, new long?[] { _caioId });
        var ocupaAna = new Agendamento
        {
            TenantId = 1, ClienteId = _outroClienteId, Inicio = As(9, 30), Fim = As(10), ResponsavelId = _anaId,
            Status = StatusAgendamento.Agendado,
        };
        _db.Agendamentos.Add(ocupaAna);
        await _db.SaveChangesAsync();

        var segundo = marcado.Itens.OrderBy(i => i.Inicio).Last();
        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(() => Agenda().TrocarResponsavelDoItem(
            marcado.AgendamentoId, segundo.AgendamentoItemId, new TrocarResponsavelRequest(null), default));
        Assert.Equal("RESPONSAVEL_INDISPONIVEL", recusa.Codigo);
    }

    [Fact]
    public async Task Servicos_ao_mesmo_tempo_nao_ficam_com_a_mesma_pessoa_na_troca()
    {
        var marcado = Corpo(await Agenda().Criar(new NovoAgendamentoRequest(
            _clienteId, As(15), new[] { _livreId, _livreId }, null, null, null,
            new long?[] { _anaId, _caioId }, new[] { 0, 0 }), default));

        var doCaio = marcado.Itens.Single(i => i.ResponsavelId == _caioId);
        var candidatos = Corpo(await Agenda().CandidatosDoItem(marcado.AgendamentoId, doCaio.AgendamentoItemId, default));
        Assert.DoesNotContain(candidatos, c => c.UsuarioId == _anaId);

        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(() => Agenda().TrocarResponsavelDoItem(
            marcado.AgendamentoId, doCaio.AgendamentoItemId, new TrocarResponsavelRequest(_anaId), default));
        Assert.Equal("SIMULTANEOS_MESMA_PESSOA", recusa.Codigo);
    }

    [Fact]
    public async Task Filtrar_por_atendente_mostra_quem_presta_o_segundo_servico()
    {
        await MarcarAsync(_clienteId, 9, new[] { _xId, _yId }, null, new long?[] { _brunaId, _caioId });

        var doCaio = Corpo(await Agenda().Listar(Segunda, Segunda, _caioId, null, null, default));

        Assert.Single(doCaio);
    }

    [Fact]
    public async Task Cancelar_quem_faltou_ou_ja_faturado_e_recusado()
    {
        var faltou = await MarcarAsync(_clienteId, 8, new[] { _livreId }, _anaId);
        await Agenda().AlterarStatus(faltou.AgendamentoId,
            new AlterarStatusRequest(StatusAgendamento.NaoCompareceu, null), default);
        var transicao = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Agenda().Cancelar(faltou.AgendamentoId, null, default));
        Assert.Equal("TRANSICAO_INVALIDA", transicao.Codigo);

        var faturado = await MarcarAsync(_outroClienteId, 16, new[] { _livreId }, _anaId);
        var venda = new Venda { TenantId = 1, ClienteId = _outroClienteId, AgendamentoId = faturado.AgendamentoId };
        _db.Vendas.Add(venda);
        await _db.SaveChangesAsync();
        var agendamento = await _db.Agendamentos.FirstAsync(a => a.Id == faturado.AgendamentoId);
        agendamento.VendaId = venda.Id;
        await _db.SaveChangesAsync();

        var comVenda = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Agenda().Cancelar(faturado.AgendamentoId, null, default));
        Assert.Equal("ATENDIMENTO_FATURADO", comVenda.Codigo);
    }

    [Fact]
    public async Task Texto_maior_que_a_coluna_volta_como_400_e_nao_grava()
    {
        var longo = new string('a', 1001);
        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(() => Agenda().Criar(
            new NovoAgendamentoRequest(_clienteId, As(9), new[] { _livreId }, _anaId, longo, null), default));
        Assert.Equal("CAMPO_LONGO", recusa.Codigo);
        Assert.Empty(await _db.Agendamentos.ToListAsync());

        var marcado = await MarcarAsync(_clienteId, 10, new[] { _livreId }, _anaId);
        var motivo = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Agenda().Cancelar(marcado.AgendamentoId, new string('m', 501), default));
        Assert.Equal("CAMPO_LONGO", motivo.Codigo);
        Assert.Equal(StatusAgendamento.Agendado,
            (await _db.Agendamentos.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Lista_de_espera_so_aceita_quem_atende_e_presta_o_servico()
    {
        var financeiro = new Usuario
        {
            TenantId = 1, Nome = "Fábio", Email = "fabio@x.com", Atendente = false,
            PerfilId = (await _db.Perfis.FirstAsync()).Id,
        };
        _db.Usuarios.Add(financeiro);
        await _db.SaveChangesAsync();

        var fila = new ListaDeEsperaController(
            _db, new ListaDeEsperaService(_db, RelogioDeTeste.Utc), RelogioDeTeste.Utc)
        {
            ControllerContext = ContextoDoController.Com("*"),
        };

        var naoAtende = await Assert.ThrowsAsync<RegraDeNegocioException>(() => fila.Entrar(
            new NovaEsperaRequest(_clienteId, _livreId, null, financeiro.Id), default));
        Assert.Equal("NAO_ATENDENTE", naoAtende.Codigo);

        // X é só da Bruna: esperar pelo X com o Caio é esperar por uma vaga que não existe.
        var naoPresta = await Assert.ThrowsAsync<RegraDeNegocioException>(() => fila.Entrar(
            new NovaEsperaRequest(_clienteId, _xId, null, _caioId), default));
        Assert.Equal("NAO_PRESTA", naoPresta.Codigo);

        await fila.Entrar(new NovaEsperaRequest(_clienteId, _xId, null, _brunaId), default);
        Assert.Single(await _db.ListaDeEspera.ToListAsync());
    }

    [Fact]
    public async Task A_grade_nao_oferece_o_horario_em_que_o_cliente_ja_esta()
    {
        await MarcarAsync(_clienteId, 10, new[] { _livreId }, _anaId);
        DisponibilidadeService Grade() => new(_db, _contexto, RelogioDeTeste.Utc);

        // Sem cliente, o horário das 10h continua oferecido: o Caio e a Bruna estão livres.
        var semCliente = await Grade().ObterDiaAsync(Segunda, 0, null, default, new[] { _livreId });
        Assert.Contains(semCliente.Livres, s => s.Inicio == As(10));

        // Com a Marina escolhida, ele sai da grade — o gravar o recusaria com
        // CLIENTE_JA_AGENDADO. Os vizinhos continuam.
        var daMarina = await Grade().ObterDiaAsync(
            Segunda, 0, null, default, new[] { _livreId }, clienteId: _clienteId);
        Assert.DoesNotContain(daMarina.Livres, s => s.Inicio < As(10, 30) && s.Fim > As(10));
        Assert.Contains(daMarina.Livres, s => s.Inicio == As(9, 30));
        Assert.Contains(daMarina.Livres, s => s.Inicio == As(10, 30));

        // Outro cliente vê o horário normalmente.
        var doOtavio = await Grade().ObterDiaAsync(
            Segunda, 0, null, default, new[] { _livreId }, clienteId: _outroClienteId);
        Assert.Contains(doOtavio.Livres, s => s.Inicio == As(10));
    }

    [Fact]
    public async Task Aprovar_o_pedido_devolve_quem_presta_cada_servico()
    {
        // Um pedido da página com dois serviços de duas pessoas.
        var pedido = new Agendamento
        {
            TenantId = 1, ClienteId = _clienteId, Inicio = As(9), Fim = As(10),
            Status = StatusAgendamento.PendenteAprovacao, ResponsavelId = _brunaId,
        };
        pedido.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = _xId, Nome = "X", DuracaoMinutos = 30, Ordem = 0,
            ResponsavelId = _brunaId,
        });
        pedido.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = _yId, Nome = "Y", DuracaoMinutos = 30, Ordem = 1,
            ResponsavelId = _caioId,
        });
        _db.Agendamentos.Add(pedido);
        await _db.SaveChangesAsync();
        // Sem nada carregado de antes: é o que a requisição de verdade encontra.
        _db.ChangeTracker.Clear();

        var disponibilidade = new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc);
        var pagina = new PaginaOnlineController(
            _db,
            new PaginaPublicaService(
                _db, _contexto, disponibilidade, new AssinaturaService(_db), RelogioDeTeste.Utc),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
        {
            ControllerContext = ContextoDoController.Com("*"),
        };

        var aprovado = Corpo(await pagina.Aprovar(pedido.Id, default));

        Assert.Equal(StatusAgendamento.Confirmado, aprovado.Status);
        Assert.Equal(new[] { "Bruna", "Caio" },
            aprovado.Itens.OrderBy(i => i.Ordem).Select(i => i.ResponsavelNome).ToArray());
    }

    [Fact]
    public async Task Cancelar_pelo_patch_o_atendimento_faturado_tambem_e_recusado()
    {
        var faturado = await MarcarAsync(_outroClienteId, 15, new[] { _livreId }, _anaId);
        var venda = new Venda { TenantId = 1, ClienteId = _outroClienteId, AgendamentoId = faturado.AgendamentoId };
        _db.Vendas.Add(venda);
        await _db.SaveChangesAsync();
        var agendamento = await _db.Agendamentos.FirstAsync(a => a.Id == faturado.AgendamentoId);
        agendamento.VendaId = venda.Id;
        await _db.SaveChangesAsync();

        // O DELETE já recusava; o PATCH para Cancelado deixava a venda aberta cobrando o
        // que não aconteceu.
        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(() => Agenda().AlterarStatus(
            faturado.AgendamentoId, new AlterarStatusRequest(StatusAgendamento.Cancelado, "desistiu"), default));
        Assert.Equal("ATENDIMENTO_FATURADO", recusa.Codigo);
        Assert.NotEqual(StatusAgendamento.Cancelado,
            (await _db.Agendamentos.AsNoTracking().FirstAsync(a => a.Id == faturado.AgendamentoId)).Status);
    }

    private PacotesController Pacotes()
    {
        var disponibilidade = new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc);
        return new PacotesController(
            _db, new PacoteAgendaService(_db, disponibilidade, RelogioDeTeste.Utc),
            new RecorrenciaDePacotesService(_db, RelogioDeTeste.Utc), disponibilidade, RelogioDeTeste.Utc)
        {
            ControllerContext = ContextoDoController.Com("*"),
        };
    }

    /// <summary>A Marina num pacote do serviço "Livre", com o ciclo aberto em volta da segunda.</summary>
    private async Task<long> PacoteDaMarinaAsync(DayOfWeek? dia = null, TimeOnly? hora = null)
    {
        var pacote = new Pacote { TenantId = 1, Nome = "Mensal", QuantidadePorCliente = 4, PrecoPorCliente = 200m };
        _db.Pacotes.Add(pacote);
        await _db.SaveChangesAsync();
        _db.PacoteItens.Add(new PacoteItem { TenantId = 1, PacoteId = pacote.Id, ItemCatalogoId = _livreId });
        var vinculo = new PacoteCliente
        {
            TenantId = 1, PacoteId = pacote.Id, ClienteId = _clienteId, DiaDaSemana = dia, Hora = hora,
        };
        _db.PacoteClientes.Add(vinculo);
        await _db.SaveChangesAsync();
        _db.CiclosDePacote.Add(new CicloDoCliente
        {
            TenantId = 1, PacoteClienteId = vinculo.Id, Ciclo = 1,
            Inicio = Segunda.AddDays(-7), Fim = Segunda.AddDays(7), QuantidadeContratada = 4,
        });
        await _db.SaveChangesAsync();
        return vinculo.Id;
    }

    [Fact]
    public async Task Sessao_de_pacote_nao_poe_o_cliente_em_dois_lugares()
    {
        // A Marina já está com a Ana às 10h. A agenda recusava outro atendimento dela às
        // 10h, e a sessão do pacote, com o Caio, era gravada.
        await MarcarAsync(_clienteId, 10, new[] { _livreId }, _anaId);
        var vinculo = await PacoteDaMarinaAsync();

        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(() => Pacotes().Marcar(
            vinculo, new MarcarDoPacoteRequest(As(10), _caioId), default));
        Assert.Equal("CLIENTE_JA_AGENDADO", recusa.Codigo);
        Assert.Equal(1, await _db.Agendamentos.CountAsync(a => a.ClienteId == _clienteId));

        // Quando o outro termina, ela está livre.
        var sessao = Corpo(await Pacotes().Marcar(
            vinculo, new MarcarDoPacoteRequest(As(10, 30), _caioId), default));
        Assert.Equal(As(10, 30), sessao.Inicio);
    }

    [Fact]
    public async Task A_proposta_do_pacote_pula_o_horario_em_que_o_cliente_ja_esta()
    {
        // Combinado: segunda às 10h. Ela já tem 10h com a Ana, e a proposta era 10h com
        // outra pessoa — que marcar agora recusa.
        await MarcarAsync(_clienteId, 10, new[] { _livreId }, _anaId);
        var vinculo = await PacoteDaMarinaAsync(DayOfWeek.Monday, new TimeOnly(10, 0));

        var propostas = await new PacoteAgendaService(
                _db, new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc), RelogioDeTeste.Utc)
            .ProporAsync(vinculo, Segunda);

        var daSegunda = propostas.First(p => p.Data == Segunda);
        Assert.True(daSegunda.TemEncaixe);
        Assert.False(daSegunda.Inicio < As(10, 30) && daSegunda.Fim > As(10),
            $"A proposta caiu em cima do atendimento das 10h: {daSegunda.Inicio:HH:mm}.");
    }
}

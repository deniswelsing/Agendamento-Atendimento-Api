using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Api.Controllers;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Pacotes;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// Cadastros, horários e pacotes, nos pontos que o uso de ponta a ponta expôs: comissão de
/// 150% aceita, cotação de um plano acima do limite, o "horário especial" que herdava a
/// pausa, o domingo aberto por exceção sem horário nenhum, o turno desativado que travava a
/// semana, a pausa invertida, e o estorno de pacote que perdia o centavo.
/// </summary>
public class CadastrosHorariosEPacotesTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;

    // Uma segunda e um domingo à frente (a empresa abre de segunda; domingo, não).
    private static readonly DateOnly Segunda = new(2030, 9, 23);
    private static readonly DateOnly Domingo = new(2030, 9, 29);

    private long _anaId;
    private long _servicoId;

    public async Task InitializeAsync()
    {
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"cadastros-{Guid.NewGuid()}").Options,
            _contexto);

        _db.HorariosFuncionamento.AddRange(
            new HorarioFuncionamento
            {
                TenantId = 1, DiaDaSemana = DayOfWeek.Monday, Aberto = true,
                Abertura = new TimeOnly(8, 0), Fechamento = new TimeOnly(18, 0),
                PausaInicio = new TimeOnly(12, 0), PausaFim = new TimeOnly(13, 0), IntervaloSlotMinutos = 60,
            },
            new HorarioFuncionamento { TenantId = 1, DiaDaSemana = DayOfWeek.Sunday, Aberto = false, IntervaloSlotMinutos = 60 });
        var perfil = new Perfil { TenantId = 1, Nome = "Atendimento" };
        _db.Perfis.Add(perfil);
        await _db.SaveChangesAsync();

        var ana = new Usuario { TenantId = 1, Nome = "Ana", Email = "ana@x.com", PerfilId = perfil.Id };
        var servico = new ItemCatalogo { TenantId = 1, Nome = "Corte", Tipo = TipoItem.Servico, Preco = 60m, DuracaoMinutos = 60 };
        _db.AddRange(ana, servico);
        await _db.SaveChangesAsync();
        (_anaId, _servicoId) = (ana.Id, servico.Id);

        _db.HorariosStaff.Add(new HorarioStaff
        {
            TenantId = 1, UsuarioId = _anaId, DiaDaSemana = DayOfWeek.Monday,
            Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(18, 0), Trabalha = true,
        });
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private DisponibilidadeService Disponibilidade() => new(_db, _contexto, RelogioDeTeste.Utc);

    private static string[] Horas(DiaDaAgenda dia) => dia.Livres
        .Select(s => TimeOnly.FromDateTime(s.Inicio.UtcDateTime).ToString("HH:mm")).ToArray();

    // ------------------------------------------------------------------ catálogo

    [Theory]
    [InlineData(150, 0, 0, null, "COMISSAO")]
    [InlineData(10, -5, 0, null, "TAXA")]
    [InlineData(10, 0, -30, null, "CUSTO")]
    [InlineData(10, 0, 0, -7, "ESTOQUE")]
    public async Task Catalogo_recusa_comissao_taxa_custo_e_estoque_fora_da_faixa(
        decimal comissao, decimal taxa, decimal custo, int? estoque, string codigo)
    {
        var catalogo = new CatalogoController(_db) { ControllerContext = ContextoDoController.Com("*") };

        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(() => catalogo.Criar(
            new ItemCatalogoRequest(TipoItem.Produto, "Coisa", null, null, 200m, custo, null, estoque,
                ComissaoPercentual: comissao, TaxaPercentual: taxa), default));

        Assert.Equal(codigo, recusa.Codigo);
    }

    [Fact]
    public async Task Preco_com_tres_casas_e_gravado_e_devolvido_em_centavos()
    {
        var catalogo = new CatalogoController(_db) { ControllerContext = ContextoDoController.Com("*") };

        var criado = (ItemCatalogoDto)((ObjectResult)(await catalogo.Criar(
            new ItemCatalogoRequest(TipoItem.Produto, "Coisa", null, null, 10.555m), default)).Result!).Value!;

        Assert.Equal(10.56m, criado.Preco);
    }

    [Fact]
    public async Task Cotacao_acima_do_limite_do_plano_e_recusada()
    {
        _db.Planos.Add(new Plano { Id = 7, Codigo = "BASIC", Nome = "Basic", UsuariosIncluidos = 1, LimiteUsuarios = 1, Ativo = true });
        await _db.SaveChangesAsync();
        var assinatura = new AssinaturaController(_db, new AssinaturaService(_db),
            new ConfigurationBuilder().Build(), _contexto)
        {
            ControllerContext = ContextoDoController.Com("*"),
        };

        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => assinatura.Cotar(new CotacaoRequest(7, CicloCobranca.Mensal, 8), default));
        Assert.Equal("ACIMA_DO_LIMITE", recusa.Codigo);
    }

    // ------------------------------------------------------------------ horários

    [Fact]
    public async Task Horario_especial_sem_pausa_nao_herda_a_pausa_do_dia()
    {
        _db.ExcecoesHorarioFuncionamento.Add(new ExcecaoHorarioFuncionamento
        {
            TenantId = 1, Data = Segunda, Fechado = false,
            Abertura = new TimeOnly(12, 0), Fechamento = new TimeOnly(16, 0),
        });
        await _db.SaveChangesAsync();

        var dia = await Disponibilidade().ObterDiaAsync(Segunda, 0, null, default, new[] { _servicoId });

        Assert.Equal(new[] { "12:00", "13:00", "14:00", "15:00" }, Horas(dia));
    }

    [Fact]
    public async Task Domingo_aberto_por_excecao_tem_horario_para_o_time()
    {
        _db.ExcecoesHorarioFuncionamento.Add(new ExcecaoHorarioFuncionamento
        {
            TenantId = 1, Data = Domingo, Fechado = false,
            Abertura = new TimeOnly(9, 0), Fechamento = new TimeOnly(12, 0), Motivo = "Mutirão",
        });
        await _db.SaveChangesAsync();

        var dia = await Disponibilidade().ObterDiaAsync(Domingo, 0, null, default, new[] { _servicoId });

        Assert.True(dia.Aberto);
        Assert.Equal(new[] { "09:00", "10:00", "11:00" }, Horas(dia));

        // E a ausência continua dizendo quem não vem.
        _db.ExcecoesHorarioStaff.Add(new ExcecaoHorarioStaff
        {
            TenantId = 1, UsuarioId = _anaId, Data = Domingo, DiaInteiro = true,
        });
        await _db.SaveChangesAsync();
        Assert.Empty((await Disponibilidade().ObterDiaAsync(Domingo, 0, null, default, new[] { _servicoId })).Livres);
    }

    [Fact]
    public async Task Turno_desativado_em_uso_nao_trava_a_semana_da_pessoa()
    {
        var turno = new Turno { TenantId = 1, Nome = "Manhã", Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(12, 0), Ativo = false };
        _db.Turnos.Add(turno);
        await _db.SaveChangesAsync();
        var segunda = await _db.HorariosStaff.FirstAsync(h => h.UsuarioId == _anaId);
        segunda.TurnoId = turno.Id;
        await _db.SaveChangesAsync();

        var horarios = new HorariosController(_db, RelogioDeTeste.Utc) { ControllerContext = ContextoDoController.Com("*") };
        await horarios.SalvarStaff(_anaId, new[]
        {
            new HorarioStaffRequest(_anaId, DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(12, 0), null, null, true, turno.Id),
        }, default);

        // Mas um dia que não estava nele não entra num turno desativado.
        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(() => horarios.SalvarStaff(_anaId, new[]
        {
            new HorarioStaffRequest(_anaId, DayOfWeek.Tuesday, new TimeOnly(8, 0), new TimeOnly(12, 0), null, null, true, turno.Id),
        }, default));
        Assert.Equal("TURNO_INVALIDO", recusa.Codigo);
    }

    [Fact]
    public async Task Pausa_invertida_na_jornada_e_recusada()
    {
        var horarios = new HorariosController(_db, RelogioDeTeste.Utc) { ControllerContext = ContextoDoController.Com("*") };

        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(() => horarios.SalvarStaff(_anaId, new[]
        {
            new HorarioStaffRequest(_anaId, DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(17, 0),
                new TimeOnly(15, 0), new TimeOnly(9, 0)),
        }, default));
        Assert.Equal("PAUSA_INVALIDA", recusa.Codigo);
    }

    // ------------------------------------------------------------------ pacotes

    [Theory]
    [InlineData(3, 100.00)]
    [InlineData(2, 66.67)]
    [InlineData(1, 33.33)]
    public void Estorno_do_pacote_nao_perde_o_centavo(int naoUsadas, decimal esperado)
    {
        var pacote = new Pacote { Nome = "Trio", QuantidadePorCliente = 3, PrecoPorCliente = 100m };
        var ciclo = new CicloDoCliente { QuantidadeContratada = 3, QuantidadeUsada = 3 - naoUsadas };

        ciclo.Encerrar(haProximoCiclo: false, pacote.ValorExatoPorAtendimento, DateTimeOffset.UtcNow);

        Assert.Equal(esperado, ciclo.EstornoValor);
    }

    private PacotesController Pacotes()
    {
        var disponibilidade = Disponibilidade();
        return new PacotesController(
            _db, new PacoteAgendaService(_db, disponibilidade, RelogioDeTeste.Utc),
            new RecorrenciaDePacotesService(_db, RelogioDeTeste.Utc), disponibilidade, RelogioDeTeste.Utc)
        {
            ControllerContext = ContextoDoController.Com("*"),
        };
    }

    [Fact]
    public async Task Pacote_de_preco_negativo_e_recusado()
    {
        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(() => Pacotes().Criar(
            new PacoteRequest("Estranho", 3, -100m, RecorrenciaDePacote.Nenhuma, new[] { _servicoId }), default));
        Assert.Equal("PRECO_INVALIDO", recusa.Codigo);
    }

    [Fact]
    public async Task Varredura_nao_roda_para_o_futuro()
    {
        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Pacotes().Varrer(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30), default));
        Assert.Equal("DATA_FUTURA", recusa.Codigo);
    }

    [Fact]
    public async Task Usadas_conta_o_que_ja_foi_atendido_no_ciclo_aberto()
    {
        var cliente = new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Bia" };
        var pacote = new Pacote { TenantId = 1, Nome = "Mensal", QuantidadePorCliente = 4, PrecoPorCliente = 200m };
        _db.AddRange(cliente, pacote);
        await _db.SaveChangesAsync();
        var vinculo = new PacoteCliente { TenantId = 1, PacoteId = pacote.Id, ClienteId = cliente.Id };
        _db.PacoteClientes.Add(vinculo);
        await _db.SaveChangesAsync();
        _db.CiclosDePacote.Add(new CicloDoCliente
        {
            TenantId = 1, PacoteClienteId = vinculo.Id, Ciclo = 1, Inicio = Segunda.AddDays(-10),
            Fim = Segunda.AddDays(20), QuantidadeContratada = 4,
        });
        _db.Agendamentos.Add(new Agendamento
        {
            TenantId = 1, ClienteId = cliente.Id, Inicio = new DateTimeOffset(Segunda.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero),
            Fim = new DateTimeOffset(Segunda.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero),
            Status = StatusAgendamento.Concluido, PacoteClienteId = vinculo.Id, PacoteCiclo = 1,
        });
        await _db.SaveChangesAsync();

        var lido = (PacoteDto)((ObjectResult)(await Pacotes().Obter(pacote.Id, default)).Result!).Value!;

        var ciclo = lido.Clientes.Single().CicloAtual!;
        Assert.Equal(1, ciclo.QuantidadeUsada);
        Assert.Equal(3, ciclo.Disponivel);
    }
}

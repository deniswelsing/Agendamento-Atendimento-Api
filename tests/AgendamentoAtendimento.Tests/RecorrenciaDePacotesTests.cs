using AgendamentoAtendimento.Api.Jobs;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Pacotes;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// A varredura diária dos pacotes: avisa o que vai vencer e vira o ciclo do que venceu.
/// Uma renovação que chega sem aviso é cobrança surpresa; um ciclo que vira sem acertar
/// o saldo é dinheiro do cliente que evapora.
/// </summary>
public class RecorrenciaDePacotesTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;
    private RecorrenciaDePacotesService _servico = null!;

    private static readonly DateOnly Hoje = new(2026, 9, 21);

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"pacotes-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        _servico = new RecorrenciaDePacotesService(_db);

        _db.Clientes.AddRange(
            new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Ana" },
            new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Bia" });
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    /// <summary>Um pacote com um cliente, o ciclo aberto e o vencimento onde se quiser.</summary>
    private async Task<(Pacote Pacote, PacoteCliente Vinculo, CicloDoCliente Ciclo)> MontarAsync(
        RecorrenciaDePacote recorrencia, DateOnly vence, int quantidade = 4, decimal preco = 200m)
    {
        var pacote = new Pacote
        {
            TenantId = 1, Nome = "Mensal " + quantidade, QuantidadePorCliente = quantidade,
            PrecoPorCliente = preco, Recorrencia = recorrencia,
            InicioDoCicloAtual = vence.AddDays(-29), FimDoCicloAtual = vence,
        };
        _db.Pacotes.Add(pacote);
        await _db.SaveChangesAsync();

        var vinculo = new PacoteCliente
        {
            TenantId = 1, PacoteId = pacote.Id, ClienteId = 1,
            DiaDaSemana = DayOfWeek.Wednesday, Hora = new TimeOnly(14, 0),
        };
        _db.PacoteClientes.Add(vinculo);
        await _db.SaveChangesAsync();

        var ciclo = new CicloDoCliente
        {
            TenantId = 1, PacoteClienteId = vinculo.Id, Ciclo = 1,
            Inicio = pacote.InicioDoCicloAtual, Fim = vence,
            QuantidadeContratada = quantidade,
        };
        _db.CiclosDePacote.Add(ciclo);
        await _db.SaveChangesAsync();

        return (pacote, vinculo, ciclo);
    }

    private async Task MarcarAsync(
        PacoteCliente vinculo, int ciclo, StatusAgendamento status, DateOnly data)
    {
        var inicio = new DateTimeOffset(data.ToDateTime(new TimeOnly(14, 0)), TimeSpan.Zero);
        _db.Agendamentos.Add(new Agendamento
        {
            TenantId = 1, ClienteId = vinculo.ClienteId, Status = status,
            Inicio = inicio, Fim = inicio.AddHours(1),
            PacoteClienteId = vinculo.Id, PacoteCiclo = ciclo,
        });
        await _db.SaveChangesAsync();
    }

    // ----------------------------------------------------------------- avisos
    [Fact]
    public async Task Avisa_o_que_vence_dentro_de_sete_dias()
    {
        await MontarAsync(RecorrenciaDePacote.Mensal, Hoje.AddDays(5));

        var r = await _servico.VarrerAsync(Hoje);

        var aviso = Assert.Single(r.Avisos);
        Assert.Equal(5, aviso.DiasAteVencer);
        Assert.Contains("renova em 5 dias", aviso.Texto);
        Assert.Equal(1, aviso.Clientes);
    }

    [Fact]
    public async Task O_que_vence_hoje_avisa_hoje()
    {
        await MontarAsync(RecorrenciaDePacote.Mensal, Hoje);

        var aviso = Assert.Single((await _servico.VarrerAsync(Hoje)).Avisos);

        Assert.Equal(0, aviso.DiasAteVencer);
        Assert.Contains("renova hoje", aviso.Texto);
    }

    [Fact]
    public async Task O_que_vence_depois_dos_sete_dias_ainda_nao_e_avisado()
    {
        await MontarAsync(RecorrenciaDePacote.Mensal, Hoje.AddDays(8));

        Assert.Empty((await _servico.VarrerAsync(Hoje)).Avisos);
    }

    /// <summary>
    /// Avisar sete dias seguidos é o mesmo que não avisar: vira ruído e ninguém lê.
    /// </summary>
    [Fact]
    public async Task Nao_avisa_duas_vezes_o_mesmo_ciclo()
    {
        await MontarAsync(RecorrenciaDePacote.Mensal, Hoje.AddDays(5));

        Assert.Single((await _servico.VarrerAsync(Hoje)).Avisos);
        Assert.Empty((await _servico.VarrerAsync(Hoje.AddDays(1))).Avisos);
    }

    /// <summary>Pacote sem recorrência também avisa — mas que ACABA, não que renova.</summary>
    [Fact]
    public async Task Pacote_avulso_avisa_que_termina()
    {
        await MontarAsync(RecorrenciaDePacote.Nenhuma, Hoje.AddDays(3));

        var aviso = Assert.Single((await _servico.VarrerAsync(Hoje)).Avisos);

        Assert.Contains("termina", aviso.Texto);
        Assert.Contains("estornado", aviso.Texto);
    }

    // ------------------------------------------------------------ virar ciclo
    /// <summary>
    /// O caso do pedido: quatro sessões, foi a duas, o pacote renova. As duas que
    /// sobraram descem para o ciclo novo em vez de sumirem.
    /// </summary>
    [Fact]
    public async Task O_que_sobrou_desce_como_credito_no_ciclo_novo()
    {
        var (pacote, vinculo, ciclo) = await MontarAsync(
            RecorrenciaDePacote.Mensal, Hoje.AddDays(-1));
        await MarcarAsync(vinculo, 1, StatusAgendamento.Concluido, Hoje.AddDays(-10));
        await MarcarAsync(vinculo, 1, StatusAgendamento.Concluido, Hoje.AddDays(-3));

        var r = await _servico.VarrerAsync(Hoje);

        Assert.Equal(1, r.CiclosEncerrados);
        Assert.Equal(1, r.CiclosAbertos);

        var fechado = await _db.CiclosDePacote.FirstAsync(c => c.Id == ciclo.Id);
        Assert.True(fechado.Encerrado);
        Assert.Equal(2, fechado.QuantidadeUsada);
        Assert.Equal(2, fechado.CreditoCedido);
        Assert.Equal(0, fechado.EstornoQuantidade);

        var novo = await _db.CiclosDePacote.FirstAsync(c => c.Ciclo == 2);
        Assert.Equal(4, novo.QuantidadeContratada);
        Assert.Equal(2, novo.CreditoRecebido);
        Assert.Equal(6, novo.Total);

        var atualizado = await _db.Pacotes.FirstAsync(p => p.Id == pacote.Id);
        Assert.Equal(2, atualizado.CicloAtual);
        Assert.Equal(Hoje, atualizado.InicioDoCicloAtual);
    }

    /// <summary>
    /// Marcado e não atendido não é usado. É justamente o dia em que o cliente não foi,
    /// e o que o crédito existe para devolver.
    /// </summary>
    [Fact]
    public async Task Agendamento_marcado_e_nao_atendido_nao_consome_o_saldo()
    {
        var (_, vinculo, ciclo) = await MontarAsync(
            RecorrenciaDePacote.Mensal, Hoje.AddDays(-1));
        await MarcarAsync(vinculo, 1, StatusAgendamento.Concluido, Hoje.AddDays(-10));
        await MarcarAsync(vinculo, 1, StatusAgendamento.NaoCompareceu, Hoje.AddDays(-5));
        await MarcarAsync(vinculo, 1, StatusAgendamento.Cancelado, Hoje.AddDays(-4));

        await _servico.VarrerAsync(Hoje);

        var fechado = await _db.CiclosDePacote.FirstAsync(c => c.Id == ciclo.Id);
        Assert.Equal(1, fechado.QuantidadeUsada);
        Assert.Equal(3, fechado.CreditoCedido);
    }

    /// <summary>Sem recorrência, o que sobrou volta como dinheiro e o pacote encerra.</summary>
    [Fact]
    public async Task Pacote_avulso_estorna_o_que_nao_foi_usado()
    {
        var (pacote, vinculo, ciclo) = await MontarAsync(
            RecorrenciaDePacote.Nenhuma, Hoje.AddDays(-1), quantidade: 4, preco: 200m);
        await MarcarAsync(vinculo, 1, StatusAgendamento.Concluido, Hoje.AddDays(-9));

        var r = await _servico.VarrerAsync(Hoje);

        var fechado = await _db.CiclosDePacote.FirstAsync(c => c.Id == ciclo.Id);
        Assert.Equal(3, fechado.EstornoQuantidade);
        Assert.Equal(150m, fechado.EstornoValor);
        Assert.Equal(1, r.EstornosGerados);
        Assert.Equal(150m, r.ValorEstornado);

        var atualizado = await _db.Pacotes.FirstAsync(p => p.Id == pacote.Id);
        Assert.Equal(StatusDePacote.Encerrado, atualizado.Status);
        Assert.Empty(await _db.CiclosDePacote.Where(c => c.Ciclo == 2).ToListAsync());
    }

    /// <summary>
    /// O pacote avulso acabou, e o cliente sai dele. Ficar "ativo" num pacote encerrado
    /// o impedia de entrar em qualquer outro (CLIENTE_JA_TEM_PACOTE), para sempre.
    /// </summary>
    [Fact]
    public async Task Pacote_avulso_encerrado_solta_o_cliente()
    {
        var (_, vinculo, _) = await MontarAsync(RecorrenciaDePacote.Nenhuma, Hoje.AddDays(-1));

        await _servico.VarrerAsync(Hoje);

        Assert.False((await _db.PacoteClientes.FirstAsync(c => c.Id == vinculo.Id)).Ativo);
    }

    /// <summary>Com recorrência o cliente segue no pacote: o ciclo seguinte é dele.</summary>
    [Fact]
    public async Task Pacote_recorrente_mantem_o_cliente_na_virada()
    {
        var (_, vinculo, _) = await MontarAsync(RecorrenciaDePacote.Mensal, Hoje.AddDays(-1));

        await _servico.VarrerAsync(Hoje);

        Assert.True((await _db.PacoteClientes.FirstAsync(c => c.Id == vinculo.Id)).Ativo);
    }

    /// <summary>Varrer duas vezes no mesmo dia não vira o ciclo duas vezes.</summary>
    [Fact]
    public async Task Varrer_de_novo_nao_vira_o_ciclo_outra_vez()
    {
        var (pacote, _, _) = await MontarAsync(RecorrenciaDePacote.Mensal, Hoje.AddDays(-1));

        await _servico.VarrerAsync(Hoje);
        var r = await _servico.VarrerAsync(Hoje);

        Assert.Equal(0, r.CiclosEncerrados);
        Assert.Equal(2, (await _db.Pacotes.FirstAsync(p => p.Id == pacote.Id)).CicloAtual);
    }

    // --------------------------------------------------------- sair do pacote
    /// <summary>
    /// O caso do pedido: cancelou hoje, faltavam duas — as duas voltam como dinheiro, e
    /// os horários que estavam presos voltam para a grade.
    /// </summary>
    [Fact]
    public async Task Sair_do_pacote_estorna_o_que_faltava_e_solta_os_horarios()
    {
        var (_, vinculo, ciclo) = await MontarAsync(
            RecorrenciaDePacote.Nenhuma, Hoje.AddDays(20), quantidade: 4, preco: 200m);
        await MarcarAsync(vinculo, 1, StatusAgendamento.Concluido, Hoje.AddDays(-14));
        await MarcarAsync(vinculo, 1, StatusAgendamento.Concluido, Hoje.AddDays(-7));
        await MarcarAsync(vinculo, 1, StatusAgendamento.Agendado, Hoje.AddDays(7));
        await MarcarAsync(vinculo, 1, StatusAgendamento.Agendado, Hoje.AddDays(14));

        var fechado = await _servico.EncerrarVinculoAsync(vinculo.Id, Hoje);

        Assert.NotNull(fechado);
        Assert.Equal(2, fechado!.QuantidadeUsada);
        Assert.Equal(2, fechado.EstornoQuantidade);
        Assert.Equal(100m, fechado.EstornoValor);

        // Os dois que estavam marcados à frente voltaram para a grade.
        var futuros = await _db.Agendamentos
            .Where(a => a.PacoteClienteId == vinculo.Id && a.Inicio > DateTimeOffset.UtcNow)
            .ToListAsync();
        Assert.All(futuros, a => Assert.Equal(StatusAgendamento.Cancelado, a.Status));

        Assert.False((await _db.PacoteClientes.FirstAsync(c => c.Id == vinculo.Id)).Ativo);
        Assert.Equal(ciclo.Id, fechado.Id);
    }

    /// <summary>O que já aconteceu não é desmarcado: aquele atendimento existiu.</summary>
    [Fact]
    public async Task Sair_do_pacote_nao_desmarca_o_que_ja_aconteceu()
    {
        var (_, vinculo, _) = await MontarAsync(
            RecorrenciaDePacote.Nenhuma, Hoje.AddDays(20));
        await MarcarAsync(vinculo, 1, StatusAgendamento.Concluido, Hoje.AddDays(-7));

        await _servico.EncerrarVinculoAsync(vinculo.Id, Hoje);

        var passado = await _db.Agendamentos.FirstAsync(a => a.PacoteClienteId == vinculo.Id);
        Assert.Equal(StatusAgendamento.Concluido, passado.Status);
    }

    [Fact]
    public async Task Sair_duas_vezes_nao_estorna_duas_vezes()
    {
        var (_, vinculo, _) = await MontarAsync(RecorrenciaDePacote.Nenhuma, Hoje.AddDays(20));

        var primeiro = await _servico.EncerrarVinculoAsync(vinculo.Id, Hoje);
        var segundo = await _servico.EncerrarVinculoAsync(vinculo.Id, Hoje);

        Assert.NotNull(primeiro);
        Assert.Null(segundo);
    }
}

/// <summary>
/// A hora em que o job acorda. A varredura precisa estar pronta quando a recepção abre,
/// e o laço nunca pode dormir zero — seria varrer em círculo até o processo cair.
/// </summary>
public class JobDiarioDePacotesTests
{
    private static DateTimeOffset Em(int hora, int minuto) =>
        new(new DateTime(2026, 9, 21, hora, minuto, 0, DateTimeKind.Utc), TimeSpan.Zero);

    [Fact]
    public void Antes_da_hora_espera_ate_hoje()
    {
        Assert.Equal(TimeSpan.FromHours(2), JobDiarioDePacotes.AteAProxima(Em(1, 0)));
    }

    [Fact]
    public void Depois_da_hora_espera_ate_amanha()
    {
        Assert.Equal(TimeSpan.FromHours(23), JobDiarioDePacotes.AteAProxima(Em(4, 0)));
    }

    /// <summary>Dormir zero num laço é o mesmo que não dormir.</summary>
    [Fact]
    public void Nunca_espera_zero()
    {
        for (var hora = 0; hora < 24; hora++)
        {
            Assert.True(
                JobDiarioDePacotes.AteAProxima(Em(hora, 0)) > TimeSpan.Zero,
                $"esperou zero às {hora}h");
        }
    }
}

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

/// <summary>Enviador de teste: guarda o que saiu e pode recusar quando se pede.</summary>
internal sealed class EnviadorDeTeste : IEnviadorDeLembrete
{
    public List<MensagemDeLembrete> Enviadas { get; } = new();
    public string? RecusarCom { get; set; }

    public Task<(bool Entregue, string? Erro)> EnviarAsync(
        MensagemDeLembrete mensagem, CancellationToken ct)
    {
        if (RecusarCom is { } erro)
        {
            return Task.FromResult((false, (string?)erro));
        }

        Enviadas.Add(mensagem);
        return Task.FromResult((true, (string?)null));
    }
}

/// <summary>
/// A fila de avisos. O ponto dela é ser registro, não cálculo: é o que deixa responder
/// "este cliente foi avisado?" e o que impede mandar o lembrete de uma hora que o
/// agendamento já não tem mais.
/// </summary>
public class LembreteServiceTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private readonly EnviadorDeTeste _enviador = new();
    private AppDbContext _db = null!;
    private LembreteService _servico = null!;

    private static readonly DateTimeOffset Agora =
        new(new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero);

    private long ClienteId { get; set; }
    private long SemEmailId { get; set; }
    private long RecusaId { get; set; }

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"lembretes-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        _servico = new LembreteService(_db, _enviador);

        var cliente = new Cliente
        {
            TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Ana",
            Email = "ana@teste.com", AceitaEmail = true,
        };
        var semEmail = new Cliente
        {
            TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Bruno", Email = null,
        };
        var recusa = new Cliente
        {
            TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Célia",
            Email = "celia@teste.com", AceitaEmail = false,
        };
        _db.Clientes.AddRange(cliente, semEmail, recusa);

        _db.ConfiguracoesDeLembrete.Add(new ConfiguracaoDeLembrete
        {
            TenantId = 1, Ativo = true, HorasDeAntecedencia = 24,
            AvisarAoMarcar = true, PedirConfirmacao = true,
        });
        await _db.SaveChangesAsync();

        ClienteId = cliente.Id;
        SemEmailId = semEmail.Id;
        RecusaId = recusa.Id;
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private async Task<long> MarcarAsync(long clienteId, DateTimeOffset inicio)
    {
        var agendamento = new Agendamento
        {
            TenantId = 1, ClienteId = clienteId, Inicio = inicio, Fim = inicio.AddMinutes(30),
            Status = StatusAgendamento.Agendado,
        };
        _db.Agendamentos.Add(agendamento);
        await _db.SaveChangesAsync();
        await _servico.ReprogramarAsync(agendamento.Id, Agora, default);
        return agendamento.Id;
    }

    private Task<List<LembreteDeAgendamento>> FilaAsync(long agendamentoId) =>
        _db.Lembretes.Where(l => l.AgendamentoId == agendamentoId).ToListAsync();

    [Fact]
    public async Task Marcar_enfileira_o_aviso_de_agora_e_o_lembrete_de_antes()
    {
        var id = await MarcarAsync(ClienteId, Agora.AddDays(3));
        var fila = await FilaAsync(id);

        Assert.Equal(2, fila.Count);
        Assert.Contains(fila, l => l.Tipo == TipoDeLembrete.Confirmacao && l.QuandoEnviar == Agora);
        Assert.Contains(fila, l => l.Tipo == TipoDeLembrete.Lembrete
            && l.QuandoEnviar == Agora.AddDays(3).AddHours(-24));
        Assert.All(fila, l => Assert.Equal("ana@teste.com", l.Destino));
    }

    [Fact]
    public async Task Marcar_para_daqui_a_pouco_nao_cria_lembrete_no_passado()
    {
        // Atendimento em 2h com lembrete de 24h: o lembrete seria ontem. Ele não existe —
        // criar um vencido só encheria a fila de coisa que nunca sai.
        var id = await MarcarAsync(ClienteId, Agora.AddHours(2));
        var fila = await FilaAsync(id);

        Assert.Single(fila);
        Assert.Equal(TipoDeLembrete.Confirmacao, fila[0].Tipo);
    }

    [Fact]
    public async Task Cliente_sem_email_nao_entra_na_fila()
    {
        var id = await MarcarAsync(SemEmailId, Agora.AddDays(3));
        Assert.Empty(await FilaAsync(id));
    }

    [Fact]
    public async Task Cliente_que_pediu_para_nao_receber_nao_entra_na_fila()
    {
        // A configuração da empresa não é permissão para escrever a quem não quer.
        var id = await MarcarAsync(RecusaId, Agora.AddDays(3));
        Assert.Empty(await FilaAsync(id));
    }

    [Fact]
    public async Task Configuracao_desligada_nao_enfileira_nada()
    {
        var config = await _db.ConfiguracoesDeLembrete.FirstAsync();
        config.Ativo = false;
        await _db.SaveChangesAsync();

        var id = await MarcarAsync(ClienteId, Agora.AddDays(3));
        Assert.Empty(await FilaAsync(id));
    }

    [Fact]
    public async Task Remarcar_mata_o_lembrete_da_hora_antiga()
    {
        var id = await MarcarAsync(ClienteId, Agora.AddDays(3));
        var antigo = (await FilaAsync(id)).First(l => l.Tipo == TipoDeLembrete.Lembrete);

        var agendamento = await _db.Agendamentos.FirstAsync(a => a.Id == id);
        agendamento.Inicio = Agora.AddDays(5);
        agendamento.Fim = agendamento.Inicio.AddMinutes(30);
        await _db.SaveChangesAsync();
        await _servico.ReprogramarAsync(id, Agora, default);

        var fila = await FilaAsync(id);
        Assert.Equal(StatusDeLembrete.Cancelado,
            fila.First(l => l.Id == antigo.Id).Status);

        var pendentes = fila.Where(l => l.Status == StatusDeLembrete.Pendente).ToList();
        Assert.Equal(2, pendentes.Count);
        Assert.Contains(pendentes, l => l.Tipo == TipoDeLembrete.Lembrete
            && l.QuandoEnviar == Agora.AddDays(5).AddHours(-24));
    }

    [Fact]
    public async Task Cancelar_o_agendamento_esvazia_a_fila_dele()
    {
        var id = await MarcarAsync(ClienteId, Agora.AddDays(3));
        await _servico.CancelarPendentesAsync(id, default);

        Assert.All(await FilaAsync(id),
            l => Assert.Equal(StatusDeLembrete.Cancelado, l.Status));
    }

    [Fact]
    public async Task Despachar_manda_o_que_venceu_e_deixa_o_resto_na_fila()
    {
        var id = await MarcarAsync(ClienteId, Agora.AddDays(3));

        var (enviados, falharam, expirados) = await _servico.DespacharAsync(Agora, default);

        Assert.Equal(1, enviados);
        Assert.Equal(0, falharam);
        Assert.Equal(0, expirados);

        var fila = await FilaAsync(id);
        Assert.Equal(StatusDeLembrete.Enviado,
            fila.First(l => l.Tipo == TipoDeLembrete.Confirmacao).Status);
        // O lembrete de daqui a dois dias continua esperando a hora dele.
        Assert.Equal(StatusDeLembrete.Pendente,
            fila.First(l => l.Tipo == TipoDeLembrete.Lembrete).Status);
    }

    [Fact]
    public async Task Despachar_duas_vezes_nao_manda_o_mesmo_aviso_de_novo()
    {
        await MarcarAsync(ClienteId, Agora.AddDays(3));
        await _servico.DespacharAsync(Agora, default);
        var (enviados, _, _) = await _servico.DespacharAsync(Agora, default);

        Assert.Equal(0, enviados);
        Assert.Single(_enviador.Enviadas);
    }

    [Fact]
    public async Task O_aviso_traz_o_link_de_confirmar_e_o_agendamento_ganha_codigo()
    {
        var id = await MarcarAsync(ClienteId, Agora.AddDays(3));
        await _servico.DespacharAsync(Agora, default);

        var agendamento = await _db.Agendamentos.FirstAsync(a => a.Id == id);
        Assert.False(string.IsNullOrWhiteSpace(agendamento.CodigoPublico));

        var mensagem = Assert.Single(_enviador.Enviadas);
        Assert.Contains(agendamento.CodigoPublico!, mensagem.LinkDeConfirmacao);
    }

    [Fact]
    public async Task Sem_pedir_confirmacao_o_aviso_nao_traz_link()
    {
        var config = await _db.ConfiguracoesDeLembrete.FirstAsync();
        config.PedirConfirmacao = false;
        await _db.SaveChangesAsync();

        await MarcarAsync(ClienteId, Agora.AddDays(3));
        await _servico.DespacharAsync(Agora, default);

        Assert.Null(Assert.Single(_enviador.Enviadas).LinkDeConfirmacao);
    }

    [Fact]
    public async Task Aviso_muito_atrasado_morre_em_vez_de_sair_fora_de_hora()
    {
        // Um lembrete de "amanhã às 9h" entregue depois das 9h não é lembrete, é confusão.
        var id = await MarcarAsync(ClienteId, Agora.AddDays(3));

        var (enviados, _, expirados) = await _servico.DespacharAsync(Agora.AddHours(5), default);

        Assert.Equal(0, enviados);
        Assert.Equal(1, expirados);
        Assert.Empty(_enviador.Enviadas);

        var confirmacao = (await FilaAsync(id)).First(l => l.Tipo == TipoDeLembrete.Confirmacao);
        Assert.Equal(StatusDeLembrete.Cancelado, confirmacao.Status);
        Assert.NotNull(confirmacao.Erro);
    }

    [Fact]
    public async Task Canal_que_recusa_deixa_a_falha_registrada()
    {
        var id = await MarcarAsync(ClienteId, Agora.AddDays(3));
        _enviador.RecusarCom = "Caixa de entrada cheia.";

        var (enviados, falharam, _) = await _servico.DespacharAsync(Agora, default);

        Assert.Equal(0, enviados);
        Assert.Equal(1, falharam);

        var confirmacao = (await FilaAsync(id)).First(l => l.Tipo == TipoDeLembrete.Confirmacao);
        Assert.Equal(StatusDeLembrete.Falhou, confirmacao.Status);
        Assert.Equal("Caixa de entrada cheia.", confirmacao.Erro);
        Assert.Equal(1, confirmacao.Tentativas);
    }

    [Fact]
    public async Task Agendamento_cancelado_entre_a_fila_e_a_varredura_nao_recebe_aviso()
    {
        var id = await MarcarAsync(ClienteId, Agora.AddDays(3));

        var agendamento = await _db.Agendamentos.FirstAsync(a => a.Id == id);
        agendamento.Status = StatusAgendamento.Cancelado;
        await _db.SaveChangesAsync();

        var (enviados, _, _) = await _servico.DespacharAsync(Agora, default);

        Assert.Equal(0, enviados);
        Assert.Empty(_enviador.Enviadas);
    }
}

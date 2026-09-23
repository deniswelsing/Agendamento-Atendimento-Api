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
/// Dois serviços ao mesmo tempo, cada um com a sua pessoa: o treinamento com uma e a
/// revisão de contrato com a outra, na mesma hora. É a empresa que pede — e muda a conta
/// inteira, porque o atendimento passa a durar o serviço mais longo, e não a soma.
/// </summary>
public class ServicosSimultaneosTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;
    private DisponibilidadeService _servico = null!;

    // 21/09/2026 é uma segunda-feira.
    private static readonly DateOnly Segunda = new(2026, 9, 21);

    private long AnaId { get; set; }
    private long BrunoId { get; set; }
    private long TreinamentoId { get; set; }
    private long RevisaoId { get; set; }
    private long ClienteId { get; set; }

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"simultaneos-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);
        _servico = new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc);

        _db.HorariosFuncionamento.Add(new HorarioFuncionamento
        {
            TenantId = 1, DiaDaSemana = DayOfWeek.Monday, Aberto = true,
            Abertura = new TimeOnly(9, 0), Fechamento = new TimeOnly(12, 0),
            IntervaloSlotMinutos = 60,
        });

        var perfil = new Perfil { TenantId = 1, Nome = "Atendimento" };
        _db.Perfis.Add(perfil);
        await _db.SaveChangesAsync();

        var pessoas = new[] { "Ana", "Bruno" }.Select(nome => new Usuario
        {
            TenantId = 1, Nome = nome, Email = $"{nome.ToLowerInvariant()}@teste.com",
            PerfilId = perfil.Id, Atendente = true,
        }).ToList();
        _db.Usuarios.AddRange(pessoas);

        var cliente = new Cliente { TenantId = 1, Tipo = TipoCliente.Empresa, Nome = "Empresa" };
        _db.Clientes.Add(cliente);

        // Um de 60 e um de 30: em sequência são 90 minutos; ao mesmo tempo, 60.
        var treinamento = new ItemCatalogo
        {
            TenantId = 1, Nome = "Treinamento de equipe", Tipo = TipoItem.Servico,
            Preco = 500m, DuracaoMinutos = 60,
        };
        var revisao = new ItemCatalogo
        {
            TenantId = 1, Nome = "Revisão de contrato", Tipo = TipoItem.Servico,
            Preco = 300m, DuracaoMinutos = 30,
        };
        _db.ItensCatalogo.AddRange(treinamento, revisao);
        await _db.SaveChangesAsync();

        AnaId = pessoas[0].Id;
        BrunoId = pessoas[1].Id;
        TreinamentoId = treinamento.Id;
        RevisaoId = revisao.Id;
        ClienteId = cliente.Id;

        foreach (var pessoa in pessoas)
        {
            _db.HorariosStaff.Add(new HorarioStaff
            {
                TenantId = 1, UsuarioId = pessoa.Id, DiaDaSemana = DayOfWeek.Monday,
                Inicio = new TimeOnly(8, 0), Fim = new TimeOnly(18, 0), Trabalha = true,
            });
        }
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private long[] Servicos => new[] { TreinamentoId, RevisaoId };

    private Task<DiaDaAgenda> DiaAsync(int[]? etapas = null, IReadOnlyList<long?>? quem = null) =>
        _servico.ObterDiaAsync(Segunda, 0, null, default, Servicos, null, quem, etapas);

    [Fact]
    public async Task Em_sequencia_o_atendimento_dura_a_soma()
    {
        var dia = await DiaAsync();

        var slot = dia.Livres.First();
        Assert.Equal(90, (slot.Fim - slot.Inicio).TotalMinutes);
        // Um começa quando o outro acaba.
        Assert.Equal(slot.Atribuicoes[0].Fim, slot.Atribuicoes[1].Inicio);
    }

    [Fact]
    public async Task Ao_mesmo_tempo_o_atendimento_dura_o_servico_mais_longo()
    {
        var dia = await DiaAsync(new[] { 0, 0 });

        var slot = dia.Livres.First();
        Assert.Equal(60, (slot.Fim - slot.Inicio).TotalMinutes);
        // Os dois começam juntos; o mais curto acaba antes, e o atendimento segue.
        Assert.Equal(slot.Atribuicoes[0].Inicio, slot.Atribuicoes[1].Inicio);
        Assert.Equal(30, (slot.Atribuicoes[1].Fim - slot.Atribuicoes[1].Inicio).TotalMinutes);
    }

    [Fact]
    public async Task Ao_mesmo_tempo_sao_pessoas_diferentes()
    {
        var dia = await DiaAsync(new[] { 0, 0 });

        Assert.NotEmpty(dia.Livres);
        Assert.All(dia.Livres, slot =>
            Assert.NotEqual(slot.Atribuicoes[0].ResponsavelId, slot.Atribuicoes[1].ResponsavelId));
    }

    [Fact]
    public async Task Com_uma_pessoa_so_no_time_nao_ha_como_atender_ao_mesmo_tempo()
    {
        var bruno = await _db.Usuarios.FirstAsync(u => u.Id == BrunoId);
        bruno.Atendente = false;
        await _db.SaveChangesAsync();

        // Em sequência a Ana faz os dois; ao mesmo tempo, ninguém faz.
        Assert.NotEmpty((await DiaAsync()).Livres);

        var dia = await DiaAsync(new[] { 0, 0 });
        Assert.Empty(dia.Livres);
        Assert.NotNull(dia.MotivoSemEncaixe);
    }

    [Fact]
    public async Task Escolher_quem_faz_cada_um_vale_tambem_ao_mesmo_tempo()
    {
        var dia = await DiaAsync(new[] { 0, 0 }, new long?[] { BrunoId, AnaId });

        Assert.NotEmpty(dia.Livres);
        Assert.All(dia.Livres, slot =>
        {
            Assert.Equal(BrunoId, slot.Atribuicoes[0].ResponsavelId);
            Assert.Equal(AnaId, slot.Atribuicoes[1].ResponsavelId);
        });
    }

    [Fact]
    public async Task A_mesma_pessoa_faz_os_dois_servicos_um_depois_do_outro()
    {
        // O que a flag decide é só se os serviços acontecem juntos. Um atendimento com
        // dois serviços da mesma pessoa continua valendo: ela faz um e depois o outro.
        var dia = await DiaAsync(quem: new long?[] { AnaId, AnaId });

        Assert.NotEmpty(dia.Livres);
        Assert.All(dia.Livres, slot =>
        {
            Assert.Equal(AnaId, slot.Atribuicoes[0].ResponsavelId);
            Assert.Equal(AnaId, slot.Atribuicoes[1].ResponsavelId);
            Assert.Equal(slot.Atribuicoes[0].Fim, slot.Atribuicoes[1].Inicio);
        });
    }

    [Fact]
    public async Task A_mesma_pessoa_nos_dois_ao_mesmo_tempo_nao_tem_encaixe()
    {
        // Ela estaria em dois lugares na mesma hora. O POST recusa com
        // SIMULTANEOS_MESMA_PESSOA; a grade simplesmente não oferece.
        var dia = await DiaAsync(new[] { 0, 0 }, new long?[] { AnaId, AnaId });

        Assert.Empty(dia.Livres);
    }

    [Fact]
    public void As_janelas_do_agendamento_seguem_a_etapa()
    {
        var inicio = new DateTimeOffset(Segunda.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
        var agendamento = new Agendamento
        {
            TenantId = 1, ClienteId = ClienteId, Inicio = inicio, Fim = inicio.AddHours(1),
        };
        agendamento.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = TreinamentoId, Nome = "Treinamento de equipe",
            DuracaoMinutos = 60, Ordem = 0, ResponsavelId = AnaId,
        });
        agendamento.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = RevisaoId, Nome = "Revisão de contrato",
            DuracaoMinutos = 30, Ordem = 0, ResponsavelId = BrunoId,
        });

        var janelas = agendamento.Janelas().ToList();

        Assert.True(agendamento.TemServicosSimultaneos);
        Assert.Equal(inicio, janelas[0].Inicio);
        Assert.Equal(inicio, janelas[1].Inicio);
        Assert.Equal(inicio.AddMinutes(60), janelas[0].Fim);
        Assert.Equal(inicio.AddMinutes(30), janelas[1].Fim);
    }

    [Fact]
    public void Ocupacao_simultanea_prende_as_duas_pessoas_na_mesma_hora()
    {
        var inicio = new DateTimeOffset(Segunda.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
        var agendamento = new Agendamento
        {
            TenantId = 1, ClienteId = ClienteId, Inicio = inicio, Fim = inicio.AddHours(1),
        };
        agendamento.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = TreinamentoId, Nome = "Treinamento",
            DuracaoMinutos = 60, Ordem = 0, ResponsavelId = AnaId,
        });
        agendamento.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = RevisaoId, Nome = "Revisão",
            DuracaoMinutos = 30, Ordem = 0, ResponsavelId = BrunoId,
        });

        var ocupacoes = agendamento.Ocupacoes().ToList();

        // Cada uma ocupada na sua janela, ambas começando na mesma hora — e não uma
        // depois da outra, que é o que a agenda mostraria antes.
        Assert.Equal(2, ocupacoes.Count);
        Assert.All(ocupacoes, o => Assert.Equal(inicio, o.Inicio));
    }

    [Fact]
    public async Task Etapa_fora_de_ordem_nao_embaralha_a_resposta()
    {
        // Revisão na etapa 0 e treinamento na 1: a lista continua na ordem em que os
        // serviços foram pedidos, porque é por posição que a escolha casa com o serviço.
        var dia = await DiaAsync(new[] { 1, 0 });

        var slot = dia.Livres.First();
        Assert.Equal("Treinamento de equipe", slot.Atribuicoes[0].Nome);
        Assert.Equal("Revisão de contrato", slot.Atribuicoes[1].Nome);
        Assert.True(slot.Atribuicoes[1].Inicio < slot.Atribuicoes[0].Inicio);
    }
}

using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.MultiTenancy;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// A empresa escolhe o que significa "ocupado". As duas leituras são legítimas e dão
/// agendas diferentes de propósito: por serviço aproveita a janela que a pessoa não
/// está prestando; por funcionário prende quem entrou no atendimento até o fim dele.
///
/// O cenário é sempre o mesmo — um atendimento das 08:00 às 09:00 com dois serviços,
/// Bruna no primeiro e Caio no segundo — e só o modo muda.
/// </summary>
public class ModoDeOcupacaoTests : IAsyncLifetime
{
    private readonly ContextoAtual _contexto = new() { TenantId = 1, UsuarioId = 1 };
    private AppDbContext _db = null!;

    // 21/09/2026 é uma segunda-feira.
    private static readonly DateOnly Segunda = new(2026, 9, 21);

    private long BrunaId { get; set; }
    private long CaioId { get; set; }
    private long CorteId { get; set; }
    private long BarbaId { get; set; }

    public async Task InitializeAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"modo-ocupacao-{Guid.NewGuid()}")
            .Options;

        _db = new AppDbContext(opcoes, _contexto);

        _db.Tenants.Add(new Tenant
        {
            Id = 1, Slug = "empresa", NomeEmpresa = "Empresa",
            ModoDeOcupacao = ModoDeOcupacao.PorServico,
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
        var caio = new Usuario
        {
            TenantId = 1, Nome = "Caio", Email = "caio@teste.com",
            PerfilId = perfil.Id, Atendente = true,
        };
        _db.Usuarios.AddRange(bruna, caio);

        var cliente = new Cliente { TenantId = 1, Tipo = TipoCliente.Pessoa, Nome = "Cliente" };
        _db.Clientes.Add(cliente);

        var corte = new ItemCatalogo
        {
            TenantId = 1, Nome = "Corte", Tipo = TipoItem.Servico, Preco = 60m, DuracaoMinutos = 30,
        };
        var barba = new ItemCatalogo
        {
            TenantId = 1, Nome = "Barba", Tipo = TipoItem.Servico, Preco = 40m, DuracaoMinutos = 30,
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

        // O atendimento que divide a opinião: Bruna presta das 08:00 às 08:30, Caio das
        // 08:30 às 09:00. Caio está no atendimento, mas não está prestando às 08:00.
        var agendamento = new Agendamento
        {
            TenantId = 1,
            ClienteId = cliente.Id,
            Inicio = Em(8, 0),
            Fim = Em(9, 0),
            ResponsavelId = bruna.Id,
            Status = StatusAgendamento.Agendado,
        };
        agendamento.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = corte.Id, Nome = "Corte", Ordem = 0,
            Quantidade = 1, DuracaoMinutos = 30, PrecoUnitario = 60m, ResponsavelId = bruna.Id,
        });
        agendamento.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = barba.Id, Nome = "Barba", Ordem = 1,
            Quantidade = 1, DuracaoMinutos = 30, PrecoUnitario = 40m, ResponsavelId = caio.Id,
        });
        _db.Agendamentos.Add(agendamento);
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private static DateTimeOffset Em(int hora, int minuto) =>
        new(new DateTime(2026, 9, 21, hora, minuto, 0, DateTimeKind.Unspecified), TimeSpan.Zero);

    private async Task<DisponibilidadeService> ComModoAsync(ModoDeOcupacao modo)
    {
        var tenant = await _db.Tenants.FirstAsync(t => t.Id == 1);
        tenant.ModoDeOcupacao = modo;
        await _db.SaveChangesAsync();

        // Serviço novo a cada modo: ele guarda o modo lido, como faz numa requisição.
        return new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc);
    }

    /// <summary>Quem a grade oferece para um serviço de 30 min começando naquela hora.</summary>
    private static IEnumerable<long> QuemPodeAs(DiaDaAgenda dia, DateTimeOffset hora) =>
        dia.Livres.Where(s => s.Inicio == hora)
            .SelectMany(s => s.Atribuicoes[0].Candidatos)
            .Select(c => c.UsuarioId)
            .Distinct();

    [Fact]
    public async Task Por_servico_solta_quem_nao_esta_prestando_naquela_janela()
    {
        var servico = await ComModoAsync(ModoDeOcupacao.PorServico);
        var dia = await servico.ObterDiaAsync(Segunda, 30, null, default, new[] { CorteId });

        // Caio está num atendimento às 08:00, mas o serviço dele só começa 08:30.
        Assert.Contains(CaioId, QuemPodeAs(dia, Em(8, 0)));
        Assert.DoesNotContain(BrunaId, QuemPodeAs(dia, Em(8, 0)));

        // E às 08:30 os papéis se invertem: agora é Caio quem está prestando.
        Assert.Contains(BrunaId, QuemPodeAs(dia, Em(8, 30)));
        Assert.DoesNotContain(CaioId, QuemPodeAs(dia, Em(8, 30)));
    }

    [Fact]
    public async Task Por_funcionario_prende_todo_mundo_do_atendimento_ate_o_fim()
    {
        var servico = await ComModoAsync(ModoDeOcupacao.PorFuncionario);
        var dia = await servico.ObterDiaAsync(Segunda, 30, null, default, new[] { CorteId });

        // Mesmo atendimento, mesma agenda: agora ninguém entra enquanto ele não acaba.
        Assert.Empty(QuemPodeAs(dia, Em(8, 0)));
        Assert.Empty(QuemPodeAs(dia, Em(8, 30)));

        // Depois das 09:00 os dois voltam: prender é durante o atendimento, não o dia.
        Assert.Contains(BrunaId, QuemPodeAs(dia, Em(9, 0)));
        Assert.Contains(CaioId, QuemPodeAs(dia, Em(9, 0)));
    }

    [Fact]
    public async Task O_modo_muda_quantos_encaixes_o_dia_tem()
    {
        var porServico = await ComModoAsync(ModoDeOcupacao.PorServico);
        var comServico = await porServico.ObterDiaAsync(Segunda, 30, null, default, new[] { CorteId });

        var porFuncionario = await ComModoAsync(ModoDeOcupacao.PorFuncionario);
        var comFuncionario = await porFuncionario.ObterDiaAsync(
            Segunda, 30, null, default, new[] { CorteId });

        // Prender o funcionário custa encaixe — é o preço que a empresa aceita ao
        // escolher esse modo, e ele tem de aparecer.
        Assert.True(comServico.Livres.Count > comFuncionario.Livres.Count,
            $"por serviço {comServico.Livres.Count} x por funcionário {comFuncionario.Livres.Count}");
    }

    [Fact]
    public async Task Sem_modo_gravado_a_agenda_segue_por_servico()
    {
        // Empresa criada antes da configuração existir não pode mudar de agenda sozinha.
        var tenant = await _db.Tenants.FirstAsync(t => t.Id == 1);
        tenant.ModoDeOcupacao = default;
        await _db.SaveChangesAsync();

        var servico = new DisponibilidadeService(_db, _contexto, RelogioDeTeste.Utc);
        var dia = await servico.ObterDiaAsync(Segunda, 30, null, default, new[] { CorteId });

        Assert.Contains(CaioId, QuemPodeAs(dia, Em(8, 0)));
    }

    /// <summary>
    /// A armadilha do modo por funcionário: a grade monta a cadeia serviço a serviço, mas
    /// quem pega o SEGUNDO serviço fica preso desde o começo do atendimento. Checar só a
    /// janela dele ofereceria um encaixe que já nasce por cima de outro compromisso.
    /// </summary>
    [Fact]
    public async Task Por_funcionario_nao_oferece_quem_esta_ocupado_no_comeco_do_atendimento()
    {
        // Caio tem um compromisso das 10:00 às 10:30. Um atendimento de dois serviços
        // começando 10:00 poria Caio no segundo (10:30–11:00) — janela livre para ele —,
        // mas nesse modo ele estaria preso desde as 10:00.
        var outro = new Agendamento
        {
            TenantId = 1, ClienteId = 1, Inicio = Em(10, 0), Fim = Em(10, 30),
            ResponsavelId = CaioId, Status = StatusAgendamento.Agendado,
        };
        outro.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = CorteId, Nome = "Corte", Ordem = 0,
            Quantidade = 1, DuracaoMinutos = 30, PrecoUnitario = 60m, ResponsavelId = CaioId,
        });
        _db.Agendamentos.Add(outro);
        await _db.SaveChangesAsync();

        var servico = await ComModoAsync(ModoDeOcupacao.PorFuncionario);
        var dia = await servico.ObterDiaAsync(
            Segunda, 30, null, default, new[] { CorteId, BarbaId });

        var asDez = dia.Livres.Where(s => s.Inicio == Em(10, 0)).ToList();
        Assert.DoesNotContain(
            asDez.SelectMany(s => s.Atribuicoes).SelectMany(a => a.Candidatos),
            c => c.UsuarioId == CaioId);

        // Por serviço ele cabe: às 10:30 o compromisso dele já acabou.
        var porServico = await ComModoAsync(ModoDeOcupacao.PorServico);
        var diaPorServico = await porServico.ObterDiaAsync(
            Segunda, 30, null, default, new[] { CorteId, BarbaId });

        Assert.Contains(
            diaPorServico.Livres.Where(s => s.Inicio == Em(10, 0))
                .SelectMany(s => s.Atribuicoes).SelectMany(a => a.Candidatos),
            c => c.UsuarioId == CaioId);
    }

    [Fact]
    public void Por_funcionario_nao_repete_quem_presta_mais_de_um_servico()
    {
        var agendamento = new Agendamento
        {
            TenantId = 1, ClienteId = 1, Inicio = Em(8, 0), Fim = Em(9, 0), ResponsavelId = 7,
        };
        agendamento.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = 1, Nome = "A", Ordem = 0,
            Quantidade = 1, DuracaoMinutos = 30, PrecoUnitario = 10m, ResponsavelId = 7,
        });
        agendamento.Itens.Add(new AgendamentoItem
        {
            TenantId = 1, ItemCatalogoId = 2, Nome = "B", Ordem = 1,
            Quantidade = 1, DuracaoMinutos = 30, PrecoUnitario = 10m, ResponsavelId = 7,
        });

        var ocupacoes = agendamento.Ocupacoes(ModoDeOcupacao.PorFuncionario).ToList();

        Assert.Single(ocupacoes);
        Assert.Equal(7L, ocupacoes[0].UsuarioId);
        Assert.Equal(Em(8, 0), ocupacoes[0].Inicio);
        Assert.Equal(Em(9, 0), ocupacoes[0].Fim);
        // Dois serviços nesse modo viram uma janela só, que não representa nenhum deles:
        // turma por funcionário só vale quando o serviço é único.
        Assert.Null(ocupacoes[0].ItemCatalogoId);
    }
}

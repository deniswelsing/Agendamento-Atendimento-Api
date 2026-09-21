using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Infrastructure.Servicos;

/// <summary>
/// Um serviço dentro de um encaixe, já com quem vai prestá-lo e quando. Os serviços são
/// sequenciais, então pessoas diferentes podem pegar serviços diferentes do mesmo
/// atendimento sem conflito.
/// </summary>
/// <summary>Uma pessoa do time, como a grade a devolve.</summary>
public sealed record PessoaResumo(long UsuarioId, string Nome);

public sealed record AtribuicaoDeServico(
    long ItemCatalogoId,
    string Nome,
    DateTimeOffset Inicio,
    DateTimeOffset Fim,
    long ResponsavelId,
    string ResponsavelNome,
    /// <summary>
    /// Todo mundo que poderia prestar este serviço neste horário — o escolhido inclusive.
    ///
    /// Vem a lista, e não só a contagem, porque quem marca pode querer outra pessoa: sem
    /// os nomes aqui a tela teria de perguntar de novo ao servidor a cada troca.
    /// </summary>
    IReadOnlyList<PessoaResumo> Candidatos)
{
    /// <summary>Um significa que não há escolha a fazer: a tela marca e segue.</summary>
    public int Alternativas => Candidatos.Count;
}

/// <summary>Encaixe livre devolvido para o app. O app não calcula nada: só exibe.</summary>
public sealed record SlotDisponivel(
    DateTimeOffset Inicio,
    DateTimeOffset Fim,
    /// <summary>Quem responde pelo encaixe — quem presta o primeiro serviço.</summary>
    long ResponsavelId,
    string ResponsavelNome,
    /// <summary>Um por serviço, na ordem em que acontecem.</summary>
    IReadOnlyList<AtribuicaoDeServico> Atribuicoes);

/// <summary>Como está a agenda de um dia inteiro — usado pela visão de dia do app.</summary>
public sealed record DiaDaAgenda(
    DateOnly Data,
    bool Aberto,
    TimeOnly? Abertura,
    TimeOnly? Fechamento,
    TimeOnly? PausaInicio,
    TimeOnly? PausaFim,
    string? MotivoFechado,
    int IntervaloSlotMinutos,
    IReadOnlyList<SlotDisponivel> Livres,
    int TotalAgendamentos,
    /// <summary>
    /// Quando o dia não tem encaixe, o primeiro dia que tem — para a tela poder dizer
    /// "não hoje, mas na quinta" em vez de só mostrar vazio. Nulo quando há encaixe,
    /// ou quando nenhum dia à frente serve.
    /// </summary>
    DateOnly? ProximaData = null,
    /// <summary>
    /// Por que o dia não tem encaixe, quando o motivo não é a empresa estar fechada —
    /// tipicamente ninguém que preste o serviço está livre.
    /// </summary>
    string? MotivoSemEncaixe = null);

/// <summary>
/// Motor de disponibilidade: interseção entre a janela da empresa e a jornada de cada
/// atendente, menos as pausas, as exceções e o que já está agendado.
///
/// Mora no servidor de propósito — é regra de negócio, e o app só desenha o resultado.
/// </summary>
public class DisponibilidadeService
{
    private readonly AppDbContext _db;
    private readonly IContextoAtual _contexto;

    /// <summary>
    /// Lido uma vez por requisição: o modo não muda no meio de um cálculo, e perguntar a
    /// cada agendamento faria uma ida ao banco por linha da grade.
    /// </summary>
    private ModoDeOcupacao? _modo;

    public DisponibilidadeService(AppDbContext db, IContextoAtual contexto)
    {
        _db = db;
        _contexto = contexto;
    }

    /// <summary>Como esta empresa conta ocupação. Sem tenant no contexto, o padrão.</summary>
    private async Task<ModoDeOcupacao> ModoAsync(CancellationToken ct)
    {
        if (_modo is { } jaLido)
        {
            return jaLido;
        }

        var tenantId = _contexto.TenantId;
        if (tenantId is null)
        {
            return (_modo = ModoDeOcupacao.PorServico).Value;
        }

        // Tenant não é entidade de tenant: o filtro global não se aplica, então a busca é
        // pelo id mesmo.
        var modo = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => (ModoDeOcupacao?)t.ModoDeOcupacao)
            .FirstOrDefaultAsync(ct);

        return (_modo = modo ?? ModoDeOcupacao.PorServico).Value;
    }

    public async Task<DiaDaAgenda> ObterDiaAsync(
        DateOnly data,
        int duracaoMinutos,
        long? responsavelId = null,
        CancellationToken ct = default,
        IReadOnlyCollection<long>? itensIds = null,
        // Agendamento a desconsiderar — é o que faz reagendar não esbarrar em si mesmo.
        long? ignorarAgendamentoId = null)
    {
        var diaDaSemana = data.DayOfWeek;

        var horarioEmpresa = await _db.HorariosFuncionamento
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.DiaDaSemana == diaDaSemana, ct);

        var excecaoEmpresa = await _db.ExcecoesHorarioFuncionamento
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Data == data, ct);

        var agendamentosDoDia = await AgendamentosDoDiaAsync(data, ct);
        if (ignorarAgendamentoId is { } ignorar)
        {
            agendamentosDoDia = agendamentosDoDia.Where(a => a.Id != ignorar).ToList();
        }

        // Exceção de data manda sobre o horário padrão.
        var fechado = excecaoEmpresa?.Fechado ?? !(horarioEmpresa?.Aberto ?? false);
        var abertura = excecaoEmpresa?.Abertura ?? horarioEmpresa?.Abertura;
        var fechamento = excecaoEmpresa?.Fechamento ?? horarioEmpresa?.Fechamento;
        var pausaInicio = excecaoEmpresa?.PausaInicio ?? horarioEmpresa?.PausaInicio;
        var pausaFim = excecaoEmpresa?.PausaFim ?? horarioEmpresa?.PausaFim;
        var intervalo = horarioEmpresa?.IntervaloSlotMinutos ?? 30;

        if (fechado || abertura is null || fechamento is null)
        {
            return new DiaDaAgenda(
                data, false, abertura, fechamento, pausaInicio, pausaFim,
                excecaoEmpresa?.Motivo ?? "Fora do horário de funcionamento",
                intervalo, Array.Empty<SlotDisponivel>(), agendamentosDoDia.Count);
        }

        var atendentes = await AtendentesAsync(responsavelId, ct);
        var jornadas = await JornadasAsync(diaDaSemana, ct);
        var ausencias = await AusenciasAsync(data, ct);

        // A sequência de serviços do encaixe. Sem itens, é um bloco só, de qualquer
        // duração pedida — é como as telas que ainda não escolheram serviço perguntam.
        var sequencia = await SequenciaAsync(itensIds, duracaoMinutos > 0 ? duracaoMinutos : intervalo, ct);
        var duracaoTotal = sequencia.Sum(x => x.Duracao);

        // Quem pode prestar cada serviço. Serviço sem ninguém marcado é aberto a todos.
        var habilitados = new List<List<Usuario>>();
        foreach (var etapa in sequencia)
        {
            habilitados.Add(etapa.ItemCatalogoId is { } id
                ? await FiltrarPorHabilidadeAsync(atendentes, new[] { id }, ct)
                : atendentes);
        }

        // Ocupação por pessoa, no modo que a empresa escolheu: a união das janelas dos
        // itens dela, ou o atendimento inteiro.
        var modoDeOcupacao = await ModoAsync(ct);
        var ocupacao = new Dictionary<long, List<(DateTimeOffset Inicio, DateTimeOffset Fim)>>();
        foreach (var agendamento in agendamentosDoDia)
        {
            foreach (var (usuarioId, ini, fimOcup) in agendamento.Ocupacoes(modoDeOcupacao))
            {
                if (!ocupacao.TryGetValue(usuarioId, out var lista))
                {
                    ocupacao[usuarioId] = lista = new List<(DateTimeOffset, DateTimeOffset)>();
                }

                lista.Add((ini, fimOcup));
            }
        }

        foreach (var ausencia in ausencias)
        {
            var iniAusencia = ausencia.DiaInteiro ? TimeOnly.MinValue : ausencia.Inicio ?? TimeOnly.MinValue;
            var fimAusencia = ausencia.DiaInteiro ? TimeOnly.MaxValue : ausencia.Fim ?? TimeOnly.MaxValue;
            if (!ocupacao.TryGetValue(ausencia.UsuarioId, out var lista))
            {
                ocupacao[ausencia.UsuarioId] = lista = new List<(DateTimeOffset, DateTimeOffset)>();
            }

            lista.Add((Combinar(data, iniAusencia), Combinar(data, fimAusencia)));
        }

        bool PodeAtender(Usuario quem, TimeOnly de, TimeOnly ate)
        {
            var jornada = jornadas.FirstOrDefault(j => j.UsuarioId == quem.Id);
            if (jornada is null || !jornada.Trabalha)
            {
                return false;
            }

            if (de < Maior(abertura.Value, jornada.InicioEfetivo) || ate > Menor(fechamento.Value, jornada.FimEfetivo))
            {
                return false;
            }

            if (ColideComPausa(de, ate, pausaInicio, pausaFim) ||
                ColideComPausa(de, ate, jornada.PausaInicioEfetiva, jornada.PausaFimEfetiva))
            {
                return false;
            }

            var iniAbs = Combinar(data, de);
            var fimAbs = Combinar(data, ate);
            return !ocupacao.TryGetValue(quem.Id, out var ocupados) ||
                   !ocupados.Any(o => iniAbs < o.Fim && fimAbs > o.Inicio);
        }

        var livres = new List<SlotDisponivel>();
        var inicioDoDia = Maior(abertura.Value, TimeOnly.MinValue);

        for (var t = inicioDoDia; AdicionarMinutos(t, duracaoTotal) <= fechamento.Value;
             t = AdicionarMinutos(t, intervalo))
        {
            var atribuicoes = MontarCadeia(sequencia, habilitados, t, data, PodeAtender, modoDeOcupacao);
            if (atribuicoes is null)
            {
                continue;
            }

            livres.Add(new SlotDisponivel(
                atribuicoes[0].Inicio,
                atribuicoes[^1].Fim,
                atribuicoes[0].ResponsavelId,
                atribuicoes[0].ResponsavelNome,
                atribuicoes));
        }

        var ordenados = livres.OrderBy(s => s.Inicio).ThenBy(s => s.ResponsavelNome).ToList();

        var motivo = ordenados.Count > 0
            ? null
            : habilitados.Any(h => h.Count == 0)
                ? "Ninguém do time presta esse serviço."
                : "Ninguém que presta esse serviço está livre neste dia.";

        return new DiaDaAgenda(
            data, true, abertura, fechamento, pausaInicio, pausaFim, null,
            intervalo, ordenados, agendamentosDoDia.Count, null, motivo);
    }

    /// <summary>
    /// Encaixa a sequência a partir de <paramref name="inicio"/>, escolhendo quem presta
    /// cada serviço. Devolve null quando algum serviço fica sem ninguém — é o que faz o
    /// horário não ser oferecido.
    ///
    /// A escolha prefere quem já pegou o serviço anterior: o cliente não trocar de mãos
    /// sem necessidade é melhor atendimento, e não custa encaixe nenhum.
    ///
    /// Em <see cref="ModoDeOcupacao.PorFuncionario"/> o candidato precisa estar livre no
    /// atendimento INTEIRO, não só na janela do serviço dele: nesse modo entrar no
    /// atendimento prende a pessoa do começo ao fim, e checar só a própria janela deixaria
    /// criar um agendamento que já nasce por cima de outro compromisso dela.
    /// </summary>
    private static List<AtribuicaoDeServico>? MontarCadeia(
        IReadOnlyList<(long? ItemCatalogoId, string Nome, int Duracao)> sequencia,
        IReadOnlyList<List<Usuario>> habilitados,
        TimeOnly inicio,
        DateOnly data,
        Func<Usuario, TimeOnly, TimeOnly, bool> podeAtender,
        ModoDeOcupacao modo)
    {
        var atribuicoes = new List<AtribuicaoDeServico>(sequencia.Count);
        var cursor = inicio;
        Usuario? anterior = null;

        var fimDoAtendimento = AdicionarMinutos(inicio, sequencia.Sum(e => e.Duracao));

        for (var i = 0; i < sequencia.Count; i++)
        {
            var etapa = sequencia[i];
            var fim = AdicionarMinutos(cursor, etapa.Duracao);

            var (deChecagem, ateChecagem) = modo == ModoDeOcupacao.PorFuncionario
                ? (inicio, fimDoAtendimento)
                : (cursor, fim);

            var candidatos = habilitados[i]
                .Where(quem => podeAtender(quem, deChecagem, ateChecagem))
                .ToList();
            if (candidatos.Count == 0)
            {
                return null;
            }

            var escolhido = candidatos.FirstOrDefault(quem => quem.Id == anterior?.Id) ?? candidatos[0];

            atribuicoes.Add(new AtribuicaoDeServico(
                etapa.ItemCatalogoId ?? 0, etapa.Nome,
                Combinar(data, cursor), Combinar(data, fim),
                escolhido.Id, escolhido.Nome,
                candidatos.Select(c => new PessoaResumo(c.Id, c.Nome)).ToList()));

            anterior = escolhido;
            cursor = fim;
        }

        return atribuicoes;
    }

    /// <summary>Os serviços pedidos, na ordem, com nome e duração.</summary>
    private async Task<IReadOnlyList<(long? ItemCatalogoId, string Nome, int Duracao)>> SequenciaAsync(
        IReadOnlyCollection<long>? itensIds, int duracaoPadrao, CancellationToken ct)
    {
        if (itensIds is null || itensIds.Count == 0)
        {
            return new[] { ((long?)null, "Atendimento", duracaoPadrao) };
        }

        var itens = await _db.ItensCatalogo.AsNoTracking()
            .Where(i => itensIds.Contains(i.Id))
            .ToListAsync(ct);

        // Respeita a ordem em que os serviços foram pedidos: é ela que define a sequência.
        return itensIds
            .Select(id => itens.FirstOrDefault(i => i.Id == id))
            .Where(i => i is not null)
            .Select(i => ((long?)i!.Id, i.Nome, Math.Max(1, i.DuracaoMinutos ?? duracaoPadrao)))
            .ToList();
    }

    /// <summary>
    /// Esta pessoa pode prestar este serviço exatamente nesta janela?
    ///
    /// É a checagem direta, sem passar pela grade: a grade só tem horários nas fronteiras
    /// do intervalo, e o segundo serviço de um atendimento começa quando o primeiro acaba
    /// — quase nunca numa fronteira.
    /// </summary>
    public async Task<bool> PodePrestarAsync(
        long usuarioId,
        long? itemCatalogoId,
        DateTimeOffset inicio,
        DateTimeOffset fim,
        long? ignorarAgendamentoId = null,
        CancellationToken ct = default,
        // A janela do atendimento inteiro. Em PorFuncionario é ela que vale: quem entra
        // fica preso do começo ao fim, então checar só o próprio serviço aprovaria uma
        // escolha que a grade nunca ofereceu.
        DateTimeOffset? atendimentoInicio = null,
        DateTimeOffset? atendimentoFim = null)
    {
        if (await ModoAsync(ct) == ModoDeOcupacao.PorFuncionario)
        {
            inicio = atendimentoInicio ?? inicio;
            fim = atendimentoFim ?? fim;
        }

        var data = DateOnly.FromDateTime(inicio.UtcDateTime);

        var quem = await _db.Usuarios.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == usuarioId && u.Ativo && u.Atendente, ct);
        if (quem is null)
        {
            return false;
        }

        // Sabe fazer? Serviço sem executor marcado é aberto a qualquer atendente.
        if (itemCatalogoId is { } itemId)
        {
            var habilitados = await FiltrarPorHabilidadeAsync(
                new List<Usuario> { quem }, new[] { itemId }, ct);
            if (habilitados.Count == 0)
            {
                return false;
            }
        }

        var diaDaSemana = data.DayOfWeek;
        var horarioEmpresa = await _db.HorariosFuncionamento.AsNoTracking()
            .FirstOrDefaultAsync(h => h.DiaDaSemana == diaDaSemana, ct);
        var excecaoEmpresa = await _db.ExcecoesHorarioFuncionamento.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Data == data, ct);

        if (excecaoEmpresa?.Fechado ?? !(horarioEmpresa?.Aberto ?? false))
        {
            return false;
        }

        var abertura = excecaoEmpresa?.Abertura ?? horarioEmpresa?.Abertura;
        var fechamento = excecaoEmpresa?.Fechamento ?? horarioEmpresa?.Fechamento;
        if (abertura is null || fechamento is null)
        {
            return false;
        }

        var jornada = (await JornadasAsync(diaDaSemana, ct))
            .FirstOrDefault(j => j.UsuarioId == usuarioId);
        if (jornada is null || !jornada.Trabalha)
        {
            return false;
        }

        var de = TimeOnly.FromDateTime(inicio.UtcDateTime);
        var ate = TimeOnly.FromDateTime(fim.UtcDateTime);

        if (de < Maior(abertura.Value, jornada.InicioEfetivo) || ate > Menor(fechamento.Value, jornada.FimEfetivo))
        {
            return false;
        }

        var pausaInicio = excecaoEmpresa?.PausaInicio ?? horarioEmpresa?.PausaInicio;
        var pausaFim = excecaoEmpresa?.PausaFim ?? horarioEmpresa?.PausaFim;
        if (ColideComPausa(de, ate, pausaInicio, pausaFim) ||
            ColideComPausa(de, ate, jornada.PausaInicioEfetiva, jornada.PausaFimEfetiva))
        {
            return false;
        }

        foreach (var ausencia in await AusenciasAsync(data, ct))
        {
            if (ausencia.UsuarioId != usuarioId)
            {
                continue;
            }

            var iniAus = ausencia.DiaInteiro ? TimeOnly.MinValue : ausencia.Inicio ?? TimeOnly.MinValue;
            var fimAus = ausencia.DiaInteiro ? TimeOnly.MaxValue : ausencia.Fim ?? TimeOnly.MaxValue;
            if (de < fimAus && ate > iniAus)
            {
                return false;
            }
        }

        // Mesma conta da grade, e pelo mesmo modo: checar aqui de um jeito e lá de outro
        // deixaria a tela oferecer um encaixe que a criação depois recusa.
        var modoDaChecagem = await ModoAsync(ct);
        var agendamentos = await AgendamentosDoDiaAsync(data, ct);
        return !agendamentos
            .Where(a => a.Id != (ignorarAgendamentoId ?? 0))
            .SelectMany(a => a.Ocupacoes(modoDaChecagem))
            .Any(o => o.UsuarioId == usuarioId && inicio < o.Fim && fim > o.Inicio);
    }

    /// <summary>
    /// O primeiro dia com encaixe a partir de <paramref name="de"/>. É o que a tela usa
    /// para dizer "aqui não, mas na quinta" em vez de mostrar um vazio sem saída.
    /// </summary>
    public async Task<(DateOnly Data, SlotDisponivel Slot)?> ProximaOportunidadeAsync(
        DateOnly de,
        IReadOnlyCollection<long> itensIds,
        long? responsavelId = null,
        int limiteDias = 30,
        CancellationToken ct = default)
    {
        for (var i = 0; i <= limiteDias; i++)
        {
            var data = de.AddDays(i);
            var dia = await ObterDiaAsync(data, 0, responsavelId, ct, itensIds);
            if (dia.Livres.Count > 0)
            {
                return (data, dia.Livres[0]);
            }
        }

        return null;
    }

    /// <summary>Resumo por dia usado pelas visões de semana e de mês.</summary>
    public async Task<IReadOnlyList<DiaDaAgenda>> ObterPeriodoAsync(
        DateOnly de,
        DateOnly ate,
        int duracaoMinutos,
        long? responsavelId = null,
        CancellationToken ct = default,
        IReadOnlyCollection<long>? itensIds = null)
    {
        if (ate < de)
        {
            (de, ate) = (ate, de);
        }

        var dias = new List<DiaDaAgenda>();
        for (var data = de; data <= ate; data = data.AddDays(1))
        {
            dias.Add(await ObterDiaAsync(data, duracaoMinutos, responsavelId, ct, itensIds));
        }
        return dias;
    }

    /// <summary>Valida se o horário pedido cabe mesmo — chamado antes de gravar.</summary>
    public async Task<bool> EstaLivreAsync(
        DateTimeOffset inicio, DateTimeOffset fim, long responsavelId,
        long? ignorarAgendamentoId = null, CancellationToken ct = default,
        IReadOnlyCollection<long>? itensIds = null)
    {
        var cadeia = await MontarAtribuicoesAsync(
            inicio, itensIds, responsavelId, ignorarAgendamentoId, (int)(fim - inicio).TotalMinutes, ct);

        return cadeia is not null;
    }

    /// <summary>
    /// A cadeia de atribuições para um horário exato, ou null quando ele não cabe.
    /// É o que o controller usa para gravar quem presta cada serviço — e é a mesma conta
    /// que montou a grade, então o que a tela ofereceu é o que o servidor aceita.
    ///
    /// <paramref name="ignorarAgendamentoId"/> tira um agendamento da conta: é o que faz
    /// reagendar ou trocar o responsável não esbarrar no próprio compromisso.
    /// </summary>
    public async Task<IReadOnlyList<AtribuicaoDeServico>?> MontarAtribuicoesAsync(
        DateTimeOffset inicio,
        IReadOnlyCollection<long>? itensIds,
        long? responsavelId = null,
        long? ignorarAgendamentoId = null,
        int duracaoMinutos = 0,
        CancellationToken ct = default)
    {
        var data = DateOnly.FromDateTime(inicio.UtcDateTime);
        var dia = await ObterDiaAsync(
            data, duracaoMinutos, responsavelId, ct, itensIds, ignorarAgendamentoId);

        if (!dia.Aberto)
        {
            return null;
        }

        return dia.Livres.FirstOrDefault(s => s.Inicio == inicio)?.Atribuicoes;
    }

    private async Task<List<Agendamento>> AgendamentosDoDiaAsync(DateOnly data, CancellationToken ct)
    {
        var inicioDia = Combinar(data, TimeOnly.MinValue);
        var fimDia = inicioDia.AddDays(1);
        return await _db.Agendamentos
            .AsNoTracking()
            // Os itens vêm junto porque a ocupação pode ser por serviço: aí quem presta
            // só o segundo fica livre durante o primeiro.
            .Include(a => a.Itens)
            .Where(a => a.Inicio < fimDia && a.Fim > inicioDia && a.Status != StatusAgendamento.Cancelado)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Restringe os atendentes aos que sabem prestar **todos** os serviços pedidos — um
    /// encaixe é atendido por uma pessoa só, então ela precisa dar conta do conjunto.
    ///
    /// Um serviço sem executores cadastrados é aberto a qualquer atendente: é o padrão, e
    /// é o que mantém agendável tudo que existia antes desta regra.
    /// </summary>
    public async Task<List<Usuario>> FiltrarPorHabilidadeAsync(
        List<Usuario> atendentes,
        IReadOnlyCollection<long>? itensIds,
        CancellationToken ct = default)
    {
        if (itensIds is null || itensIds.Count == 0 || atendentes.Count == 0)
        {
            return atendentes;
        }

        var executores = await _db.ExecutoresDeServico
            .AsNoTracking()
            .Where(e => itensIds.Contains(e.ItemCatalogoId))
            .ToListAsync(ct);

        if (executores.Count == 0)
        {
            return atendentes;
        }

        // Só os serviços que declararam executores restringem; os outros seguem abertos.
        var comRestricao = executores.Select(e => e.ItemCatalogoId).Distinct().ToList();

        return atendentes.Where(a => comRestricao.All(
            itemId => executores.Any(e => e.ItemCatalogoId == itemId && e.UsuarioId == a.Id)))
            .ToList();
    }

    /// <summary>Quem pode prestar um serviço. Lista vazia quer dizer "qualquer atendente".</summary>
    public async Task<IReadOnlyList<long>> ExecutoresDoServicoAsync(
        long itemCatalogoId, CancellationToken ct = default) =>
        await _db.ExecutoresDeServico
            .AsNoTracking()
            .Where(e => e.ItemCatalogoId == itemCatalogoId)
            .Select(e => e.UsuarioId)
            .ToListAsync(ct);

    private async Task<List<Usuario>> AtendentesAsync(long? responsavelId, CancellationToken ct) =>
        await _db.Usuarios
            .AsNoTracking()
            .Where(u => u.Ativo && u.Atendente && !u.ConvitePendente)
            .Where(u => responsavelId == null || u.Id == responsavelId)
            .OrderBy(u => u.Nome)
            .ToListAsync(ct);

    /// <summary>
    /// O turno vem junto: quando a pessoa segue escala, é dele que saem os horários, e
    /// sem carregá-lo a janela efetiva cairia no valor antigo guardado na linha.
    /// </summary>
    private async Task<List<HorarioStaff>> JornadasAsync(DayOfWeek dia, CancellationToken ct) =>
        await _db.HorariosStaff.AsNoTracking()
            .Include(h => h.Turno)
            .Where(h => h.DiaDaSemana == dia)
            .ToListAsync(ct);

    private async Task<List<ExcecaoHorarioStaff>> AusenciasAsync(DateOnly data, CancellationToken ct) =>
        await _db.ExcecoesHorarioStaff.AsNoTracking().Where(e => e.Data == data).ToListAsync(ct);

    private static bool ColideComPausa(TimeOnly inicio, TimeOnly fim, TimeOnly? pausaInicio, TimeOnly? pausaFim) =>
        pausaInicio is not null && pausaFim is not null && inicio < pausaFim && fim > pausaInicio;

    private static TimeOnly Maior(TimeOnly a, TimeOnly b) => a > b ? a : b;

    private static TimeOnly Menor(TimeOnly a, TimeOnly b) => a < b ? a : b;

    private static TimeOnly AdicionarMinutos(TimeOnly hora, int minutos) => hora.AddMinutes(minutos);

    private static DateTimeOffset Combinar(DateOnly data, TimeOnly hora) =>
        new(data.ToDateTime(hora), TimeSpan.Zero);
}

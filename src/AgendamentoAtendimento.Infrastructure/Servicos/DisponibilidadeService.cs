using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Catalogo;
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
    IReadOnlyList<PessoaResumo> Candidatos,
    /// <summary>
    /// Capacidade da turma. 1 é atendimento individual — a esmagadora maioria.
    /// </summary>
    int Capacidade = 1,
    /// <summary>Quantas pessoas já estão nesta sessão.</summary>
    int Inscritos = 0)
{
    /// <summary>Um significa que não há escolha a fazer: a tela marca e segue.</summary>
    public int Alternativas => Candidatos.Count;

    public bool EhTurma => Capacidade > 1;

    /// <summary>Quantas ainda cabem. Zero numa turma cheia; irrelevante fora de turma.</summary>
    public int VagasRestantes => Math.Max(0, Capacidade - Inscritos);
}

/// <summary>
/// Um dia próximo com encaixe, e os primeiros horários dele. Em lista, e não um "próximo
/// dia" solto: quem marca precisa de opções na mesma resposta.
/// </summary>
public sealed record SugestaoDeDia(DateOnly Data, IReadOnlyList<SlotDisponivel> Slots);

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
    /// Quando o dia não tem encaixe, os próximos dias que têm — para a tela poder dizer
    /// "não hoje, mas na quinta às 14h" em vez de só mostrar vazio. Vazio quando há
    /// encaixe, ou quando nenhum dia à frente serve.
    /// </summary>
    IReadOnlyList<SugestaoDeDia>? Sugestoes = null,
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

    /// <summary>A política de agenda da empresa: modo de ocupação e tetos diários.</summary>
    private sealed record PoliticaDaAgenda(
        ModoDeOcupacao Modo, int LimiteDoDia, int LimitePorPessoa);

    private PoliticaDaAgenda? _politica;

    private async Task<PoliticaDaAgenda> PoliticaAsync(CancellationToken ct)
    {
        if (_politica is { } jaLida)
        {
            return jaLida;
        }

        var tenantId = _contexto.TenantId;
        if (tenantId is null)
        {
            return _politica = new PoliticaDaAgenda(ModoDeOcupacao.PorServico, 0, 0);
        }

        // Tenant não é entidade de tenant: o filtro global não se aplica, então a busca é
        // pelo id mesmo.
        var lida = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new PoliticaDaAgenda(
                t.ModoDeOcupacao, t.LimiteDiarioDeAtendimentos, t.LimiteDiarioPorPessoa))
            .FirstOrDefaultAsync(ct);

        return _politica = lida is null || !Enum.IsDefined(lida.Modo)
            ? new PoliticaDaAgenda(ModoDeOcupacao.PorServico, lida?.LimiteDoDia ?? 0,
                lida?.LimitePorPessoa ?? 0)
            : lida;
    }

    /// <summary>Como esta empresa conta ocupação. Sem tenant no contexto, o padrão.</summary>
    private async Task<ModoDeOcupacao> ModoAsync(CancellationToken ct) =>
        _modo ??= (await PoliticaAsync(ct)).Modo;

    /// <summary>
    /// Quantos atendimentos cada pessoa já tem no dia, e quantos a empresa tem no total.
    /// Um atendimento conta UMA vez por pessoa, mesmo passando por dois serviços dela:
    /// o teto é de atendimentos, não de serviços.
    /// </summary>
    private static (int Total, Dictionary<long, int> PorPessoa) ContarDoDia(
        IReadOnlyList<Agendamento> agendamentos)
    {
        var porPessoa = new Dictionary<long, int>();
        foreach (var agendamento in agendamentos)
        {
            foreach (var quem in agendamento.Itens
                         .Select(i => i.ResponsavelId ?? agendamento.ResponsavelId)
                         .Concat(new[] { agendamento.ResponsavelId })
                         .OfType<long>()
                         .Distinct())
            {
                porPessoa[quem] = porPessoa.GetValueOrDefault(quem, 0) + 1;
            }
        }

        return (agendamentos.Count, porPessoa);
    }

    /// <param name="responsaveisPorItem">
    /// Quem foi escolhido para cada serviço, na ordem de <paramref name="itensIds"/>.
    /// Posição nula é "quem estiver livre".
    ///
    /// Existe porque um atendimento passa por mais de uma pessoa: a Ana faz a manicure e
    /// o Bruno, em seguida, a hidratação. Pedir isso com o <paramref name="responsavelId"/>
    /// — que é "uma pessoa só faz tudo" — não dá, e deixar a tela recortar depois faria a
    /// contagem do dia, o motivo de não ter encaixe e o "próximo dia com vaga" saírem de
    /// uma conta diferente da que a tela mostra.
    /// </param>
    /// <param name="horaDe">
    /// A faixa de horário pedida. O atendimento INTEIRO tem de caber nela: quem pede
    /// "entre 14h e 18h" não quer um encaixe que termina 19h45. Fora da faixa o dia
    /// responde como dia sem encaixe, com o motivo dizendo qual era a faixa.
    /// </param>
    /// <param name="horaAte">O fim da faixa. Veja <paramref name="horaDe"/>.</param>
    /// <param name="etapasPorItem">
    /// A etapa de cada serviço, na ordem de <paramref name="itensIds"/>. Serviços na mesma
    /// etapa acontecem AO MESMO TEMPO, cada um com a sua pessoa; etapas diferentes são um
    /// depois do outro. Sem isto, cada serviço é a sua própria etapa — o atendimento em
    /// sequência, que é o de sempre.
    /// </param>
    public async Task<DiaDaAgenda> ObterDiaAsync(
        DateOnly data,
        int duracaoMinutos,
        long? responsavelId = null,
        CancellationToken ct = default,
        IReadOnlyCollection<long>? itensIds = null,
        // Agendamento a desconsiderar — é o que faz reagendar não esbarrar em si mesmo.
        long? ignorarAgendamentoId = null,
        IReadOnlyList<long?>? responsaveisPorItem = null,
        IReadOnlyList<int>? etapasPorItem = null,
        TimeOnly? horaDe = null,
        TimeOnly? horaAte = null)
    {
        var diaDaSemana = data.DayOfWeek;

        var horarioEmpresa = await HorarioDaEmpresaAsync(diaDaSemana, ct);

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
        var sequencia = await SequenciaAsync(
            itensIds, duracaoMinutos > 0 ? duracaoMinutos : intervalo, ct, etapasPorItem);
        var duracaoTotal = DuracaoTotal(sequencia);

        // Quem pode prestar cada serviço. Serviço sem ninguém marcado é aberto a todos.
        var habilitados = new List<List<Usuario>>();

        // A escolha de quem presta cada serviço entra aqui, e não depois: ela muda quais
        // horários existem. Quem foi escolhido e não sabe fazer aquilo aparece à parte,
        // porque "ninguém do time presta esse serviço" seria mentira nesse caso.
        string? escolhaImpossivel = null;
        for (var i = 0; i < sequencia.Count; i++)
        {
            var etapa = sequencia[i];
            var podem = etapa.ItemCatalogoId is { } id
                ? await FiltrarPorHabilidadeAsync(atendentes, new[] { id }, ct)
                : atendentes;

            var escolhido = responsaveisPorItem is not null && i < responsaveisPorItem.Count
                ? responsaveisPorItem[i]
                : null;

            if (escolhido is { } quem)
            {
                var so = podem.Where(p => p.Id == quem).ToList();
                if (so.Count == 0 && podem.Count > 0)
                {
                    var nome = atendentes.FirstOrDefault(a => a.Id == quem)?.Nome
                        ?? "Quem você escolheu";
                    escolhaImpossivel ??= $"{nome} não presta {etapa.Nome}.";
                }

                podem = so;
            }

            habilitados.Add(podem);
        }

        var temEscolha = responsaveisPorItem?.Any(r => r is not null) == true;

        // Ocupação por pessoa, no modo que a empresa escolheu: a união das janelas dos
        // itens dela, ou o atendimento inteiro.
        var politica = await PoliticaAsync(ct);
        var modoDeOcupacao = politica.Modo;
        var (totalDoDia, atendimentosPorPessoa) = ContarDoDia(agendamentosDoDia);

        // O dia cheio não oferece nada, e diz por quê: um dia em branco sem motivo
        // parece empresa fechada, que é outra coisa.
        if (politica.LimiteDoDia > 0 && totalDoDia >= politica.LimiteDoDia)
        {
            return new DiaDaAgenda(
                data, true, abertura, fechamento, pausaInicio, pausaFim, null,
                intervalo, Array.Empty<SlotDisponivel>(), agendamentosDoDia.Count, null,
                $"A empresa fechou a agenda do dia: {totalDoDia} de "
                + $"{politica.LimiteDoDia} atendimentos.");
        }
        var ocupacao = new Dictionary<long, List<Bloqueio>>();
        foreach (var agendamento in agendamentosDoDia)
        {
            foreach (var (usuarioId, ini, fimOcup, itemDoBloqueio) in agendamento.Ocupacoes(modoDeOcupacao))
            {
                if (!ocupacao.TryGetValue(usuarioId, out var lista))
                {
                    ocupacao[usuarioId] = lista = new List<Bloqueio>();
                }

                lista.Add(new Bloqueio(ini, fimOcup, itemDoBloqueio));
            }
        }

        // Turmas: quantas pessoas já estão em cada sessão. A chave é o que define uma
        // sessão — mesmo serviço, mesma pessoa atendendo, mesma janela.
        var capacidades = await CapacidadesAsync(ct);
        var inscritos = ContarInscritos(agendamentosDoDia, modoDeOcupacao);

        foreach (var ausencia in ausencias)
        {
            var iniAusencia = ausencia.DiaInteiro ? TimeOnly.MinValue : ausencia.Inicio ?? TimeOnly.MinValue;
            var fimAusencia = ausencia.DiaInteiro ? TimeOnly.MaxValue : ausencia.Fim ?? TimeOnly.MaxValue;
            if (!ocupacao.TryGetValue(ausencia.UsuarioId, out var lista))
            {
                ocupacao[ausencia.UsuarioId] = lista = new List<Bloqueio>();
            }

            // Ausência não vem de serviço nenhum: nunca é uma turma em que se possa entrar.
            lista.Add(new Bloqueio(
                Combinar(data, iniAusencia), Combinar(data, fimAusencia), null));
        }

        /// <summary>
        /// Vagas que sobram na sessão de turma daquele serviço, naquela janela, com
        /// aquela pessoa. Negativo nunca: cheia é zero.
        /// </summary>
        int VagasNaTurma(long itemId, long usuarioId, DateTimeOffset iniAbs, DateTimeOffset fimAbs)
        {
            var capacidade = capacidades.GetValueOrDefault(itemId, 1);
            if (capacidade <= 1)
            {
                return 0;
            }

            var chave = new SessaoDeTurma(itemId, usuarioId, iniAbs, fimAbs);
            return Math.Max(0, capacidade - inscritos.GetValueOrDefault(chave, 0));
        }

        bool PodeAtender(Usuario quem, TimeOnly de, TimeOnly ate, long? itemId = null)
        {
            var jornada = jornadas.FirstOrDefault(j => j.UsuarioId == quem.Id);
            if (jornada is null || !jornada.Trabalha)
            {
                return false;
            }

            // Quem já bateu o próprio teto no dia sai da grade. A empresa pode aguentar
            // vinte atendimentos num dia em que ninguém deveria fazer mais de seis.
            if (politica.LimitePorPessoa > 0
                && atendimentosPorPessoa.GetValueOrDefault(quem.Id, 0) >= politica.LimitePorPessoa)
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
            if (!ocupacao.TryGetValue(quem.Id, out var ocupados))
            {
                return true;
            }

            return !ocupados.Any(o =>
            {
                if (iniAbs >= o.Fim || fimAbs <= o.Inicio)
                {
                    return false;
                }

                // O bloqueio é a própria sessão de turma que estamos tentando entrar, e
                // ela ainda tem vaga: o horário continua valendo. Sem isto, a turma
                // sumiria da grade no primeiro inscrito, que é o oposto de ser turma.
                return !(itemId is { } id
                    && o.ItemCatalogoId == id
                    && o.Inicio == iniAbs
                    && o.Fim == fimAbs
                    && VagasNaTurma(id, quem.Id, iniAbs, fimAbs) > 0);
            });
        }

        var livres = new List<SlotDisponivel>();
        var inicioDoDia = Maior(abertura.Value, TimeOnly.MinValue);

        // A conta anda em minutos do dia, e não em TimeOnly: TimeOnly.AddMinutes dá a volta
        // na meia-noite, e com fechamento perto dela (23:30, 23:59) "t + duração" voltava
        // para a madrugada, continuava <= fechamento e o laço nunca terminava.
        var minutoDeFechamento = (int)fechamento.Value.ToTimeSpan().TotalMinutes;
        for (var minuto = (int)inicioDoDia.ToTimeSpan().TotalMinutes;
             minuto + duracaoTotal <= minutoDeFechamento;
             minuto += Math.Max(1, intervalo))
        {
            var t = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minuto));
            var atribuicoes = MontarCadeia(
                sequencia, habilitados, t, data, PodeAtender, modoDeOcupacao,
                (itemId, usuarioId, ini, fim) => itemId is { } id
                    ? (capacidades.GetValueOrDefault(id, 1),
                       inscritos.GetValueOrDefault(new SessaoDeTurma(id, usuarioId, ini, fim), 0))
                    : (1, 0));
            if (atribuicoes is null)
            {
                continue;
            }

            livres.Add(new SlotDisponivel(
                atribuicoes.Min(a => a.Inicio),
                // O fim é o do serviço que termina por último, e não o do último da
                // lista: com serviços ao mesmo tempo o mais longo pode estar no meio.
                atribuicoes.Max(a => a.Fim),
                atribuicoes[0].ResponsavelId,
                atribuicoes[0].ResponsavelNome,
                atribuicoes));
        }

        var todosDoDia = livres.OrderBy(s => s.Inicio).ThenBy(s => s.ResponsavelNome).ToList();

        // A faixa corta no fim, e não na montagem da cadeia: é o mesmo dia, visto por
        // uma janela menor — e é o que deixa dizer "havia horário, mas não nessa faixa".
        var ordenados = todosDoDia
            .Where(s => horaDe is not { } de || TimeOnly.FromDateTime(s.Inicio.UtcDateTime) >= de)
            .Where(s => horaAte is not { } ate || TimeOnly.FromDateTime(s.Fim.UtcDateTime) <= ate)
            .ToList();

        var soAFaixaCortou = ordenados.Count == 0 && todosDoDia.Count > 0;

        var todosNoTeto = politica.LimitePorPessoa > 0 && atendentes.Count > 0
            && atendentes.All(a =>
                atendimentosPorPessoa.GetValueOrDefault(a.Id, 0) >= politica.LimitePorPessoa);

        var motivo = ordenados.Count > 0
            ? null
            // Dizer "ninguém está livre" quando havia horário fora da faixa mandaria
            // procurar outro dia quando bastava abrir a faixa.
            : soAFaixaCortou
                ? $"Há horário neste dia, mas não entre {Faixa(horaDe, horaAte)}."
                : escolhaImpossivel is not null
                ? escolhaImpossivel
                : habilitados.Any(h => h.Count == 0)
                ? "Ninguém do time presta esse serviço."
                // "Ninguém está livre" seria mentira quando o que fechou o dia foi o
                // teto, e não a agenda.
                : todosNoTeto
                    ? $"Todo o time já bateu o teto de {politica.LimitePorPessoa} "
                      + "atendimento(s) por dia."
                    // Com pessoas escolhidas, o dia vazio quase sempre é a agenda delas —
                    // e não a do time. Dizer "ninguém está livre" mandaria procurar outro
                    // dia quando bastava soltar uma escolha.
                    : temEscolha
                        ? "Quem você escolheu não tem horário livre neste dia."
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
        IReadOnlyList<(long? ItemCatalogoId, string Nome, int Duracao, int Etapa)> sequencia,
        IReadOnlyList<List<Usuario>> habilitados,
        TimeOnly inicio,
        DateOnly data,
        Func<Usuario, TimeOnly, TimeOnly, long?, bool> podeAtender,
        ModoDeOcupacao modo,
        /// <summary>Capacidade e inscritos da sessão, para a tela poder dizer "3 de 8".</summary>
        Func<long?, long, DateTimeOffset, DateTimeOffset, (int Capacidade, int Inscritos)> vagas)
    {
        // Guardadas na posição em que o serviço foi pedido, e não na ordem em que a
        // cadeia as monta: com etapas fora de ordem as duas listas divergiriam, e quem
        // casa escolha com serviço por posição — o POST — passaria a casar errado.
        var porPosicao = new AtribuicaoDeServico?[sequencia.Count];
        var cursor = inicio;
        Usuario? anterior = null;

        var fimDoAtendimento = AdicionarMinutos(inicio, DuracaoTotal(sequencia));

        // Índices agrupados por etapa, na ordem em que as etapas aparecem. O que está na
        // mesma etapa acontece ao mesmo tempo.
        // Ordenadas pelo número da etapa, e não pela ordem em que os serviços foram
        // pedidos: é o número que diz o que vem antes.
        foreach (var etapa in sequencia
                     .Select((e, i) => (Indice: i, Passo: e))
                     .GroupBy(x => x.Passo.Etapa)
                     .OrderBy(g => g.Key))
        {
            var doPasso = etapa.ToList();
            var fimDaEtapa = cursor;

            // Duas coisas ao mesmo tempo não podem ser da mesma pessoa: ela estaria em
            // dois lugares na mesma hora. Quem já pegou um serviço desta etapa sai da
            // conta dos outros.
            var jaNaEtapa = new HashSet<long>();

            foreach (var (indice, passo) in doPasso)
            {
                var fim = AdicionarMinutos(cursor, passo.Duracao);

                var (deChecagem, ateChecagem) = modo == ModoDeOcupacao.PorFuncionario
                    ? (inicio, fimDoAtendimento)
                    : (cursor, fim);

                var candidatos = habilitados[indice]
                    .Where(quem => !jaNaEtapa.Contains(quem.Id))
                    .Where(quem => podeAtender(quem, deChecagem, ateChecagem, passo.ItemCatalogoId))
                    .ToList();
                if (candidatos.Count == 0)
                {
                    return null;
                }

                // Continuidade só vale entre etapas: dentro da etapa a pessoa anterior é
                // justamente quem NÃO pode pegar o serviço de agora.
                var escolhido = doPasso.Count == 1
                    ? candidatos.FirstOrDefault(quem => quem.Id == anterior?.Id) ?? candidatos[0]
                    : candidatos[0];

                var (capacidade, jaInscritos) = vagas(
                    passo.ItemCatalogoId, escolhido.Id, Combinar(data, cursor), Combinar(data, fim));

                porPosicao[indice] = new AtribuicaoDeServico(
                    passo.ItemCatalogoId ?? 0, passo.Nome,
                    Combinar(data, cursor), Combinar(data, fim),
                    escolhido.Id, escolhido.Nome,
                    candidatos.Select(c => new PessoaResumo(c.Id, c.Nome)).ToList(),
                    capacidade, jaInscritos);

                jaNaEtapa.Add(escolhido.Id);
                if (fim > fimDaEtapa)
                {
                    fimDaEtapa = fim;
                }
            }

            // Quem segue para a próxima etapa é quem estava sozinho nesta: com duas
            // pessoas em paralelo não há "a mesma pessoa" para continuar.
            anterior = doPasso.Count == 1
                ? habilitados[doPasso[0].Indice]
                    .FirstOrDefault(u => u.Id == porPosicao[doPasso[0].Indice]!.ResponsavelId)
                : null;

            cursor = fimDaEtapa;
        }

        return porPosicao.Select(a => a!).ToList();
    }

    /// <summary>A faixa como a mensagem a escreve. Só um lado dela também é faixa.</summary>
    private static string Faixa(TimeOnly? de, TimeOnly? ate) => (de, ate) switch
    {
        ({ } d, { } a) => $@"{d:HH\:mm} e {a:HH\:mm}",
        ({ } d, null) => $@"{d:HH\:mm} e o fechamento",
        (null, { } a) => $@"a abertura e {a:HH\:mm}",
        _ => "essa faixa",
    };

    /// <summary>
    /// Quanto o atendimento inteiro dura: a soma das etapas, e cada etapa vale o serviço
    /// mais longo dela. Somar tudo daria um atendimento mais longo do que ele é quando
    /// duas pessoas atendem ao mesmo tempo.
    /// </summary>
    private static int DuracaoTotal(
        IReadOnlyList<(long? ItemCatalogoId, string Nome, int Duracao, int Etapa)> sequencia) =>
        sequencia.GroupBy(e => e.Etapa).Sum(g => g.Max(e => e.Duracao));

    /// <summary>
    /// Os serviços pedidos, na ordem, com nome, duração e ETAPA.
    ///
    /// A etapa é o que diz o que acontece junto: mesma etapa é ao mesmo tempo, etapas
    /// diferentes são um depois do outro. Sem <paramref name="etapasPorItem"/> cada
    /// serviço é a sua própria etapa — o atendimento em sequência, que é o de sempre.
    /// </summary>
    private async Task<IReadOnlyList<(long? ItemCatalogoId, string Nome, int Duracao, int Etapa)>>
        SequenciaAsync(
            IReadOnlyCollection<long>? itensIds,
            int duracaoPadrao,
            CancellationToken ct,
            IReadOnlyList<int>? etapasPorItem = null)
    {
        if (itensIds is null || itensIds.Count == 0)
        {
            return new[] { ((long?)null, "Atendimento", duracaoPadrao, 0) };
        }

        var itens = await _db.ItensCatalogo.AsNoTracking()
            .Where(i => itensIds.Contains(i.Id))
            .ToListAsync(ct);

        // Respeita a ordem em que os serviços foram pedidos: é ela que define a sequência.
        return itensIds
            .Select((id, posicao) => (Item: itens.FirstOrDefault(i => i.Id == id), Posicao: posicao))
            .Where(x => x.Item is not null)
            .Select(x => ((long?)x.Item!.Id, x.Item.Nome,
                Math.Max(1, x.Item.DuracaoMinutos ?? duracaoPadrao),
                etapasPorItem is not null && x.Posicao < etapasPorItem.Count
                    ? etapasPorItem[x.Posicao]
                    : x.Posicao))
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
        var horarioEmpresa = await HorarioDaEmpresaAsync(diaDaSemana, ct);
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
        var politicaDaChecagem = await PoliticaAsync(ct);
        var modoDaChecagem = politicaDaChecagem.Modo;
        var agendamentos = (await AgendamentosDoDiaAsync(data, ct))
            .Where(a => a.Id != (ignorarAgendamentoId ?? 0))
            .ToList();

        // Os tetos valem aqui também: sem isto, a criação direta furaria o limite que a
        // grade respeita.
        var (totalNoDia, porPessoaNoDia) = ContarDoDia(agendamentos);
        if (politicaDaChecagem.LimiteDoDia > 0 && totalNoDia >= politicaDaChecagem.LimiteDoDia)
        {
            return false;
        }

        if (politicaDaChecagem.LimitePorPessoa > 0
            && porPessoaNoDia.GetValueOrDefault(usuarioId, 0) >= politicaDaChecagem.LimitePorPessoa)
        {
            return false;
        }

        var capacidade = itemCatalogoId is { } paraTurma
            ? (await CapacidadesAsync(ct)).GetValueOrDefault(paraTurma, 1)
            : 1;

        var conflitos = agendamentos
            .SelectMany(a => a.Ocupacoes(modoDaChecagem))
            .Where(o => o.UsuarioId == usuarioId && inicio < o.Fim && fim > o.Inicio)
            .ToList();

        if (conflitos.Count == 0)
        {
            return true;
        }

        // Turma: o conflito pode ser a própria sessão em que se quer entrar. Só vale
        // quando TODO conflito é essa sessão — outro compromisso por cima continua
        // impedindo, turma ou não.
        if (capacidade <= 1 || itemCatalogoId is not { } daTurma)
        {
            return false;
        }

        var todosSaoDaMesmaSessao = conflitos.All(o =>
            o.ItemCatalogoId == daTurma && o.Inicio == inicio && o.Fim == fim);

        if (!todosSaoDaMesmaSessao)
        {
            return false;
        }

        var inscritos = ContarInscritos(agendamentos, modoDaChecagem)
            .GetValueOrDefault(new SessaoDeTurma(daTurma, usuarioId, inicio, fim), 0);

        return inscritos < capacidade;
    }

    /// <summary>
    /// Os próximos dias que têm encaixe, a partir de <paramref name="de"/>, já com os
    /// primeiros horários de cada um.
    ///
    /// Um dia sem encaixe não pode ser um beco sem saída, e apontar só o próximo dia
    /// obriga a tela a perguntar de novo para mostrar o seguinte. Alguns dias com alguns
    /// horários cada é o que deixa escolher sem sair da tela.
    ///
    /// As escolhas e a faixa de horário viajam junto: uma sugestão que as ignorasse
    /// mandaria para um dia que a tela mostraria vazio.
    /// </summary>
    public async Task<IReadOnlyList<(DateOnly Data, IReadOnlyList<SlotDisponivel> Slots)>>
        SugestoesAsync(
            DateOnly de,
            IReadOnlyCollection<long> itensIds,
            long? responsavelId = null,
            int limiteDias = 30,
            CancellationToken ct = default,
            IReadOnlyList<long?>? responsaveisPorItem = null,
            IReadOnlyList<int>? etapasPorItem = null,
            TimeOnly? horaDe = null,
            TimeOnly? horaAte = null,
            int diasSugeridos = 3,
            int slotsPorDia = 3)
    {
        var sugestoes = new List<(DateOnly, IReadOnlyList<SlotDisponivel>)>();

        for (var i = 0; i <= limiteDias && sugestoes.Count < diasSugeridos; i++)
        {
            var data = de.AddDays(i);
            var dia = await ObterDiaAsync(
                data, 0, responsavelId, ct, itensIds, null, responsaveisPorItem, etapasPorItem,
                horaDe, horaAte);

            if (dia.Livres.Count > 0)
            {
                sugestoes.Add((data, dia.Livres.Take(slotsPorDia).ToList()));
            }
        }

        return sugestoes;
    }

    /// <summary>Resumo por dia usado pelas visões de semana e de mês.</summary>
    public async Task<IReadOnlyList<DiaDaAgenda>> ObterPeriodoAsync(
        DateOnly de,
        DateOnly ate,
        int duracaoMinutos,
        long? responsavelId = null,
        CancellationToken ct = default,
        IReadOnlyCollection<long>? itensIds = null,
        IReadOnlyList<long?>? responsaveisPorItem = null,
        IReadOnlyList<int>? etapasPorItem = null,
        TimeOnly? horaDe = null,
        TimeOnly? horaAte = null)
    {
        if (ate < de)
        {
            (de, ate) = (ate, de);
        }

        var dias = new List<DiaDaAgenda>();
        for (var data = de; data <= ate; data = data.AddDays(1))
        {
            // A semana conta o que o dia mostra: sem as escolhas aqui, o calendário
            // prometeria encaixe em dias que a tela abriria vazios.
            dias.Add(await ObterDiaAsync(
                data, duracaoMinutos, responsavelId, ct, itensIds, null,
                responsaveisPorItem, etapasPorItem, horaDe, horaAte));
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
        CancellationToken ct = default,
        IReadOnlyList<int>? etapasPorItem = null)
    {
        var data = DateOnly.FromDateTime(inicio.UtcDateTime);
        var dia = await ObterDiaAsync(
            data, duracaoMinutos, responsavelId, ct, itensIds, ignorarAgendamentoId,
            null, etapasPorItem);

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

        var executores = (await TodosOsExecutoresAsync(ct))
            .Where(e => itensIds.Contains(e.ItemCatalogoId))
            .ToList();

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


    /// <summary>
    /// Quem pode pegar este serviço nesta janela: presta aquilo E está livre agora,
    /// desconsiderando o próprio atendimento que está sendo remanejado — senão quem já
    /// está nele apareceria como ocupado por si mesmo.
    ///
    /// Existe para a tela não precisar oferecer o time inteiro e deixar o servidor
    /// recusar depois. Mostrar quem não pode é prometer uma troca que não acontece, e a
    /// pessoa só descobre no erro.
    ///
    /// Passa por PodePrestarAsync, um a um, de propósito: é a MESMA regra que valida a
    /// troca. Uma segunda implementação aqui acabaria discordando dela.
    /// </summary>
    public async Task<IReadOnlyList<PessoaResumo>> QuemPodePrestarAsync(
        long? itemCatalogoId,
        DateTimeOffset inicio,
        DateTimeOffset fim,
        long? ignorarAgendamentoId = null,
        CancellationToken ct = default,
        DateTimeOffset? atendimentoInicio = null,
        DateTimeOffset? atendimentoFim = null)
    {
        var atendentes = await AtendentesAsync(null, ct);
        var livres = new List<PessoaResumo>();

        foreach (var quem in atendentes)
        {
            var pode = await PodePrestarAsync(
                quem.Id, itemCatalogoId, inicio, fim, ignorarAgendamentoId, ct,
                atendimentoInicio, atendimentoFim);

            if (pode)
            {
                livres.Add(new PessoaResumo(quem.Id, quem.Nome));
            }
        }

        return livres;
    }

    private async Task<List<Usuario>> AtendentesAsync(long? responsavelId, CancellationToken ct)
    {
        _atendentes ??= await _db.Usuarios
            .AsNoTracking()
            .Where(u => u.Ativo && u.Atendente && !u.ConvitePendente)
            .OrderBy(u => u.Nome)
            .ToListAsync(ct);

        return _atendentes.Where(u => responsavelId == null || u.Id == responsavelId).ToList();
    }

    // ------------------------------------------------------------------------------
    // A configuração da agenda (funcionamento, jornadas, quem presta o quê, turmas e o
    // time) não muda no meio de uma requisição, e a grade a relia a cada dia calculado:
    // uma semana, um mês ou as sugestões de 30 dias viravam centenas de consultas iguais.
    // Fica lida uma vez por instância — o serviço é por requisição. O que muda dia a dia
    // (agendamentos, exceções, ausências) continua sendo lido a cada dia.
    // ------------------------------------------------------------------------------
    private List<HorarioFuncionamento>? _funcionamento;
    private List<HorarioStaff>? _jornadas;
    private List<Usuario>? _atendentes;
    private List<ExecutorDeServico>? _executores;
    private Dictionary<long, int>? _capacidades;

    private async Task<HorarioFuncionamento?> HorarioDaEmpresaAsync(DayOfWeek dia, CancellationToken ct)
    {
        _funcionamento ??= await _db.HorariosFuncionamento.AsNoTracking().ToListAsync(ct);
        return _funcionamento.FirstOrDefault(h => h.DiaDaSemana == dia);
    }

    private async Task<List<ExecutorDeServico>> TodosOsExecutoresAsync(CancellationToken ct) =>
        _executores ??= await _db.ExecutoresDeServico.AsNoTracking().ToListAsync(ct);

    /// <summary>
    /// O turno vem junto: quando a pessoa segue escala, é dele que saem os horários, e
    /// sem carregá-lo a janela efetiva cairia no valor antigo guardado na linha.
    /// </summary>
    /// <summary>Um pedaço do dia em que alguém está comprometido, e por qual serviço.</summary>
    private readonly record struct Bloqueio(
        DateTimeOffset Inicio, DateTimeOffset Fim, long? ItemCatalogoId);

    /// <summary>
    /// A identidade de uma sessão de turma. Não há entidade para ela de propósito: uma
    /// turma é o conjunto de agendamentos que caem no mesmo serviço, com a mesma pessoa,
    /// na mesma janela — inventar uma tabela só duplicaria essa verdade.
    /// </summary>
    private readonly record struct SessaoDeTurma(
        long ItemCatalogoId, long UsuarioId, DateTimeOffset Inicio, DateTimeOffset Fim);

    /// <summary>Capacidade de cada serviço. 1 é atendimento individual.</summary>
    private async Task<Dictionary<long, int>> CapacidadesAsync(CancellationToken ct) =>
        _capacidades ??= await _db.ItensCatalogo.AsNoTracking()
            .Where(i => i.CapacidadeTurma > 1)
            .ToDictionaryAsync(i => i.Id, i => i.CapacidadeTurma, ct);

    /// <summary>Quantas pessoas já estão em cada sessão de turma do dia.</summary>
    private static Dictionary<SessaoDeTurma, int> ContarInscritos(
        IEnumerable<Agendamento> agendamentos, ModoDeOcupacao modo)
    {
        var contagem = new Dictionary<SessaoDeTurma, int>();
        foreach (var agendamento in agendamentos)
        {
            foreach (var (usuarioId, ini, fim, itemId) in agendamento.Ocupacoes(modo))
            {
                if (itemId is not { } id)
                {
                    continue;
                }

                var chave = new SessaoDeTurma(id, usuarioId, ini, fim);
                contagem[chave] = contagem.GetValueOrDefault(chave, 0) + 1;
            }
        }

        return contagem;
    }

    private async Task<List<HorarioStaff>> JornadasAsync(DayOfWeek dia, CancellationToken ct)
    {
        _jornadas ??= await _db.HorariosStaff.AsNoTracking()
            .Include(h => h.Turno)
            .ToListAsync(ct);

        return _jornadas.Where(h => h.DiaDaSemana == dia).ToList();
    }

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

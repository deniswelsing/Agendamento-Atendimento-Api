using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>
/// Horário de funcionamento da empresa, jornada do time e as exceções dos dois. É o que
/// limita toda a agenda — nenhum encaixe é oferecido fora do que está configurado aqui.
/// </summary>
[Route("api/horarios")]
public class HorariosController : ControllerBaseApi
{
    private readonly AppDbContext _db;

    public HorariosController(AppDbContext db) => _db = db;

    // ------------------------------------------------------------- modo de ocupação
    /// <summary>
    /// Como a agenda conta ocupação nesta empresa. Fica junto dos horários porque é da
    /// mesma natureza: define o que a grade considera livre.
    /// </summary>
    [HttpGet("ocupacao")]
    [RequerPermissao("horarios.ver")]
    public async Task<ActionResult<ModoDeOcupacaoDto>> Ocupacao(CancellationToken ct)
    {
        var modo = await ModoDaEmpresaAsync(ct);
        return Ok(ModoDeOcupacaoDto.De(modo));
    }

    [HttpPut("ocupacao")]
    [RequerPermissao("horarios.editar")]
    public async Task<ActionResult<ModoDeOcupacaoDto>> SalvarOcupacao(
        ModoDeOcupacaoRequest req, CancellationToken ct)
    {
        if (!Enum.IsDefined(req.Modo))
        {
            throw new RegraDeNegocioException("Modo de ocupação desconhecido.", "MODO_INVALIDO");
        }

        var tenant = NaoNulo(
            await _db.Tenants.FirstOrDefaultAsync(t => t.Id == TenantId, ct),
            "Empresa não encontrada.");

        tenant.ModoDeOcupacao = req.Modo;
        await _db.SaveChangesAsync(ct);

        return Ok(ModoDeOcupacaoDto.De(tenant.ModoDeOcupacao));
    }

    /// <summary>
    /// A jornada precisa caber no funcionamento da empresa. A agenda já usa a interseção
    /// das duas janelas, então gravar fora dela guardaria hora que nunca vira encaixe — e
    /// a tela mostraria uma escala que a agenda não cumpre.
    /// </summary>
    private static void ValidarCabeNoFuncionamento(
        DayOfWeek diaDaSemana, TimeOnly inicio, TimeOnly fim,
        IReadOnlyList<HorarioFuncionamento> funcionamento)
    {
        if (inicio >= fim)
        {
            throw new RegraDeNegocioException(
                "O início da jornada precisa vir antes do fim.", "JANELA_INVALIDA");
        }

        var empresa = funcionamento.FirstOrDefault(h => h.DiaDaSemana == diaDaSemana);
        var nomeDoDia = NomeDoDia(diaDaSemana);

        if (empresa is null || !empresa.Aberto || empresa.Abertura is null || empresa.Fechamento is null)
        {
            throw new RegraDeNegocioException(
                $"A empresa não abre {nomeDoDia}. Abra o dia em Horários da empresa "
                + "antes de escalar alguém.",
                "EMPRESA_FECHADA");
        }

        if (inicio < empresa.Abertura || fim > empresa.Fechamento)
        {
            // Fora da interpolação: dentro dela o ":" abriria o formato.
            var abre = empresa.Abertura.Value.ToString("HH:mm");
            var fecha = empresa.Fechamento.Value.ToString("HH:mm");
            // Começa a frase, então vai com maiúscula.
            var noDia = char.ToUpperInvariant(nomeDoDia[0]) + nomeDoDia[1..];
            throw new RegraDeNegocioException(
                $"{noDia} a empresa funciona das {abre} às {fecha}. "
                + "A jornada precisa caber nesse intervalo.",
                "FORA_DO_FUNCIONAMENTO");
        }
    }

    /// <summary>
    /// Atrela uma pessoa a um turno de uma vez. Sem isto, escalar alguém em cinco dias é
    /// repetir a mesma escolha cinco vezes — que é justamente o trabalho que o turno
    /// existe para evitar.
    ///
    /// Dia em que o turno não cabe é PULADO, não recusado: pedir a semana inteira numa
    /// empresa que fecha mais cedo no sábado é um pedido razoável, e recusar tudo por
    /// causa de um dia obrigaria a montar a lista à mão.
    /// </summary>
    [HttpPut("staff/{usuarioId:long}/turno")]
    [RequerPermissao("horarios.editar")]
    [RequerRecurso(CatalogoRecursos.JornadaPorPessoa)]
    public async Task<ActionResult<IReadOnlyList<HorarioStaffDto>>> AtrelarAoTurno(
        long usuarioId, AtrelarAoTurnoRequest req, CancellationToken ct)
    {
        var usuario = NaoNulo(
            await _db.Usuarios.FirstOrDefaultAsync(u => u.Id == usuarioId, ct),
            "Usuário não encontrado.");

        var turno = NaoNulo(
            await _db.Turnos.FirstOrDefaultAsync(t => t.Id == req.TurnoId && t.Ativo, ct),
            "Turno não encontrado ou inativo.");

        var funcionamento = await _db.HorariosFuncionamento.AsNoTracking().ToListAsync(ct);

        // Sem dias no pedido, vale a semana que a empresa abre: é o que "atrelar ao
        // turno" quer dizer quando ninguém detalha.
        var dias = req.Dias is { Count: > 0 }
            ? req.Dias.Distinct().ToList()
            : funcionamento.Where(h => h.Aberto).Select(h => h.DiaDaSemana).ToList();

        var aplicados = dias
            .Where(d => funcionamento.Any(h =>
                h.DiaDaSemana == d && h.Aberto
                && h.Abertura is { } abre && h.Fechamento is { } fecha
                && turno.Inicio >= abre && turno.Fim <= fecha))
            .ToList();

        if (aplicados.Count == 0)
        {
            throw new RegraDeNegocioException(
                $"O turno {turno.Nome} ({turno.Janela}) não cabe no funcionamento da "
                + "empresa em nenhum dos dias pedidos.",
                "TURNO_NAO_CABE");
        }

        var atuais = await _db.HorariosStaff.Where(h => h.UsuarioId == usuario.Id).ToListAsync(ct);
        foreach (var dia in aplicados)
        {
            var horario = atuais.FirstOrDefault(h => h.DiaDaSemana == dia);
            if (horario is null)
            {
                horario = new HorarioStaff { UsuarioId = usuario.Id, DiaDaSemana = dia };
                _db.HorariosStaff.Add(horario);
            }

            horario.TurnoId = turno.Id;
            horario.Trabalha = true;
        }

        await _db.SaveChangesAsync(ct);
        return await Staff(usuario.Id, ct);
    }

    /// <summary>
    /// Dá à pessoa um horário próprio naquele dia, soltando-a de qualquer turno. Soltar
    /// sem pôr um horário no lugar deixaria a linha valendo pela janela antiga guardada
    /// nela, que ninguém escolheu.
    /// </summary>
    [HttpPut("staff/{usuarioId:long}/horario-proprio")]
    [RequerPermissao("horarios.editar")]
    [RequerRecurso(CatalogoRecursos.JornadaPorPessoa)]
    public async Task<ActionResult<IReadOnlyList<HorarioStaffDto>>> DefinirHorarioProprio(
        long usuarioId, HorarioProprioRequest req, CancellationToken ct)
    {
        var usuario = NaoNulo(
            await _db.Usuarios.FirstOrDefaultAsync(u => u.Id == usuarioId, ct),
            "Usuário não encontrado.");

        var funcionamento = await _db.HorariosFuncionamento.AsNoTracking().ToListAsync(ct);

        if (req.Trabalha)
        {
            ValidarCabeNoFuncionamento(req.DiaDaSemana, req.Inicio, req.Fim, funcionamento);

            if (req.PausaInicio is { } pi && req.PausaFim is { } pf
                && (pi >= pf || pi < req.Inicio || pf > req.Fim))
            {
                throw new RegraDeNegocioException(
                    "A pausa precisa ficar dentro da jornada.", "PAUSA_INVALIDA");
            }
        }

        var horario = await _db.HorariosStaff
            .FirstOrDefaultAsync(h => h.UsuarioId == usuario.Id && h.DiaDaSemana == req.DiaDaSemana, ct);

        if (horario is null)
        {
            horario = new HorarioStaff { UsuarioId = usuario.Id, DiaDaSemana = req.DiaDaSemana };
            _db.HorariosStaff.Add(horario);
        }

        // Horário próprio é o oposto de seguir turno: soltar o vínculo é o ponto.
        horario.TurnoId = null;
        horario.Inicio = req.Inicio;
        horario.Fim = req.Fim;
        horario.PausaInicio = req.PausaInicio;
        horario.PausaFim = req.PausaFim;
        horario.Trabalha = req.Trabalha;

        await _db.SaveChangesAsync(ct);
        return await Staff(usuario.Id, ct);
    }

    private static string NomeDoDia(DayOfWeek dia) => dia switch
    {
        DayOfWeek.Sunday => "aos domingos",
        DayOfWeek.Monday => "às segundas",
        DayOfWeek.Tuesday => "às terças",
        DayOfWeek.Wednesday => "às quartas",
        DayOfWeek.Thursday => "às quintas",
        DayOfWeek.Friday => "às sextas",
        _ => "aos sábados",
    };

    private async Task<ModoDeOcupacao> ModoDaEmpresaAsync(CancellationToken ct) =>
        await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == TenantId)
            .Select(t => t.ModoDeOcupacao)
            .FirstOrDefaultAsync(ct) is var lido && Enum.IsDefined(lido)
            ? lido
            : ModoDeOcupacao.PorServico;

    // --------------------------------------------------------------- limite diário
    /// <summary>
    /// Os tetos de atendimento por dia. Ficam aqui porque são da mesma natureza do
    /// horário: definem o que a agenda ainda oferece.
    /// </summary>
    [HttpGet("limite-diario")]
    [RequerPermissao("horarios.ver")]
    public async Task<ActionResult<LimiteDiarioDto>> LimiteDiario(
        [FromQuery] DateOnly? data, CancellationToken ct = default)
    {
        var tenant = NaoNulo(
            await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == TenantId, ct),
            "Empresa não encontrada.");

        var dia = data ?? DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var usados = await ContarDoDiaAsync(dia, ct);

        return Ok(Montar(tenant.LimiteDiarioDeAtendimentos, tenant.LimiteDiarioPorPessoa, usados));
    }

    [HttpPut("limite-diario")]
    [RequerPermissao("horarios.editar")]
    [RequerRecurso(CatalogoRecursos.LimiteDiario)]
    public async Task<ActionResult<LimiteDiarioDto>> SalvarLimiteDiario(
        LimiteDiarioRequest req, CancellationToken ct)
    {
        if (req.LimiteDoDia < 0 || req.LimitePorPessoa < 0)
        {
            throw new RegraDeNegocioException("O teto não pode ser negativo.", "LIMITE_INVALIDO");
        }

        // Um teto por pessoa maior que o do dia nunca seria alcançado: o do dia fecharia
        // a agenda antes. Deixar passar daria um número que não faz nada.
        if (req.LimiteDoDia > 0 && req.LimitePorPessoa > req.LimiteDoDia)
        {
            throw new RegraDeNegocioException(
                $"O teto por pessoa ({req.LimitePorPessoa}) não pode passar do teto do dia "
                + $"({req.LimiteDoDia}): o do dia fecharia a agenda antes.",
                "LIMITE_INCOERENTE");
        }

        var tenant = NaoNulo(
            await _db.Tenants.FirstOrDefaultAsync(t => t.Id == TenantId, ct),
            "Empresa não encontrada.");

        tenant.LimiteDiarioDeAtendimentos = req.LimiteDoDia;
        tenant.LimiteDiarioPorPessoa = req.LimitePorPessoa;
        await _db.SaveChangesAsync(ct);

        var hoje = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        return Ok(Montar(req.LimiteDoDia, req.LimitePorPessoa, await ContarDoDiaAsync(hoje, ct)));
    }

    private Task<int> ContarDoDiaAsync(DateOnly dia, CancellationToken ct)
    {
        var inicio = new DateTimeOffset(dia.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var fim = inicio.AddDays(1);
        return _db.Agendamentos.CountAsync(
            a => a.Inicio < fim && a.Fim > inicio && a.Status != StatusAgendamento.Cancelado, ct);
    }

    private static LimiteDiarioDto Montar(int doDia, int porPessoa, int usados) => new(
        doDia, porPessoa, usados, doDia > 0, porPessoa > 0,
        doDia > 0
            ? $"{usados} de {doDia} atendimento(s) hoje."
            : $"{usados} atendimento(s) hoje, sem teto.");

    // ------------------------------------------------------------------- turnos
    /// <summary>
    /// Os turnos da escala. Existem para não redigitar o mesmo horário em cada pessoa e
    /// cada dia — mudar o turno muda a escala inteira de quem o segue.
    /// </summary>
    [HttpGet("turnos")]
    [RequerPermissao("horarios.ver")]
    public async Task<ActionResult<IReadOnlyList<TurnoDto>>> Turnos(
        [FromQuery] bool incluirInativos = false, CancellationToken ct = default)
    {
        var turnos = await _db.Turnos.AsNoTracking()
            .Where(t => incluirInativos || t.Ativo)
            .OrderBy(t => t.Inicio).ThenBy(t => t.Nome)
            .ToListAsync(ct);

        // Quantas linhas de escala usam cada um: é o que impede apagar sem saber o custo.
        var emUso = await _db.HorariosStaff.AsNoTracking()
            .Where(h => h.TurnoId != null)
            .GroupBy(h => h.TurnoId!.Value)
            .Select(g => new { TurnoId = g.Key, Quantas = g.Count() })
            .ToListAsync(ct);

        return Ok(turnos
            .Select(t => t.ParaDto(emUso.FirstOrDefault(u => u.TurnoId == t.Id)?.Quantas ?? 0))
            .ToList());
    }

    [HttpPost("turnos")]
    [RequerPermissao("horarios.editar")]
    public async Task<ActionResult<TurnoDto>> CriarTurno(TurnoRequest req, CancellationToken ct)
    {
        var turno = new Turno { Nome = string.Empty, Inicio = req.Inicio, Fim = req.Fim };
        await AplicarAsync(turno, req, ct);

        _db.Turnos.Add(turno);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Turnos), new { }, turno.ParaDto());
    }

    [HttpPut("turnos/{id:long}")]
    [RequerPermissao("horarios.editar")]
    public async Task<ActionResult<TurnoDto>> SalvarTurno(
        long id, TurnoRequest req, CancellationToken ct)
    {
        var turno = NaoNulo(
            await _db.Turnos.FirstOrDefaultAsync(t => t.Id == id, ct), "Turno não encontrado.");

        await AplicarAsync(turno, req, ct, id);
        await _db.SaveChangesAsync(ct);

        var quantas = await _db.HorariosStaff.CountAsync(h => h.TurnoId == id, ct);
        return Ok(turno.ParaDto(quantas));
    }

    /// <summary>
    /// Turno em uso é desativado, não apagado: apagar soltaria a escala de quem o segue
    /// para o horário antigo guardado na linha, sem ninguém pedir.
    /// </summary>
    [HttpDelete("turnos/{id:long}")]
    [RequerPermissao("horarios.editar")]
    public async Task<ActionResult<TurnoDto>> ExcluirTurno(long id, CancellationToken ct)
    {
        var turno = NaoNulo(
            await _db.Turnos.FirstOrDefaultAsync(t => t.Id == id, ct), "Turno não encontrado.");

        var quantas = await _db.HorariosStaff.CountAsync(h => h.TurnoId == id, ct);
        if (quantas > 0)
        {
            turno.Ativo = false;
            await _db.SaveChangesAsync(ct);
            return Ok(turno.ParaDto(quantas));
        }

        turno.Excluido = true;
        turno.ExcluidoEm = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task AplicarAsync(
        Turno turno, TurnoRequest req, CancellationToken ct, long? ignorarId = null)
    {
        var nome = (req.Nome ?? string.Empty).Trim();
        if (nome.Length < 2)
        {
            throw new RegraDeNegocioException("O turno precisa de um nome.", "NOME");
        }

        if (req.Inicio >= req.Fim)
        {
            throw new RegraDeNegocioException(
                "O início do turno precisa vir antes do fim.", "JANELA_INVALIDA");
        }

        if (req.PausaInicio is { } pi && req.PausaFim is { } pf)
        {
            if (pi >= pf)
            {
                throw new RegraDeNegocioException(
                    "O início da pausa precisa vir antes do fim.", "PAUSA_INVALIDA");
            }

            // Pausa fora do turno não pausa nada: só deixaria a tela mostrando um
            // intervalo que a agenda ignora.
            if (pi < req.Inicio || pf > req.Fim)
            {
                throw new RegraDeNegocioException(
                    "A pausa precisa ficar dentro do turno.", "PAUSA_FORA");
            }
        }

        var repetido = await _db.Turnos
            .AnyAsync(t => t.Nome.ToLower() == nome.ToLower() && t.Id != (ignorarId ?? 0), ct);
        if (repetido)
        {
            throw new RegraDeNegocioException("Já existe um turno com esse nome.", "NOME_REPETIDO");
        }

        turno.Nome = nome;
        turno.Inicio = req.Inicio;
        turno.Fim = req.Fim;
        turno.PausaInicio = req.PausaInicio;
        turno.PausaFim = req.PausaFim;
        turno.Cor = string.IsNullOrWhiteSpace(req.Cor) ? null : req.Cor.Trim();
        turno.Ativo = req.Ativo;
    }

    // ------------------------------------------------------- funcionamento da empresa
    [HttpGet("funcionamento")]
    [RequerPermissao("horarios.ver")]
    public async Task<ActionResult<IReadOnlyList<HorarioFuncionamentoDto>>> Funcionamento(CancellationToken ct)
    {
        var horarios = await _db.HorariosFuncionamento
            .AsNoTracking().OrderBy(h => h.DiaDaSemana).ToListAsync(ct);

        return Ok(horarios.Select(h => h.ParaDto()).ToList());
    }

    /// <summary>
    /// Grava a semana inteira de uma vez: é assim que a tela funciona, e evita deixar a
    /// empresa com dias sem configuração.
    /// </summary>
    [HttpPut("funcionamento")]
    [RequerPermissao("horarios.editar")]
    public async Task<ActionResult<IReadOnlyList<HorarioFuncionamentoDto>>> SalvarFuncionamento(
        IReadOnlyList<HorarioFuncionamentoRequest> req, CancellationToken ct)
    {
        foreach (var dia in req)
        {
            ValidarJanela(dia.Aberto, dia.Abertura, dia.Fechamento, dia.PausaInicio, dia.PausaFim);
        }

        var atuais = await _db.HorariosFuncionamento.ToListAsync(ct);
        foreach (var pedido in req)
        {
            var horario = atuais.FirstOrDefault(h => h.DiaDaSemana == pedido.DiaDaSemana);
            if (horario is null)
            {
                horario = new HorarioFuncionamento { DiaDaSemana = pedido.DiaDaSemana };
                _db.HorariosFuncionamento.Add(horario);
            }

            horario.Aberto = pedido.Aberto;
            horario.Abertura = pedido.Aberto ? pedido.Abertura : null;
            horario.Fechamento = pedido.Aberto ? pedido.Fechamento : null;
            horario.PausaInicio = pedido.Aberto ? pedido.PausaInicio : null;
            horario.PausaFim = pedido.Aberto ? pedido.PausaFim : null;
            horario.TipoDia = pedido.TipoDia;
            horario.IntervaloSlotMinutos = pedido.IntervaloSlotMinutos is >= 5 and <= 240
                ? pedido.IntervaloSlotMinutos : 30;
        }

        await _db.SaveChangesAsync(ct);
        return await Funcionamento(ct);
    }

    // --------------------------------------------------------------- exceções da empresa
    [HttpGet("funcionamento/excecoes")]
    [RequerPermissao("horarios.ver")]
    public async Task<ActionResult<IReadOnlyList<ExcecaoFuncionamentoDto>>> Excecoes(
        [FromQuery] DateOnly? de, [FromQuery] DateOnly? ate, CancellationToken ct = default)
    {
        var consulta = _db.ExcecoesHorarioFuncionamento.AsNoTracking().AsQueryable();
        if (de is { } inicio)
        {
            consulta = consulta.Where(e => e.Data >= inicio);
        }
        if (ate is { } fim)
        {
            consulta = consulta.Where(e => e.Data <= fim);
        }

        var excecoes = await consulta.OrderBy(e => e.Data).ToListAsync(ct);
        return Ok(excecoes.Select(e => e.ParaDto()).ToList());
    }

    [HttpPost("funcionamento/excecoes")]
    [RequerPermissao("horarios.editar")]
    public async Task<ActionResult<ExcecaoFuncionamentoDto>> CriarExcecao(
        ExcecaoFuncionamentoRequest req, CancellationToken ct)
    {
        ValidarJanela(!req.Fechado, req.Abertura, req.Fechamento, req.PausaInicio, req.PausaFim);

        if (await _db.ExcecoesHorarioFuncionamento.AnyAsync(e => e.Data == req.Data, ct))
        {
            throw new RegraDeNegocioException(
                $"Já existe uma exceção para {req.Data:dd/MM/yyyy}.", "EXCECAO_DUPLICADA");
        }

        var excecao = new ExcecaoHorarioFuncionamento
        {
            Data = req.Data,
            Fechado = req.Fechado,
            Abertura = req.Abertura,
            Fechamento = req.Fechamento,
            PausaInicio = req.PausaInicio,
            PausaFim = req.PausaFim,
            Motivo = req.Motivo,
        };

        _db.ExcecoesHorarioFuncionamento.Add(excecao);
        await _db.SaveChangesAsync(ct);
        return Ok(excecao.ParaDto());
    }

    [HttpDelete("funcionamento/excecoes/{id:long}")]
    [RequerPermissao("horarios.editar")]
    public async Task<IActionResult> RemoverExcecao(long id, CancellationToken ct)
    {
        var excecao = NaoNulo(
            await _db.ExcecoesHorarioFuncionamento.FirstOrDefaultAsync(e => e.Id == id, ct),
            "Exceção não encontrada.");

        _db.ExcecoesHorarioFuncionamento.Remove(excecao);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // -------------------------------------------------------------- jornada do time
    [HttpGet("staff")]
    [RequerPermissao("horarios.ver")]
    public async Task<ActionResult<IReadOnlyList<HorarioStaffDto>>> Staff(
        [FromQuery] long? usuarioId, CancellationToken ct = default)
    {
        var horarios = await _db.HorariosStaff
            .AsNoTracking()
            .Include(h => h.Usuario)
            // O turno vem junto: sem ele, a janela efetiva cairia no valor da linha.
            .Include(h => h.Turno)
            .Where(h => usuarioId == null || h.UsuarioId == usuarioId)
            .OrderBy(h => h.UsuarioId).ThenBy(h => h.DiaDaSemana)
            .ToListAsync(ct);

        return Ok(horarios.Select(h => h.ParaDto()).ToList());
    }

    /// <summary>Grava a semana inteira de uma pessoa do time.</summary>
    [HttpPut("staff/{usuarioId:long}")]
    [RequerPermissao("horarios.editar")]
    [RequerRecurso(CatalogoRecursos.JornadaPorPessoa)]
    public async Task<ActionResult<IReadOnlyList<HorarioStaffDto>>> SalvarStaff(
        long usuarioId, IReadOnlyList<HorarioStaffRequest> req, CancellationToken ct)
    {
        var usuario = NaoNulo(
            await _db.Usuarios.FirstOrDefaultAsync(u => u.Id == usuarioId, ct),
            "Usuário não encontrado.");

        // O funcionamento da empresa manda: a agenda já usa a interseção das duas
        // janelas, então salvar jornada fora dela guardaria hora que nunca vira encaixe
        // — e a tela mostraria uma escala que a agenda não cumpre.
        var funcionamento = await _db.HorariosFuncionamento.AsNoTracking().ToListAsync(ct);
        var turnos = await _db.Turnos.AsNoTracking().Where(t => t.Ativo).ToListAsync(ct);

        foreach (var dia in req)
        {
            if (!dia.Trabalha)
            {
                continue;
            }

            var turno = dia.TurnoId is { } turnoId
                ? turnos.FirstOrDefault(t => t.Id == turnoId)
                  ?? throw new RegraDeNegocioException(
                      "Turno não encontrado ou inativo.", "TURNO_INVALIDO")
                : null;

            // Com turno, os horários vêm dele; sem turno, da janela livre que veio no
            // pedido. É a mesma conta que a agenda faz depois.
            ValidarCabeNoFuncionamento(
                dia.DiaDaSemana, turno?.Inicio ?? dia.Inicio, turno?.Fim ?? dia.Fim, funcionamento);
        }

        var atuais = await _db.HorariosStaff.Where(h => h.UsuarioId == usuario.Id).ToListAsync(ct);
        foreach (var pedido in req)
        {
            var horario = atuais.FirstOrDefault(h => h.DiaDaSemana == pedido.DiaDaSemana);
            if (horario is null)
            {
                horario = new HorarioStaff { UsuarioId = usuario.Id, DiaDaSemana = pedido.DiaDaSemana };
                _db.HorariosStaff.Add(horario);
            }

            // Com turno, a linha não copia os minutos: guardá-los aqui deixaria a escala
            // desatualizada no dia em que o turno mudasse de horário.
            horario.TurnoId = pedido.TurnoId;
            horario.Inicio = pedido.Inicio;
            horario.Fim = pedido.Fim;
            horario.PausaInicio = pedido.PausaInicio;
            horario.PausaFim = pedido.PausaFim;
            horario.Trabalha = pedido.Trabalha;
        }

        await _db.SaveChangesAsync(ct);
        return await Staff(usuario.Id, ct);
    }

    // ------------------------------------------------------------ ausências do time
    [HttpGet("staff/ausencias")]
    [RequerPermissao("horarios.ver")]
    public async Task<ActionResult<IReadOnlyList<AusenciaStaffDto>>> Ausencias(
        [FromQuery] long? usuarioId, [FromQuery] DateOnly? de, [FromQuery] DateOnly? ate,
        CancellationToken ct = default)
    {
        var consulta = _db.ExcecoesHorarioStaff.AsNoTracking().Include(e => e.Usuario).AsQueryable();
        if (usuarioId is { } id)
        {
            consulta = consulta.Where(e => e.UsuarioId == id);
        }
        if (de is { } inicio)
        {
            consulta = consulta.Where(e => e.Data >= inicio);
        }
        if (ate is { } fim)
        {
            consulta = consulta.Where(e => e.Data <= fim);
        }

        var ausencias = await consulta.OrderBy(e => e.Data).ToListAsync(ct);
        return Ok(ausencias.Select(a => a.ParaDto()).ToList());
    }

    [HttpPost("staff/ausencias")]
    [RequerPermissao("horarios.editar")]
    [RequerRecurso(CatalogoRecursos.JornadaPorPessoa)]
    public async Task<ActionResult<AusenciaStaffDto>> CriarAusencia(
        AusenciaStaffRequest req, CancellationToken ct)
    {
        var usuario = NaoNulo(
            await _db.Usuarios.FirstOrDefaultAsync(u => u.Id == req.UsuarioId, ct),
            "Usuário não encontrado.");

        if (!req.DiaInteiro && (req.Inicio is null || req.Fim is null || req.Inicio >= req.Fim))
        {
            throw new RegraDeNegocioException(
                "Ausência parcial precisa de início e fim válidos.", "JANELA_INVALIDA");
        }

        var ausencia = new ExcecaoHorarioStaff
        {
            UsuarioId = usuario.Id,
            Data = req.Data,
            DiaInteiro = req.DiaInteiro,
            Inicio = req.DiaInteiro ? null : req.Inicio,
            Fim = req.DiaInteiro ? null : req.Fim,
            Motivo = req.Motivo,
        };

        _db.ExcecoesHorarioStaff.Add(ausencia);
        await _db.SaveChangesAsync(ct);

        ausencia.Usuario = usuario;
        return Ok(ausencia.ParaDto());
    }

    [HttpDelete("staff/ausencias/{id:long}")]
    [RequerPermissao("horarios.editar")]
    [RequerRecurso(CatalogoRecursos.JornadaPorPessoa)]
    public async Task<IActionResult> RemoverAusencia(long id, CancellationToken ct)
    {
        var ausencia = NaoNulo(
            await _db.ExcecoesHorarioStaff.FirstOrDefaultAsync(e => e.Id == id, ct),
            "Ausência não encontrada.");

        _db.ExcecoesHorarioStaff.Remove(ausencia);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static void ValidarJanela(
        bool aberto, TimeOnly? abertura, TimeOnly? fechamento, TimeOnly? pausaInicio, TimeOnly? pausaFim)
    {
        if (!aberto)
        {
            return;
        }

        if (abertura is null || fechamento is null || abertura >= fechamento)
        {
            throw new RegraDeNegocioException(
                "A abertura precisa vir antes do fechamento.", "JANELA_INVALIDA");
        }

        if (pausaInicio is not null || pausaFim is not null)
        {
            if (pausaInicio is null || pausaFim is null || pausaInicio >= pausaFim)
            {
                throw new RegraDeNegocioException(
                    "A pausa precisa ter início e fim válidos.", "PAUSA_INVALIDA");
            }

            if (pausaInicio < abertura || pausaFim > fechamento)
            {
                throw new RegraDeNegocioException(
                    "A pausa precisa ficar dentro do horário de funcionamento.", "PAUSA_FORA_DA_JANELA");
            }
        }
    }
}

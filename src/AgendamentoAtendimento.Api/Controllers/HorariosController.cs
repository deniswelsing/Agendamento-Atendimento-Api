using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
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
            .Where(h => usuarioId == null || h.UsuarioId == usuarioId)
            .OrderBy(h => h.UsuarioId).ThenBy(h => h.DiaDaSemana)
            .ToListAsync(ct);

        return Ok(horarios.Select(h => h.ParaDto()).ToList());
    }

    /// <summary>Grava a semana inteira de uma pessoa do time.</summary>
    [HttpPut("staff/{usuarioId:long}")]
    [RequerPermissao("horarios.editar")]
    public async Task<ActionResult<IReadOnlyList<HorarioStaffDto>>> SalvarStaff(
        long usuarioId, IReadOnlyList<HorarioStaffRequest> req, CancellationToken ct)
    {
        var usuario = NaoNulo(
            await _db.Usuarios.FirstOrDefaultAsync(u => u.Id == usuarioId, ct),
            "Usuário não encontrado.");

        foreach (var dia in req)
        {
            if (dia.Trabalha && dia.Inicio >= dia.Fim)
            {
                throw new RegraDeNegocioException(
                    "O início da jornada precisa vir antes do fim.", "JANELA_INVALIDA");
            }
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

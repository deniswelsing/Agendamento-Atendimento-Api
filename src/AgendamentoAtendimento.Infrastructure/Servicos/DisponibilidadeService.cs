using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Infrastructure.Servicos;

/// <summary>Encaixe livre devolvido para o app. O app não calcula nada: só exibe.</summary>
public sealed record SlotDisponivel(
    DateTimeOffset Inicio,
    DateTimeOffset Fim,
    long ResponsavelId,
    string ResponsavelNome);

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
    int TotalAgendamentos);

/// <summary>
/// Motor de disponibilidade: interseção entre a janela da empresa e a jornada de cada
/// atendente, menos as pausas, as exceções e o que já está agendado.
///
/// Mora no servidor de propósito — é regra de negócio, e o app só desenha o resultado.
/// </summary>
public class DisponibilidadeService
{
    private readonly AppDbContext _db;

    public DisponibilidadeService(AppDbContext db) => _db = db;

    public async Task<DiaDaAgenda> ObterDiaAsync(
        DateOnly data,
        int duracaoMinutos,
        long? responsavelId = null,
        CancellationToken ct = default)
    {
        var diaDaSemana = data.DayOfWeek;

        var horarioEmpresa = await _db.HorariosFuncionamento
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.DiaDaSemana == diaDaSemana, ct);

        var excecaoEmpresa = await _db.ExcecoesHorarioFuncionamento
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Data == data, ct);

        var agendamentosDoDia = await AgendamentosDoDiaAsync(data, ct);

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

        var duracao = duracaoMinutos > 0 ? duracaoMinutos : intervalo;
        var atendentes = await AtendentesAsync(responsavelId, ct);
        var jornadas = await JornadasAsync(diaDaSemana, ct);
        var ausencias = await AusenciasAsync(data, ct);

        var livres = new List<SlotDisponivel>();
        foreach (var atendente in atendentes)
        {
            var jornada = jornadas.FirstOrDefault(j => j.UsuarioId == atendente.Id);
            if (jornada is null || !jornada.Trabalha)
            {
                continue;
            }

            // A agenda vale pela interseção da jornada com a janela da empresa.
            var inicioJanela = Maior(abertura.Value, jornada.Inicio);
            var fimJanela = Menor(fechamento.Value, jornada.Fim);
            if (inicioJanela >= fimJanela)
            {
                continue;
            }

            var ocupados = agendamentosDoDia
                .Where(a => a.ResponsavelId == atendente.Id)
                .Select(a => (a.Inicio, a.Fim))
                .ToList();

            foreach (var ausencia in ausencias.Where(a => a.UsuarioId == atendente.Id))
            {
                var iniAusencia = ausencia.DiaInteiro ? TimeOnly.MinValue : ausencia.Inicio ?? TimeOnly.MinValue;
                var fimAusencia = ausencia.DiaInteiro ? TimeOnly.MaxValue : ausencia.Fim ?? TimeOnly.MaxValue;
                ocupados.Add((Combinar(data, iniAusencia), Combinar(data, fimAusencia)));
            }

            for (var t = inicioJanela; AdicionarMinutos(t, duracao) <= fimJanela; t = AdicionarMinutos(t, intervalo))
            {
                var fimSlot = AdicionarMinutos(t, duracao);
                if (ColideComPausa(t, fimSlot, pausaInicio, pausaFim) ||
                    ColideComPausa(t, fimSlot, jornada.PausaInicio, jornada.PausaFim))
                {
                    continue;
                }

                var inicioAbsoluto = Combinar(data, t);
                var fimAbsoluto = Combinar(data, fimSlot);
                if (ocupados.Any(o => inicioAbsoluto < o.Item2 && fimAbsoluto > o.Item1))
                {
                    continue;
                }

                livres.Add(new SlotDisponivel(inicioAbsoluto, fimAbsoluto, atendente.Id, atendente.Nome));
            }
        }

        var ordenados = livres
            .OrderBy(s => s.Inicio)
            .ThenBy(s => s.ResponsavelNome)
            .ToList();

        return new DiaDaAgenda(
            data, true, abertura, fechamento, pausaInicio, pausaFim, null,
            intervalo, ordenados, agendamentosDoDia.Count);
    }

    /// <summary>Resumo por dia usado pelas visões de semana e de mês.</summary>
    public async Task<IReadOnlyList<DiaDaAgenda>> ObterPeriodoAsync(
        DateOnly de,
        DateOnly ate,
        int duracaoMinutos,
        long? responsavelId = null,
        CancellationToken ct = default)
    {
        if (ate < de)
        {
            (de, ate) = (ate, de);
        }

        var dias = new List<DiaDaAgenda>();
        for (var data = de; data <= ate; data = data.AddDays(1))
        {
            dias.Add(await ObterDiaAsync(data, duracaoMinutos, responsavelId, ct));
        }
        return dias;
    }

    /// <summary>Valida se o horário pedido cabe mesmo — chamado antes de gravar.</summary>
    public async Task<bool> EstaLivreAsync(
        DateTimeOffset inicio, DateTimeOffset fim, long responsavelId,
        long? ignorarAgendamentoId = null, CancellationToken ct = default)
    {
        var data = DateOnly.FromDateTime(inicio.UtcDateTime);
        var dia = await ObterDiaAsync(data, (int)(fim - inicio).TotalMinutes, responsavelId, ct);
        if (!dia.Aberto)
        {
            return false;
        }

        var conflita = await _db.Agendamentos
            .AsNoTracking()
            .AnyAsync(a =>
                a.ResponsavelId == responsavelId &&
                a.Status != StatusAgendamento.Cancelado &&
                a.Id != (ignorarAgendamentoId ?? 0) &&
                a.Inicio < fim && a.Fim > inicio, ct);

        return !conflita && dia.Livres.Any(s => s.Inicio == inicio && s.ResponsavelId == responsavelId);
    }

    private async Task<List<Agendamento>> AgendamentosDoDiaAsync(DateOnly data, CancellationToken ct)
    {
        var inicioDia = Combinar(data, TimeOnly.MinValue);
        var fimDia = inicioDia.AddDays(1);
        return await _db.Agendamentos
            .AsNoTracking()
            .Where(a => a.Inicio < fimDia && a.Fim > inicioDia && a.Status != StatusAgendamento.Cancelado)
            .ToListAsync(ct);
    }

    private async Task<List<Usuario>> AtendentesAsync(long? responsavelId, CancellationToken ct) =>
        await _db.Usuarios
            .AsNoTracking()
            .Where(u => u.Ativo && u.Atendente && !u.ConvitePendente)
            .Where(u => responsavelId == null || u.Id == responsavelId)
            .OrderBy(u => u.Nome)
            .ToListAsync(ct);

    private async Task<List<HorarioStaff>> JornadasAsync(DayOfWeek dia, CancellationToken ct) =>
        await _db.HorariosStaff.AsNoTracking().Where(h => h.DiaDaSemana == dia).ToListAsync(ct);

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

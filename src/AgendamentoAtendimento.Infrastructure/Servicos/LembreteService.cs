using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgendamentoAtendimento.Infrastructure.Servicos;

/// <summary>O texto que sai para o cliente, já montado.</summary>
public sealed record MensagemDeLembrete(
    string Destino,
    string Assunto,
    string Corpo,
    /// <summary>Endereço de confirmação, quando a empresa pede confirmação.</summary>
    string? LinkDeConfirmacao);

/// <summary>
/// Quem de fato entrega. É interface porque o canal real (SMTP, provedor de e-mail) é
/// configuração de quem hospeda — e porque fingir que mandou seria pior que não mandar.
/// </summary>
public interface IEnviadorDeLembrete
{
    Task<(bool Entregue, string? Erro)> EnviarAsync(MensagemDeLembrete mensagem, CancellationToken ct);
}

/// <summary>
/// A implementação padrão: registra no log e dá por entregue. Não há SMTP configurado
/// nesta instalação, e é melhor a fila mostrar "enviado por log" do que a tela dizer que
/// um e-mail saiu quando nenhum saiu.
/// </summary>
public class EnviadorDeLembreteEmLog : IEnviadorDeLembrete
{
    private readonly ILogger<EnviadorDeLembreteEmLog> _log;

    public EnviadorDeLembreteEmLog(ILogger<EnviadorDeLembreteEmLog> log) => _log = log;

    public Task<(bool Entregue, string? Erro)> EnviarAsync(
        MensagemDeLembrete mensagem, CancellationToken ct)
    {
        _log.LogInformation(
            "Lembrete para {Destino}: {Assunto} — {Corpo} {Link}",
            mensagem.Destino, mensagem.Assunto, mensagem.Corpo, mensagem.LinkDeConfirmacao);

        return Task.FromResult((true, (string?)null));
    }
}

/// <summary>
/// A fila de avisos ao cliente. Guardar o que já saiu é o ponto: sem isso, remarcar um
/// atendimento mandaria o mesmo lembrete de novo, e ninguém saberia dizer se o cliente
/// foi avisado.
/// </summary>
public class LembreteService
{
    private readonly AppDbContext _db;
    private readonly IEnviadorDeLembrete _enviador;

    public LembreteService(AppDbContext db, IEnviadorDeLembrete enviador)
    {
        _db = db;
        _enviador = enviador;
    }

    /// <summary>A configuração da empresa, ou o padrão desligado quando não há nenhuma.</summary>
    public async Task<ConfiguracaoDeLembrete> ConfiguracaoAsync(CancellationToken ct) =>
        await _db.ConfiguracoesDeLembrete.AsNoTracking().FirstOrDefaultAsync(ct)
        ?? new ConfiguracaoDeLembrete();

    /// <summary>
    /// Refaz a fila deste agendamento. Chamado ao criar e ao remarcar: o lembrete antigo
    /// aponta para uma hora que não existe mais, então ele morre e outro nasce.
    /// </summary>
    public async Task ReprogramarAsync(long agendamentoId, DateTimeOffset agora, CancellationToken ct)
    {
        var config = await ConfiguracaoAsync(ct);

        var agendamento = await _db.Agendamentos
            .Include(a => a.Cliente)
            .FirstOrDefaultAsync(a => a.Id == agendamentoId, ct);

        if (agendamento is null)
        {
            return;
        }

        await CancelarPendentesAsync(agendamentoId, ct);

        if (!config.Ativo || !PodeReceber(agendamento))
        {
            return;
        }

        // Cancelado ou já encerrado não tem o que lembrar.
        if (agendamento.Status is StatusAgendamento.Cancelado or StatusAgendamento.Concluido
            or StatusAgendamento.NaoCompareceu)
        {
            return;
        }

        var destino = agendamento.Cliente!.Email!;

        // O código é o que deixa o cliente confirmar sem ter conta. Um agendamento feito
        // por dentro ainda não tem; é aqui que ele ganha o seu.
        if (string.IsNullOrWhiteSpace(agendamento.CodigoPublico))
        {
            agendamento.CodigoPublico = CodigoDeAcesso.Gerar();
        }

        if (config.AvisarAoMarcar)
        {
            _db.Lembretes.Add(new LembreteDeAgendamento
            {
                AgendamentoId = agendamento.Id,
                Tipo = TipoDeLembrete.Confirmacao,
                Canal = config.Canal,
                // Sai na primeira varredura: o cliente acabou de marcar.
                QuandoEnviar = agora,
                Destino = destino,
            });
        }

        var horaDoLembrete = agendamento.Inicio.AddHours(-config.HorasDeAntecedencia);

        // Marcar para daqui a uma hora com lembrete de 24h não gera um lembrete no
        // passado: ele simplesmente não existe, e o aviso de "está marcado" já cobre.
        if (horaDoLembrete > agora)
        {
            _db.Lembretes.Add(new LembreteDeAgendamento
            {
                AgendamentoId = agendamento.Id,
                Tipo = TipoDeLembrete.Lembrete,
                Canal = config.Canal,
                QuandoEnviar = horaDoLembrete,
                Destino = destino,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Mata o que ainda não saiu. O que já saiu fica: é histórico, e apagar deixaria a
    /// tela dizendo que o cliente nunca foi avisado.
    /// </summary>
    public async Task CancelarPendentesAsync(long agendamentoId, CancellationToken ct)
    {
        var pendentes = await _db.Lembretes
            .Where(l => l.AgendamentoId == agendamentoId && l.Status == StatusDeLembrete.Pendente)
            .ToListAsync(ct);

        foreach (var lembrete in pendentes)
        {
            lembrete.Status = StatusDeLembrete.Cancelado;
        }

        if (pendentes.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Manda o que venceu. Devolve quantos saíram e quantos falharam — a tela mostra os
    /// dois números, porque "despachou" sem dizer quantos falharam não é informação.
    /// </summary>
    public async Task<(int Enviados, int Falharam, int Expirados)> DespacharAsync(
        DateTimeOffset agora, CancellationToken ct)
    {
        var config = await ConfiguracaoAsync(ct);

        var vencidos = await _db.Lembretes
            .Include(l => l.Agendamento).ThenInclude(a => a!.Cliente)
            .Where(l => l.Status == StatusDeLembrete.Pendente && l.QuandoEnviar <= agora)
            .OrderBy(l => l.QuandoEnviar)
            .ToListAsync(ct);

        var enviados = 0;
        var falharam = 0;
        var expirados = 0;

        foreach (var lembrete in vencidos)
        {
            var agendamento = lembrete.Agendamento;

            // O agendamento pode ter sido cancelado entre a fila e a varredura.
            if (agendamento is null || agendamento.Status == StatusAgendamento.Cancelado)
            {
                lembrete.Status = StatusDeLembrete.Cancelado;
                continue;
            }

            // Um lembrete de "amanhã às 9h" entregue depois das 9h não é lembrete, é
            // confusão. Passou da tolerância, ele morre em vez de sair atrasado.
            var atraso = agora - lembrete.QuandoEnviar;
            if (atraso > TimeSpan.FromMinutes(config.ToleranciaDeAtrasoMinutos))
            {
                lembrete.Status = StatusDeLembrete.Cancelado;
                lembrete.Erro = "Passou da tolerância de atraso e não foi enviado.";
                expirados++;
                continue;
            }

            lembrete.Tentativas++;

            var (entregue, erro) = await _enviador.EnviarAsync(
                Montar(lembrete, agendamento, config), ct);

            if (entregue)
            {
                lembrete.Status = StatusDeLembrete.Enviado;
                lembrete.EnviadoEm = agora;
                lembrete.Erro = null;
                enviados++;
            }
            else
            {
                lembrete.Status = StatusDeLembrete.Falhou;
                lembrete.Erro = erro ?? "O canal recusou a mensagem.";
                falharam++;
            }
        }

        if (vencidos.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return (enviados, falharam, expirados);
    }

    /// <summary>
    /// Só recebe quem deu e-mail e não pediu para parar. `AceitaEmail` é escolha do
    /// cliente: ignorá-la aqui transformaria a configuração da empresa em permissão para
    /// escrever para quem não quer.
    /// </summary>
    private static bool PodeReceber(Agendamento agendamento) =>
        agendamento.Cliente is { AceitaEmail: true, Email: { } email }
        && !string.IsNullOrWhiteSpace(email);

    private static MensagemDeLembrete Montar(
        LembreteDeAgendamento lembrete,
        Agendamento agendamento,
        ConfiguracaoDeLembrete config)
    {
        var quando = agendamento.Inicio.ToString("dd/MM 'às' HH:mm");
        var nome = agendamento.Cliente?.Nome ?? "Olá";

        var assunto = lembrete.Tipo == TipoDeLembrete.Confirmacao
            ? $"Seu atendimento está marcado para {quando}"
            : $"Lembrete: seu atendimento é {quando}";

        var corpo = lembrete.Tipo == TipoDeLembrete.Confirmacao
            ? $"{nome}, seu atendimento ficou marcado para {quando}."
            : $"{nome}, passando para lembrar do seu atendimento em {quando}.";

        // Sem confirmação pedida, não há link: mandar um que ninguém vai usar só dá ao
        // cliente um botão que não muda nada.
        var link = config.PedirConfirmacao && agendamento.CodigoPublico is { } codigo
            ? $"/agendamento/{codigo}/confirmar"
            : null;

        if (link is not null)
        {
            corpo += " Confirme sua presença pelo link.";
        }

        return new MensagemDeLembrete(lembrete.Destino, assunto, corpo, link);
    }
}

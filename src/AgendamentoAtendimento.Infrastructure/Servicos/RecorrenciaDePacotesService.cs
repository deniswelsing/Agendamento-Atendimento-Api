using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Pacotes;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Infrastructure.Servicos;

/// <summary>Um pacote que está para vencer, como o aviso diário o descreve.</summary>
public sealed record AvisoDeRenovacao(
    long PacoteId,
    string Nome,
    int Ciclo,
    DateOnly Vence,
    int DiasAteVencer,
    int Clientes,
    RecorrenciaDePacote Recorrencia,
    string Texto);

/// <summary>O que a varredura do dia fez.</summary>
public sealed record ResultadoDaVarredura(
    IReadOnlyList<AvisoDeRenovacao> Avisos,
    int CiclosEncerrados,
    int CiclosAbertos,
    int PacotesEncerrados,
    int EstornosGerados,
    decimal ValorEstornado);

/// <summary>
/// A varredura diária dos pacotes. Faz duas coisas, nesta ordem:
///
/// 1. AVISA o que vence hoje ou nos próximos sete dias. Uma renovação que chega sem
///    aviso é uma cobrança surpresa — e quem atende precisa de tempo para falar com o
///    cliente antes, não depois.
/// 2. VIRA o ciclo do que já venceu: fecha o saldo de cada cliente, desce o que sobrou
///    como crédito quando há recorrência, e estorna quando não há.
///
/// Roda por tenant, e é idempotente: avisar duas vezes no mesmo dia não acontece porque
/// o pacote guarda de qual ciclo foi o último aviso, e fechar um ciclo já fechado é
/// operação vazia.
/// </summary>
public class RecorrenciaDePacotesService
{
    /// <summary>Quantos dias antes o time é avisado. Sete, como pedido.</summary>
    public const int DiasDeAviso = 7;

    private readonly AppDbContext _db;

    public RecorrenciaDePacotesService(AppDbContext db) => _db = db;

    public async Task<ResultadoDaVarredura> VarrerAsync(
        DateOnly hoje, CancellationToken ct = default)
    {
        var limite = hoje.AddDays(DiasDeAviso);

        var pacotes = await _db.Pacotes
            .Where(p => p.Status == StatusDePacote.Ativo && p.FimDoCicloAtual <= limite)
            .ToListAsync(ct);

        var avisos = new List<AvisoDeRenovacao>();
        var encerrados = 0;
        var abertos = 0;
        var pacotesEncerrados = 0;
        var estornos = 0;
        var valorEstornado = 0m;

        foreach (var pacote in pacotes)
        {
            var dias = pacote.DiasAteVencer(hoje);

            if (dias >= 0)
            {
                var aviso = await AvisarAsync(pacote, hoje, dias, ct);
                if (aviso is not null)
                {
                    avisos.Add(aviso);
                }

                continue;
            }

            // Venceu: hora de virar o ciclo.
            var (fechados, novos, comEstorno, valor) = await VirarCicloAsync(pacote, hoje, ct);
            encerrados += fechados;
            abertos += novos;
            estornos += comEstorno;
            valorEstornado += valor;
            if (!pacote.EhRecorrente)
            {
                pacotesEncerrados++;
            }
        }

        await _db.SaveChangesAsync(ct);

        return new ResultadoDaVarredura(
            avisos, encerrados, abertos, pacotesEncerrados, estornos, valorEstornado);
    }


    /// <summary>
    /// Tira um cliente do pacote agora, sem esperar o ciclo virar. É o "se ele não puder
    /// ir mesmo": cancela o que estava marcado à frente e acerta o que sobrou.
    ///
    /// Com recorrência à frente o saldo desce como crédito — ele volta no próximo ciclo.
    /// Sem ela, vira estorno na hora, contado da data de hoje: cancelou hoje e ainda
    /// faltavam duas sessões, então são duas que voltam.
    /// </summary>
    public async Task<CicloDoCliente?> EncerrarVinculoAsync(
        long pacoteClienteId, DateOnly hoje, CancellationToken ct = default)
    {
        var vinculo = await _db.PacoteClientes
            .FirstOrDefaultAsync(c => c.Id == pacoteClienteId, ct);
        if (vinculo is null || !vinculo.Ativo)
        {
            return null;
        }

        var pacote = await _db.Pacotes.FirstOrDefaultAsync(p => p.Id == vinculo.PacoteId, ct);
        if (pacote is null)
        {
            return null;
        }

        var ciclo = await _db.CiclosDePacote
            .Where(c => c.PacoteClienteId == vinculo.Id && !c.Encerrado)
            .OrderByDescending(c => c.Ciclo)
            .FirstOrDefaultAsync(ct);
        if (ciclo is null)
        {
            vinculo.Ativo = false;
            await _db.SaveChangesAsync(ct);
            return null;
        }

        // O que já foi atendido é o que foi usado. O resto — marcado ou não — não vai
        // acontecer, porque o cliente está saindo.
        ciclo.QuantidadeUsada = await _db.Agendamentos.CountAsync(
            a => a.PacoteClienteId == vinculo.Id
                 && a.PacoteCiclo == ciclo.Ciclo
                 && a.Status == StatusAgendamento.Concluido, ct);

        // O que estava marcado para frente volta para a grade: o horário é de outra
        // pessoa agora, e deixá-lo preso seria guardar vaga para quem não vem.
        var inicioDeHoje = new DateTimeOffset(hoje.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var aCancelar = await _db.Agendamentos
            .Where(a => a.PacoteClienteId == vinculo.Id
                        && a.PacoteCiclo == ciclo.Ciclo
                        && a.Inicio >= inicioDeHoje
                        && a.Status != StatusAgendamento.Cancelado
                        && a.Status != StatusAgendamento.Concluido)
            .ToListAsync(ct);

        foreach (var agendamento in aCancelar)
        {
            agendamento.Status = StatusAgendamento.Cancelado;
        }

        // Há recorrência à frente? Só quando o pacote renova E este cliente segue nele.
        // Saindo, não há: o saldo tem de voltar como dinheiro.
        ciclo.Encerrar(haProximoCiclo: false, pacote.ValorPorAtendimento, DateTimeOffset.UtcNow);
        vinculo.Ativo = false;

        await _db.SaveChangesAsync(ct);
        return ciclo;
    }

    /// <summary>
    /// Avisa uma vez por ciclo. Sem essa marca, o pacote apareceria no relatório sete
    /// dias seguidos e o aviso viraria ruído — que é o mesmo que não avisar.
    /// </summary>
    private async Task<AvisoDeRenovacao?> AvisarAsync(
        Pacote pacote, DateOnly hoje, int dias, CancellationToken ct)
    {
        if (pacote.AvisoDoCiclo == pacote.CicloAtual)
        {
            return null;
        }

        pacote.AvisadoEm = DateTimeOffset.UtcNow;
        pacote.AvisoDoCiclo = pacote.CicloAtual;

        var clientes = await _db.PacoteClientes
            .CountAsync(c => c.PacoteId == pacote.Id && c.Ativo, ct);

        var quando = dias == 0 ? "hoje" : dias == 1 ? "amanhã" : $"em {dias} dias";
        var texto = pacote.EhRecorrente
            ? $"{pacote.Nome} renova {quando} ({pacote.FimDoCicloAtual:dd/MM}), "
              + $"para {clientes} cliente(s)."
            : $"{pacote.Nome} termina {quando} ({pacote.FimDoCicloAtual:dd/MM}). "
              + "O que não for usado será estornado.";

        return new AvisoDeRenovacao(
            pacote.Id, pacote.Nome, pacote.CicloAtual, pacote.FimDoCicloAtual,
            dias, clientes, pacote.Recorrencia, texto);
    }

    /// <summary>
    /// Fecha o ciclo de cada cliente e abre o seguinte, quando há. É aqui que o que
    /// sobrou vira crédito — ou estorno, no pacote que acaba.
    /// </summary>
    private async Task<(int Fechados, int Abertos, int ComEstorno, decimal Valor)> VirarCicloAsync(
        Pacote pacote, DateOnly hoje, CancellationToken ct)
    {
        var agora = DateTimeOffset.UtcNow;
        var vinculos = await _db.PacoteClientes
            .Where(c => c.PacoteId == pacote.Id && c.Ativo)
            .ToListAsync(ct);

        var fechados = 0;
        var abertos = 0;
        var comEstorno = 0;
        var valor = 0m;

        var proximoInicio = pacote.FimDoCicloAtual.AddDays(1);
        var proximoFim = pacote.FimCalculado(proximoInicio);

        foreach (var vinculo in vinculos)
        {
            var ciclo = await _db.CiclosDePacote
                .Where(c => c.PacoteClienteId == vinculo.Id && !c.Encerrado)
                .OrderByDescending(c => c.Ciclo)
                .FirstOrDefaultAsync(ct);
            if (ciclo is null)
            {
                continue;
            }

            // Só o que aconteceu consome saldo. O que ficou marcado e não foi atendido
            // não foi usado — e é justamente o que vira crédito ou estorno.
            ciclo.QuantidadeUsada = await _db.Agendamentos.CountAsync(
                a => a.PacoteClienteId == vinculo.Id
                     && a.PacoteCiclo == ciclo.Ciclo
                     && a.Status == StatusAgendamento.Concluido, ct);

            ciclo.Encerrar(pacote.EhRecorrente, pacote.ValorPorAtendimento, agora);
            fechados++;

            if (ciclo.EstornoQuantidade > 0)
            {
                comEstorno++;
                valor += ciclo.EstornoValor;
            }

            if (!pacote.EhRecorrente)
            {
                // O pacote acabou, e o cliente sai dele junto. Ficar "ativo" num pacote
                // encerrado o impedia de entrar em qualquer outro (CLIENTE_JA_TEM_PACOTE e
                // o índice único de vínculo ativo), sem ciclo aberto para usar aqui.
                vinculo.Ativo = false;
                continue;
            }

            _db.CiclosDePacote.Add(new CicloDoCliente
            {
                TenantId = vinculo.TenantId,
                PacoteClienteId = vinculo.Id,
                Ciclo = pacote.CicloAtual + 1,
                Inicio = proximoInicio,
                Fim = proximoFim,
                QuantidadeContratada = pacote.QuantidadePorCliente,
                CreditoRecebido = ciclo.CreditoCedido,
            });
            abertos++;
        }

        if (pacote.EhRecorrente)
        {
            pacote.CicloAtual += 1;
            pacote.InicioDoCicloAtual = proximoInicio;
            pacote.FimDoCicloAtual = proximoFim;
        }
        else
        {
            pacote.Status = StatusDePacote.Encerrado;
        }

        return (fechados, abertos, comEstorno, valor);
    }
}

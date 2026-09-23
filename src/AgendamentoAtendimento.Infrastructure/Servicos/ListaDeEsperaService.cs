using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Infrastructure.Servicos;

/// <summary>
/// A fila de quem ficou sem horário. "Não tem vaga" não pode ser o fim da conversa: quem
/// ficou de fora hoje é o primeiro a chamar quando alguém desmarca — e sem registro
/// ninguém lembra quem era.
/// </summary>
public class ListaDeEsperaService
{
    private readonly AppDbContext _db;
    private readonly RelogioDoTenant _relogio;

    public ListaDeEsperaService(AppDbContext db, RelogioDoTenant relogio)
    {
        _db = db;
        _relogio = relogio;
    }

    /// <summary>
    /// Põe alguém na fila. Já estar nela devolve a entrada que existe, em vez de criar
    /// outra: dois lugares na fila para o mesmo cliente seriam dois lugares na fila.
    /// </summary>
    public async Task<(EntradaListaDeEspera Entrada, bool JaEstava)> EntrarAsync(
        long clienteId,
        long itemCatalogoId,
        DateOnly? dataDesejada,
        long? responsavelId,
        string? observacao,
        CancellationToken ct)
    {
        var existente = await _db.ListaDeEspera
            .Include(e => e.Cliente)
            .Include(e => e.ItemCatalogo)
            .FirstOrDefaultAsync(e =>
                e.ClienteId == clienteId
                && e.ItemCatalogoId == itemCatalogoId
                && e.DataDesejada == dataDesejada
                && (e.Status == StatusNaEspera.Aguardando || e.Status == StatusNaEspera.Avisado), ct);

        if (existente is not null)
        {
            return (existente, true);
        }

        var entrada = new EntradaListaDeEspera
        {
            ClienteId = clienteId,
            ItemCatalogoId = itemCatalogoId,
            DataDesejada = dataDesejada,
            ResponsavelId = responsavelId,
            Observacao = string.IsNullOrWhiteSpace(observacao) ? null : observacao.Trim(),
        };

        _db.ListaDeEspera.Add(entrada);
        await _db.SaveChangesAsync(ct);
        return (entrada, false);
    }

    /// <summary>
    /// Quem estava esperando exatamente pelo que este agendamento acabou de liberar.
    /// A ordem é a da fila — quem chegou primeiro é chamado primeiro, porque qualquer
    /// outra ordem precisaria de uma justificativa que ninguém deu.
    /// </summary>
    public async Task<IReadOnlyList<EntradaListaDeEspera>> QuemEsperavaPorAsync(
        Agendamento agendamento, CancellationToken ct)
    {
        // O dia que a vaga abriu, no calendário da empresa — é nele que a fila pensa.
        var data = _relogio.DataLocal(agendamento.Inicio);

        // Os serviços que a vaga libera, com quem os prestava.
        var liberados = agendamento.Itens
            .Select(i => (i.ItemCatalogoId, Responsavel: i.ResponsavelId ?? agendamento.ResponsavelId))
            .ToList();

        if (liberados.Count == 0)
        {
            return Array.Empty<EntradaListaDeEspera>();
        }

        var itensIds = liberados.Select(l => l.ItemCatalogoId).Distinct().ToList();

        var candidatos = await _db.ListaDeEspera
            .Include(e => e.Cliente)
            .Include(e => e.ItemCatalogo)
            .Where(e => itensIds.Contains(e.ItemCatalogoId)
                && (e.Status == StatusNaEspera.Aguardando || e.Status == StatusNaEspera.Avisado)
                && (e.DataDesejada == null || e.DataDesejada == data))
            .OrderBy(e => e.CriadoEm)
            .ToListAsync(ct);

        // O casamento fino fica no domínio: responsável nulo aceita qualquer um, e isso
        // não se traduz bem para SQL sem espalhar a regra.
        return candidatos
            .Where(e => liberados.Any(l => e.CasaCom(l.ItemCatalogoId, data, l.Responsavel)))
            .ToList();
    }

    /// <summary>
    /// Marca como avisado quem foi chamado. Não manda nada por conta própria: quem
    /// entrega é o motor de lembretes, e duplicar o envio aqui daria dois avisos.
    /// </summary>
    public async Task<int> MarcarComoAvisadosAsync(
        IEnumerable<long> entradasIds, DateTimeOffset agora, CancellationToken ct)
    {
        var ids = entradasIds.ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        var entradas = await _db.ListaDeEspera
            .Where(e => ids.Contains(e.Id) && e.Status == StatusNaEspera.Aguardando)
            .ToListAsync(ct);

        foreach (var entrada in entradas)
        {
            entrada.Status = StatusNaEspera.Avisado;
            entrada.AvisadoEm = agora;
        }

        if (entradas.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return entradas.Count;
    }

    /// <summary>A espera virou agendamento. É o fim feliz da fila.</summary>
    public async Task ConverterAsync(long entradaId, long agendamentoId, CancellationToken ct)
    {
        var entrada = await _db.ListaDeEspera.FirstOrDefaultAsync(e => e.Id == entradaId, ct);
        if (entrada is null || !entrada.NaFila)
        {
            return;
        }

        entrada.Status = StatusNaEspera.Convertido;
        entrada.AgendamentoId = agendamentoId;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Fecha as esperas cuja data já passou. Expirado não some da tabela: é o que mostra
    /// demanda que a empresa não conseguiu atender, e apagar isso apagaria o motivo de a
    /// fila existir.
    /// </summary>
    public async Task<int> ExpirarVencidasAsync(DateOnly hoje, CancellationToken ct)
    {
        var vencidas = await _db.ListaDeEspera
            .Where(e => e.DataDesejada != null && e.DataDesejada < hoje
                && (e.Status == StatusNaEspera.Aguardando || e.Status == StatusNaEspera.Avisado))
            .ToListAsync(ct);

        foreach (var entrada in vencidas)
        {
            entrada.Status = StatusNaEspera.Expirado;
        }

        if (vencidas.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return vencidas.Count;
    }
}

using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Infrastructure.Servicos;

/// <summary>
/// Uma pessoa não está em dois lugares ao mesmo tempo: o mesmo cliente não fica marcado
/// duas vezes no mesmo horário — nem com duas pessoas diferentes, nem ocupando duas vagas
/// da mesma turma. Empresa pode: ela manda gente diferente.
///
/// É uma regra só para toda porta que marca: a agenda, a sessão de pacote e a página
/// pública. Só a agenda a conferia, e a sessão do pacote e o pedido da página punham o
/// cliente em dois lugares — o mesmo horário com a Bruna, o Caio e o Denis.
/// </summary>
public static class AgendaDoCliente
{
    /// <summary>
    /// Se o cliente já tem, entre <paramref name="inicio"/> e <paramref name="fim"/>, um
    /// atendimento que ocupa. O cancelado e aquele em que ele faltou não ocupam. Confira
    /// sob a trava da agenda: é ela que impede dois pedidos de passarem juntos.
    /// </summary>
    public static async Task<bool> ClienteJaTemAtendimentoAsync(
        this AppDbContext db, Cliente cliente, DateTimeOffset inicio, DateTimeOffset fim,
        long? ignorarAgendamentoId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(cliente);

        // Cadastro ainda não gravado não tem agenda.
        if (cliente.Tipo != TipoCliente.Pessoa || cliente.Id == 0)
        {
            return false;
        }

        return await db.Agendamentos.AsNoTracking().AnyAsync(a =>
            a.ClienteId == cliente.Id
            && a.Id != (ignorarAgendamentoId ?? 0)
            && a.Status != StatusAgendamento.Cancelado
            && a.Status != StatusAgendamento.NaoCompareceu
            && a.Inicio < fim && a.Fim > inicio, ct);
    }
}

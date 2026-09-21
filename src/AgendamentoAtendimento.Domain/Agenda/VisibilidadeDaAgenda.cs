using System.Linq.Expressions;
using AgendamentoAtendimento.Domain.Usuarios;

namespace AgendamentoAtendimento.Domain.Agenda;

/// <summary>
/// Quem enxerga quais atendimentos. Vive aqui, e não dentro de um controller, porque a
/// mesma regra vale na listagem, no detalhe, nas alterações e nos contadores do painel —
/// e uma regra de visibilidade escrita quatro vezes vira quatro regras.
/// </summary>
public static class VisibilidadeDaAgenda
{
    /// <summary>A permissão que abre a agenda do time inteiro.</summary>
    public const string VerTodos = "agenda.ver-todos";

    public static bool VeTudo(IEnumerable<string>? permissoes) =>
        Permissoes.Permite(permissoes?.ToList() ?? new List<string>(), VerTodos);

    /// <summary>
    /// O que esta pessoa pode ver. "Meu" inclui o atendimento em que ela presta QUALQUER
    /// serviço, não só aquele em que ela responde pelo todo: desde que cada serviço tem
    /// o seu responsável, um atendimento passa por mais de uma pessoa, e quem entra nele
    /// precisa enxergá-lo.
    /// </summary>
    public static Expression<Func<Agendamento, bool>> Filtro(long usuarioId) =>
        a => a.ResponsavelId == usuarioId || a.Itens.Any(i => i.ResponsavelId == usuarioId);

    /// <summary>Aplica o corte, ou devolve a consulta intacta para quem vê tudo.</summary>
    public static IQueryable<Agendamento> Aplicar(
        IQueryable<Agendamento> consulta, IEnumerable<string>? permissoes, long usuarioId) =>
        VeTudo(permissoes) ? consulta : consulta.Where(Filtro(usuarioId));
}

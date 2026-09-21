using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class VerAgendaDeTodoOTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Quem já via a agenda via a do time inteiro: era o único comportamento que
            // existia. Nascer sem a permissão nova estreitaria a agenda de todo mundo da
            // noite para o dia, sem ninguém ter pedido. Quem quiser restringir, tira.
            migrationBuilder.Sql(@"
                INSERT INTO perfil_permissoes (tenant_id, perfil_id, permissao, criado_em, excluido)
                SELECT pp.tenant_id, pp.perfil_id, 'agenda.ver-todos', now(), false
                  FROM perfil_permissoes pp
                 WHERE pp.permissao = 'agenda.ver'
                   AND pp.excluido = false
                   AND NOT EXISTS (
                       SELECT 1 FROM perfil_permissoes j
                        WHERE j.perfil_id = pp.perfil_id
                          AND j.permissao = 'agenda.ver-todos'
                          AND j.excluido = false);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM perfil_permissoes WHERE permissao = 'agenda.ver-todos';");
        }
    }
}

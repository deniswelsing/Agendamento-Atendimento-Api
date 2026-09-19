using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ResponsavelPorServico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "vendedor_id",
                table: "venda_itens",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ordem",
                table: "agendamento_itens",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "responsavel_id",
                table: "agendamento_itens",
                type: "bigint",
                nullable: true);

            // A ordem passa a definir a janela de cada serviço. Tudo que existia nasceu
            // com 0; a sequência real é a de inserção, que o id preserva.
            migrationBuilder.Sql(@"
                UPDATE agendamento_itens AS ai
                SET ordem = ordenado.posicao
                FROM (
                    SELECT id, ROW_NUMBER() OVER (
                        PARTITION BY agendamento_id ORDER BY id) - 1 AS posicao
                    FROM agendamento_itens
                ) AS ordenado
                WHERE ai.id = ordenado.id;");

            // `responsavel_id` e `vendedor_id` ficam nulos de propósito: nulo quer dizer
            // 'segue o responsável do agendamento' e 'segue o vendedor da venda'. Copiar
            // o valor congelaria o que hoje acompanha — e o que já existia continua certo.

            migrationBuilder.CreateIndex(
                name: "ix_venda_itens_vendedor_id",
                table: "venda_itens",
                column: "vendedor_id");

            migrationBuilder.CreateIndex(
                name: "ix_agendamento_itens_responsavel_id",
                table: "agendamento_itens",
                column: "responsavel_id");

            migrationBuilder.CreateIndex(
                name: "ix_agendamento_itens_tenant_id_responsavel_id",
                table: "agendamento_itens",
                columns: new[] { "tenant_id", "responsavel_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_agendamento_itens_usuarios_responsavel_id",
                table: "agendamento_itens",
                column: "responsavel_id",
                principalTable: "usuarios",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_venda_itens_usuarios_vendedor_id",
                table: "venda_itens",
                column: "vendedor_id",
                principalTable: "usuarios",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agendamento_itens_usuarios_responsavel_id",
                table: "agendamento_itens");

            migrationBuilder.DropForeignKey(
                name: "fk_venda_itens_usuarios_vendedor_id",
                table: "venda_itens");

            migrationBuilder.DropIndex(
                name: "ix_venda_itens_vendedor_id",
                table: "venda_itens");

            migrationBuilder.DropIndex(
                name: "ix_agendamento_itens_responsavel_id",
                table: "agendamento_itens");

            migrationBuilder.DropIndex(
                name: "ix_agendamento_itens_tenant_id_responsavel_id",
                table: "agendamento_itens");

            migrationBuilder.DropColumn(
                name: "vendedor_id",
                table: "venda_itens");

            migrationBuilder.DropColumn(
                name: "ordem",
                table: "agendamento_itens");

            migrationBuilder.DropColumn(
                name: "responsavel_id",
                table: "agendamento_itens");
        }
    }
}

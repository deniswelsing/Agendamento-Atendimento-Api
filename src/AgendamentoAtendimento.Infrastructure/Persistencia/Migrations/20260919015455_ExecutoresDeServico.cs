using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ExecutoresDeServico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "executores_de_servico",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    item_catalogo_id = table.Column<long>(type: "bigint", nullable: false),
                    usuario_id = table.Column<long>(type: "bigint", nullable: false),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    criado_por_id = table.Column<long>(type: "bigint", nullable: true),
                    atualizado_por_id = table.Column<long>(type: "bigint", nullable: true),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    excluido = table.Column<bool>(type: "boolean", nullable: false),
                    excluido_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_executores_de_servico", x => x.id);
                    table.ForeignKey(
                        name: "fk_executores_de_servico_itens_catalogo_item_catalogo_id",
                        column: x => x.item_catalogo_id,
                        principalTable: "itens_catalogo",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_executores_de_servico_usuarios_usuario_id",
                        column: x => x.usuario_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_executores_de_servico_item_catalogo_id",
                table: "executores_de_servico",
                column: "item_catalogo_id");

            migrationBuilder.CreateIndex(
                name: "ix_executores_de_servico_tenant_id_item_catalogo_id_usuario_id",
                table: "executores_de_servico",
                columns: new[] { "tenant_id", "item_catalogo_id", "usuario_id" },
                unique: true,
                filter: "excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_executores_de_servico_usuario_id",
                table: "executores_de_servico",
                column: "usuario_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "executores_de_servico");
        }
    }
}

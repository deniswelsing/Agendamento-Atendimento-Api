using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ListaDeEspera : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lista_de_espera",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    cliente_id = table.Column<long>(type: "bigint", nullable: false),
                    item_catalogo_id = table.Column<long>(type: "bigint", nullable: false),
                    data_desejada = table.Column<DateOnly>(type: "date", nullable: true),
                    responsavel_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    avisado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    agendamento_id = table.Column<long>(type: "bigint", nullable: true),
                    observacao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_lista_de_espera", x => x.id);
                    table.ForeignKey(
                        name: "fk_lista_de_espera_agendamentos_agendamento_id",
                        column: x => x.agendamento_id,
                        principalTable: "agendamentos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_lista_de_espera_clientes_cliente_id",
                        column: x => x.cliente_id,
                        principalTable: "clientes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lista_de_espera_itens_catalogo_item_catalogo_id",
                        column: x => x.item_catalogo_id,
                        principalTable: "itens_catalogo",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lista_de_espera_usuarios_responsavel_id",
                        column: x => x.responsavel_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lista_de_espera_agendamento_id",
                table: "lista_de_espera",
                column: "agendamento_id");

            migrationBuilder.CreateIndex(
                name: "ix_lista_de_espera_cliente_id",
                table: "lista_de_espera",
                column: "cliente_id");

            migrationBuilder.CreateIndex(
                name: "ix_lista_de_espera_item_catalogo_id",
                table: "lista_de_espera",
                column: "item_catalogo_id");

            migrationBuilder.CreateIndex(
                name: "ix_lista_de_espera_responsavel_id",
                table: "lista_de_espera",
                column: "responsavel_id");

            migrationBuilder.CreateIndex(
                name: "ix_lista_de_espera_tenant_id_cliente_id_item_catalogo_id_data_",
                table: "lista_de_espera",
                columns: new[] { "tenant_id", "cliente_id", "item_catalogo_id", "data_desejada" },
                unique: true,
                filter: "excluido = false AND status IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "ix_lista_de_espera_tenant_id_status_item_catalogo_id_data_dese",
                table: "lista_de_espera",
                columns: new[] { "tenant_id", "status", "item_catalogo_id", "data_desejada" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lista_de_espera");
        }
    }
}

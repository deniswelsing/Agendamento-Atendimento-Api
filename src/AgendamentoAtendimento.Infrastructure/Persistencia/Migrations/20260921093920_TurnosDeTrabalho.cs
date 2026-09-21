using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class TurnosDeTrabalho : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "turno_id",
                table: "horarios_staff",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "turnos",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    nome = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    inicio = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    fim = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    pausa_inicio = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    pausa_fim = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    cor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    ativo = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_turnos", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_horarios_staff_turno_id",
                table: "horarios_staff",
                column: "turno_id");

            migrationBuilder.CreateIndex(
                name: "ix_turnos_tenant_id_nome",
                table: "turnos",
                columns: new[] { "tenant_id", "nome" },
                unique: true,
                filter: "excluido = false");

            migrationBuilder.AddForeignKey(
                name: "fk_horarios_staff_turnos_turno_id",
                table: "horarios_staff",
                column: "turno_id",
                principalTable: "turnos",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_horarios_staff_turnos_turno_id",
                table: "horarios_staff");

            migrationBuilder.DropTable(
                name: "turnos");

            migrationBuilder.DropIndex(
                name: "ix_horarios_staff_turno_id",
                table: "horarios_staff");

            migrationBuilder.DropColumn(
                name: "turno_id",
                table: "horarios_staff");
        }
    }
}

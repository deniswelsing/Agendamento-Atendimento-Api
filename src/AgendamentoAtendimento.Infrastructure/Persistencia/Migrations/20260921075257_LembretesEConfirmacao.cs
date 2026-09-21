using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class LembretesEConfirmacao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "confirmado_em",
                table: "agendamentos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "configuracoes_de_lembrete",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ativo = table.Column<bool>(type: "boolean", nullable: false),
                    horas_de_antecedencia = table.Column<int>(type: "integer", nullable: false),
                    avisar_ao_marcar = table.Column<bool>(type: "boolean", nullable: false),
                    pedir_confirmacao = table.Column<bool>(type: "boolean", nullable: false),
                    canal = table.Column<int>(type: "integer", nullable: false),
                    tolerancia_de_atraso_minutos = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_configuracoes_de_lembrete", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "lembretes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    agendamento_id = table.Column<long>(type: "bigint", nullable: false),
                    tipo = table.Column<int>(type: "integer", nullable: false),
                    canal = table.Column<int>(type: "integer", nullable: false),
                    quando_enviar = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    enviado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    destino = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    erro = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    tentativas = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_lembretes", x => x.id);
                    table.ForeignKey(
                        name: "fk_lembretes_agendamentos_agendamento_id",
                        column: x => x.agendamento_id,
                        principalTable: "agendamentos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_configuracoes_de_lembrete_tenant_id",
                table: "configuracoes_de_lembrete",
                column: "tenant_id",
                unique: true,
                filter: "excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_lembretes_agendamento_id_tipo",
                table: "lembretes",
                columns: new[] { "agendamento_id", "tipo" },
                unique: true,
                filter: "excluido = false AND status = 1");

            migrationBuilder.CreateIndex(
                name: "ix_lembretes_tenant_id_status_quando_enviar",
                table: "lembretes",
                columns: new[] { "tenant_id", "status", "quando_enviar" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "configuracoes_de_lembrete");

            migrationBuilder.DropTable(
                name: "lembretes");

            migrationBuilder.DropColumn(
                name: "confirmado_em",
                table: "agendamentos");
        }
    }
}

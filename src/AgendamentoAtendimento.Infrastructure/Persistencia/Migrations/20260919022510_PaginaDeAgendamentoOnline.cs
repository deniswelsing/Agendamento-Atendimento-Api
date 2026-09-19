using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class PaginaDeAgendamentoOnline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "visivel_online",
                table: "itens_catalogo",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "codigo_publico",
                table: "agendamentos",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "origem",
                table: "agendamentos",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "paginas_publicas",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ativa = table.Column<bool>(type: "boolean", nullable: false),
                    slug = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    titulo_publico = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    mensagem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    endereco = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    telefone_contato = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    antecedencia_minima_horas = table.Column<int>(type: "integer", nullable: false),
                    janela_maxima_dias = table.Column<int>(type: "integer", nullable: false),
                    exige_aprovacao = table.Column<bool>(type: "boolean", nullable: false),
                    permite_escolher_profissional = table.Column<bool>(type: "boolean", nullable: false),
                    exige_telefone = table.Column<bool>(type: "boolean", nullable: false),
                    limite_diario_por_cliente = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_paginas_publicas", x => x.id);
                });

            // `origem` nasceu com defaultValue 0, que não é valor nenhum do enum
            // (Interno = 1). Todo agendamento que já existia foi o time que marcou.
            migrationBuilder.Sql("UPDATE agendamentos SET origem = 1 WHERE origem = 0;");

            // `visivel_online` nasceu falso para toda linha existente, mas a propriedade
            // nasce verdadeira: sem isto o mesmo catálogo se comportaria de dois jeitos
            // conforme a data em que o item foi criado. Produto nunca vai à página.
            migrationBuilder.Sql(
                "UPDATE itens_catalogo SET visivel_online = true WHERE tipo = 1;");

            migrationBuilder.CreateIndex(
                name: "ix_agendamentos_codigo_publico",
                table: "agendamentos",
                column: "codigo_publico",
                unique: true,
                filter: "codigo_publico IS NOT NULL AND excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_paginas_publicas_slug",
                table: "paginas_publicas",
                column: "slug",
                unique: true,
                filter: "excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_paginas_publicas_tenant_id",
                table: "paginas_publicas",
                column: "tenant_id",
                unique: true,
                filter: "excluido = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "paginas_publicas");

            migrationBuilder.DropIndex(
                name: "ix_agendamentos_codigo_publico",
                table: "agendamentos");

            migrationBuilder.DropColumn(
                name: "visivel_online",
                table: "itens_catalogo");

            migrationBuilder.DropColumn(
                name: "codigo_publico",
                table: "agendamentos");

            migrationBuilder.DropColumn(
                name: "origem",
                table: "agendamentos");
        }
    }
}

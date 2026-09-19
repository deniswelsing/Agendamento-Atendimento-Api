using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class CobrancaECapturaDePagamento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "adquirente_chave",
                table: "pagamentos",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bandeira",
                table: "pagamentos",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "conciliado_em",
                table: "pagamentos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "meio",
                table: "pagamentos",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "nsu",
                table: "pagamentos",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "taxa_conferida",
                table: "pagamentos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ultimos_digitos",
                table: "pagamentos",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "valor_taxa_estimada",
                table: "pagamentos",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "cobrancas",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    venda_id = table.Column<long>(type: "bigint", nullable: false),
                    chave_idempotencia = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    meio = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    forma_pagamento_id = table.Column<long>(type: "bigint", nullable: false),
                    valor = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    parcelas = table.Column<int>(type: "integer", nullable: false),
                    adquirente_chave = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    terminal_serie = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    nsu = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    codigo_autorizacao = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    bandeira = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ultimos_digitos = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    transacao_externa_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    pix_copia_e_cola = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    valor_taxa_real = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    motivo_recusa = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    enviada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    respondida_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expira_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    pagamento_id = table.Column<long>(type: "bigint", nullable: true),
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
                    table.PrimaryKey("pk_cobrancas", x => x.id);
                    table.ForeignKey(
                        name: "fk_cobrancas_formas_pagamento_forma_pagamento_id",
                        column: x => x.forma_pagamento_id,
                        principalTable: "formas_pagamento",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cobrancas_pagamentos_pagamento_id",
                        column: x => x.pagamento_id,
                        principalTable: "pagamentos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_cobrancas_vendas_venda_id",
                        column: x => x.venda_id,
                        principalTable: "vendas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pagamentos_tenant_id_nsu",
                table: "pagamentos",
                columns: new[] { "tenant_id", "nsu" });

            migrationBuilder.CreateIndex(
                name: "ix_cobrancas_forma_pagamento_id",
                table: "cobrancas",
                column: "forma_pagamento_id");

            migrationBuilder.CreateIndex(
                name: "ix_cobrancas_pagamento_id",
                table: "cobrancas",
                column: "pagamento_id");

            migrationBuilder.CreateIndex(
                name: "ix_cobrancas_tenant_id_chave_idempotencia",
                table: "cobrancas",
                columns: new[] { "tenant_id", "chave_idempotencia" },
                unique: true,
                filter: "excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_cobrancas_tenant_id_venda_id_status",
                table: "cobrancas",
                columns: new[] { "tenant_id", "venda_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_cobrancas_venda_id",
                table: "cobrancas",
                column: "venda_id");

            // Os pagamentos que já existem foram lançados com a taxa estimada da
            // configuração. Sem isto a estimada ficaria zero e todos apareceriam como
            // divergência — o líquido deles não mudou, só passou a ser rastreável.
            migrationBuilder.Sql(
                "UPDATE pagamentos SET valor_taxa_estimada = valor_taxa, taxa_conferida = false;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cobrancas");

            migrationBuilder.DropIndex(
                name: "ix_pagamentos_tenant_id_nsu",
                table: "pagamentos");

            migrationBuilder.DropColumn(
                name: "adquirente_chave",
                table: "pagamentos");

            migrationBuilder.DropColumn(
                name: "bandeira",
                table: "pagamentos");

            migrationBuilder.DropColumn(
                name: "conciliado_em",
                table: "pagamentos");

            migrationBuilder.DropColumn(
                name: "meio",
                table: "pagamentos");

            migrationBuilder.DropColumn(
                name: "nsu",
                table: "pagamentos");

            migrationBuilder.DropColumn(
                name: "taxa_conferida",
                table: "pagamentos");

            migrationBuilder.DropColumn(
                name: "ultimos_digitos",
                table: "pagamentos");

            migrationBuilder.DropColumn(
                name: "valor_taxa_estimada",
                table: "pagamentos");
        }
    }
}

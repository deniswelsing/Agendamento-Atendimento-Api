using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class PacotesPrePagos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "pacote_ciclo",
                table: "agendamentos",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "pacote_cliente_id",
                table: "agendamentos",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "pacote_modelos",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    nome = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    descricao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    quantidade = table.Column<int>(type: "integer", nullable: false),
                    preco = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    recorrencia = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_pacote_modelos", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pacote_modelo_itens",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    pacote_modelo_id = table.Column<long>(type: "bigint", nullable: false),
                    item_catalogo_id = table.Column<long>(type: "bigint", nullable: false),
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
                    table.PrimaryKey("pk_pacote_modelo_itens", x => x.id);
                    table.ForeignKey(
                        name: "fk_pacote_modelo_itens_itens_catalogo_item_catalogo_id",
                        column: x => x.item_catalogo_id,
                        principalTable: "itens_catalogo",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pacote_modelo_itens_pacote_modelos_pacote_modelo_id",
                        column: x => x.pacote_modelo_id,
                        principalTable: "pacote_modelos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pacotes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    pacote_modelo_id = table.Column<long>(type: "bigint", nullable: true),
                    nome = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    quantidade_por_cliente = table.Column<int>(type: "integer", nullable: false),
                    preco_por_cliente = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    recorrencia = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    ciclo_atual = table.Column<int>(type: "integer", nullable: false),
                    inicio_do_ciclo_atual = table.Column<DateOnly>(type: "date", nullable: false),
                    fim_do_ciclo_atual = table.Column<DateOnly>(type: "date", nullable: false),
                    avisado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    aviso_do_ciclo = table.Column<int>(type: "integer", nullable: true),
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
                    table.PrimaryKey("pk_pacotes", x => x.id);
                    table.ForeignKey(
                        name: "fk_pacotes_pacote_modelos_pacote_modelo_id",
                        column: x => x.pacote_modelo_id,
                        principalTable: "pacote_modelos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "pacote_clientes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    pacote_id = table.Column<long>(type: "bigint", nullable: false),
                    cliente_id = table.Column<long>(type: "bigint", nullable: false),
                    dia_da_semana = table.Column<int>(type: "integer", nullable: true),
                    hora = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    responsavel_preferido_id = table.Column<long>(type: "bigint", nullable: true),
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
                    table.PrimaryKey("pk_pacote_clientes", x => x.id);
                    table.ForeignKey(
                        name: "fk_pacote_clientes_clientes_cliente_id",
                        column: x => x.cliente_id,
                        principalTable: "clientes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pacote_clientes_pacotes_pacote_id",
                        column: x => x.pacote_id,
                        principalTable: "pacotes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_pacote_clientes_usuarios_responsavel_preferido_id",
                        column: x => x.responsavel_preferido_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "pacote_itens",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    pacote_id = table.Column<long>(type: "bigint", nullable: false),
                    item_catalogo_id = table.Column<long>(type: "bigint", nullable: false),
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
                    table.PrimaryKey("pk_pacote_itens", x => x.id);
                    table.ForeignKey(
                        name: "fk_pacote_itens_itens_catalogo_item_catalogo_id",
                        column: x => x.item_catalogo_id,
                        principalTable: "itens_catalogo",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pacote_itens_pacotes_pacote_id",
                        column: x => x.pacote_id,
                        principalTable: "pacotes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ciclos_de_pacote",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    pacote_cliente_id = table.Column<long>(type: "bigint", nullable: false),
                    ciclo = table.Column<int>(type: "integer", nullable: false),
                    inicio = table.Column<DateOnly>(type: "date", nullable: false),
                    fim = table.Column<DateOnly>(type: "date", nullable: false),
                    quantidade_contratada = table.Column<int>(type: "integer", nullable: false),
                    credito_recebido = table.Column<int>(type: "integer", nullable: false),
                    quantidade_usada = table.Column<int>(type: "integer", nullable: false),
                    encerrado = table.Column<bool>(type: "boolean", nullable: false),
                    encerrado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    credito_cedido = table.Column<int>(type: "integer", nullable: false),
                    estorno_quantidade = table.Column<int>(type: "integer", nullable: false),
                    estorno_valor = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
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
                    table.PrimaryKey("pk_ciclos_de_pacote", x => x.id);
                    table.ForeignKey(
                        name: "fk_ciclos_de_pacote_pacote_clientes_pacote_cliente_id",
                        column: x => x.pacote_cliente_id,
                        principalTable: "pacote_clientes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agendamentos_tenant_id_pacote_cliente_id_pacote_ciclo",
                table: "agendamentos",
                columns: new[] { "tenant_id", "pacote_cliente_id", "pacote_ciclo" });

            migrationBuilder.CreateIndex(
                name: "ix_ciclos_de_pacote_pacote_cliente_id",
                table: "ciclos_de_pacote",
                column: "pacote_cliente_id");

            migrationBuilder.CreateIndex(
                name: "ix_ciclos_de_pacote_tenant_id_pacote_cliente_id_ciclo",
                table: "ciclos_de_pacote",
                columns: new[] { "tenant_id", "pacote_cliente_id", "ciclo" },
                unique: true,
                filter: "excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_pacote_clientes_cliente_id",
                table: "pacote_clientes",
                column: "cliente_id");

            migrationBuilder.CreateIndex(
                name: "ix_pacote_clientes_pacote_id",
                table: "pacote_clientes",
                column: "pacote_id");

            migrationBuilder.CreateIndex(
                name: "ix_pacote_clientes_responsavel_preferido_id",
                table: "pacote_clientes",
                column: "responsavel_preferido_id");

            migrationBuilder.CreateIndex(
                name: "ix_pacote_clientes_tenant_id_cliente_id",
                table: "pacote_clientes",
                columns: new[] { "tenant_id", "cliente_id" },
                unique: true,
                filter: "excluido = false AND ativo = true");

            migrationBuilder.CreateIndex(
                name: "ix_pacote_clientes_tenant_id_pacote_id",
                table: "pacote_clientes",
                columns: new[] { "tenant_id", "pacote_id" });

            migrationBuilder.CreateIndex(
                name: "ix_pacote_itens_item_catalogo_id",
                table: "pacote_itens",
                column: "item_catalogo_id");

            migrationBuilder.CreateIndex(
                name: "ix_pacote_itens_pacote_id",
                table: "pacote_itens",
                column: "pacote_id");

            migrationBuilder.CreateIndex(
                name: "ix_pacote_itens_tenant_id_pacote_id_item_catalogo_id",
                table: "pacote_itens",
                columns: new[] { "tenant_id", "pacote_id", "item_catalogo_id" },
                unique: true,
                filter: "excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_pacote_modelo_itens_item_catalogo_id",
                table: "pacote_modelo_itens",
                column: "item_catalogo_id");

            migrationBuilder.CreateIndex(
                name: "ix_pacote_modelo_itens_pacote_modelo_id",
                table: "pacote_modelo_itens",
                column: "pacote_modelo_id");

            migrationBuilder.CreateIndex(
                name: "ix_pacote_modelo_itens_tenant_id_pacote_modelo_id_item_catalog",
                table: "pacote_modelo_itens",
                columns: new[] { "tenant_id", "pacote_modelo_id", "item_catalogo_id" },
                unique: true,
                filter: "excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_pacote_modelos_tenant_id_nome",
                table: "pacote_modelos",
                columns: new[] { "tenant_id", "nome" },
                unique: true,
                filter: "excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_pacotes_pacote_modelo_id",
                table: "pacotes",
                column: "pacote_modelo_id");

            migrationBuilder.CreateIndex(
                name: "ix_pacotes_tenant_id_status_fim_do_ciclo_atual",
                table: "pacotes",
                columns: new[] { "tenant_id", "status", "fim_do_ciclo_atual" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ciclos_de_pacote");

            migrationBuilder.DropTable(
                name: "pacote_itens");

            migrationBuilder.DropTable(
                name: "pacote_modelo_itens");

            migrationBuilder.DropTable(
                name: "pacote_clientes");

            migrationBuilder.DropTable(
                name: "pacotes");

            migrationBuilder.DropTable(
                name: "pacote_modelos");

            migrationBuilder.DropIndex(
                name: "ix_agendamentos_tenant_id_pacote_cliente_id_pacote_ciclo",
                table: "agendamentos");

            migrationBuilder.DropColumn(
                name: "pacote_ciclo",
                table: "agendamentos");

            migrationBuilder.DropColumn(
                name: "pacote_cliente_id",
                table: "agendamentos");
        }
    }
}

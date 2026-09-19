using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Comissão congelada no item vendido e o vendedor na venda.
    ///
    /// As vendas que já existem ficam com comissão zero e sem vendedor, de propósito:
    /// copiar o percentual atual do catálogo inventaria uma comissão que ninguém acordou
    /// na época. Zero é o que se sabe.
    /// </summary>
    public partial class ComissaoEVendedorNaVenda : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "vendedor_id",
                table: "vendas",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "comissao_percentual",
                table: "venda_itens",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateIndex(
                name: "ix_vendas_vendedor_id",
                table: "vendas",
                column: "vendedor_id");

            migrationBuilder.AddForeignKey(
                name: "fk_vendas_usuarios_vendedor_id",
                table: "vendas",
                column: "vendedor_id",
                principalTable: "usuarios",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_vendas_usuarios_vendedor_id",
                table: "vendas");

            migrationBuilder.DropIndex(
                name: "ix_vendas_vendedor_id",
                table: "vendas");

            migrationBuilder.DropColumn(
                name: "vendedor_id",
                table: "vendas");

            migrationBuilder.DropColumn(
                name: "comissao_percentual",
                table: "venda_itens");
        }
    }
}

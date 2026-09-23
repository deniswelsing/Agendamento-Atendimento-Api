using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <summary>
    /// Estorno de recebimento: o pagamento estornado continua na venda, com a data e o
    /// motivo. E a venda passa a saber se a finalização baixou o estoque
    /// (`estoque_baixado`), para o cancelamento devolver exatamente o que saiu.
    ///
    /// Vendas antigas nascem com `estoque_baixado = false`: pelo que está gravado não dá
    /// para saber quais passaram pela finalização (uma venda paga pelo recebimento também
    /// ganha `finalizada_em`, sem baixar nada). Devolver estoque que nunca saiu seria pior
    /// que não devolver — então o cancelamento só devolve o que for finalizado daqui em diante.
    /// </summary>
    public partial class EstornoDePagamento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "estoque_baixado",
                table: "vendas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "estornado_em",
                table: "pagamentos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "motivo_estorno",
                table: "pagamentos",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "estoque_baixado",
                table: "vendas");

            migrationBuilder.DropColumn(
                name: "estornado_em",
                table: "pagamentos");

            migrationBuilder.DropColumn(
                name: "motivo_estorno",
                table: "pagamentos");
        }
    }
}

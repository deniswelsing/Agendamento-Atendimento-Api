using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ModoDeOcupacaoDaEmpresa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1 = PorServico. Zero não é modo nenhum: quem já é cliente continua com a
            // agenda que tinha ontem, que é justamente a ocupação por serviço.
            migrationBuilder.AddColumn<int>(
                name: "modo_de_ocupacao",
                table: "tenants",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.Sql("UPDATE tenants SET modo_de_ocupacao = 1 WHERE modo_de_ocupacao = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "modo_de_ocupacao",
                table: "tenants");
        }
    }
}

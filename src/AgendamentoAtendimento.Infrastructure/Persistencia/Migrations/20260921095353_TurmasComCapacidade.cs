using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class TurmasComCapacidade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "capacidade_turma",
                table: "itens_catalogo",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // Zero seria um serviço que ninguém pode marcar. Todo serviço que existe hoje
            // é individual, e é assim que continua até alguém dizer o contrário.
            migrationBuilder.Sql(
                "UPDATE itens_catalogo SET capacidade_turma = 1 WHERE capacidade_turma < 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "capacidade_turma",
                table: "itens_catalogo");
        }
    }
}

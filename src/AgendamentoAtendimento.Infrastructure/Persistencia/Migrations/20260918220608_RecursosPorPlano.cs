using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <summary>
    /// Traz os planos já gravados para as chaves do catálogo de recursos. O esquema não
    /// muda: o que muda é o conteúdo de `planos.recursos`, que antes guardava rótulos
    /// soltos ("agenda,clientes,...") e agora guarda as chaves que a Api e o app conhecem.
    /// Só os quatro códigos que a plataforma semeia são tocados; um plano criado à mão
    /// fica como está.
    /// </summary>
    public partial class RecursosPorPlano : Migration
    {
        private const string Nivel1 =
            "agendamentos,clientes,catalogo,vendas,pagina-online,lembrete-email";

        private const string Nivel2 = Nivel1 +
            ",multi-usuario,jornada-por-pessoa,politica-cancelamento,cartao-em-arquivo" +
            ",lembrete-sms-whatsapp,google-calendar,agendamento-recorrente,sem-marca";

        private const string Nivel3 = Nivel2 +
            ",relatorios-avancados,comissoes,multiunidade,permissoes-avancadas" +
            ",recursos-reservaveis";

        private const string Nivel4 = Nivel3 +
            ",onboarding-dedicado,api-publica,gerente-de-conta";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Aplicar(migrationBuilder, "BASIC", Nivel1);
            Aplicar(migrationBuilder, "PLATINUM", Nivel2);
            Aplicar(migrationBuilder, "ULTIMATE", Nivel3);
            Aplicar(migrationBuilder, "CUSTOM", Nivel4);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Aplicar(migrationBuilder, "BASIC", "agenda,clientes,catalogo,vendas");
            Aplicar(migrationBuilder, "PLATINUM",
                "agenda,clientes,catalogo,vendas,financeiro,time,horarios,relatorios");
            Aplicar(migrationBuilder, "ULTIMATE",
                "agenda,clientes,catalogo,vendas,financeiro,time,horarios,relatorios,multiunidade,api");
            Aplicar(migrationBuilder, "CUSTOM", "tudo");
        }

        private static void Aplicar(MigrationBuilder builder, string codigo, string recursos) =>
            builder.Sql($"UPDATE planos SET recursos = '{recursos}' WHERE codigo = '{codigo}';");
    }
}

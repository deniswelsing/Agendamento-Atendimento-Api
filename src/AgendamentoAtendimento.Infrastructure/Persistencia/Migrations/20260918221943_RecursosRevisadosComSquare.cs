using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <summary>
    /// Revisão do catálogo depois de conferir a tabela do Square Appointments: entram
    /// contratos e sinal (degrau 1), turmas, lista de espera e limite diário (degrau 2) e
    /// registro de ponto (degrau 3). O esquema não muda, só o conteúdo de
    /// `planos.recursos`, e apenas nos quatro códigos que a plataforma semeia.
    /// </summary>
    public partial class RecursosRevisadosComSquare : Migration
    {
        private const string Nivel1 =
            "agendamentos,clientes,catalogo,vendas,pagina-online,lembrete-email,contratos-e-sinal";

        private const string Nivel2 = Nivel1 +
            ",multi-usuario,jornada-por-pessoa,politica-cancelamento,cartao-em-arquivo,lembrete-sms-whatsapp,google-calendar,agendamento-recorrente,sem-marca,turmas,lista-de-espera,limite-diario";

        private const string Nivel3 = Nivel2 +
            ",relatorios-avancados,comissoes,ponto-do-time,multiunidade,permissoes-avancadas,recursos-reservaveis";

        private const string Nivel4 = Nivel3 +
            ",onboarding-dedicado,api-publica,gerente-de-conta";

        // O que a migração anterior (RecursosPorPlano) havia gravado.
        private const string Anterior1 =
            "agendamentos,clientes,catalogo,vendas,pagina-online,lembrete-email";

        private const string Anterior2 = Anterior1 +
            ",multi-usuario,jornada-por-pessoa,politica-cancelamento,cartao-em-arquivo" +
            ",lembrete-sms-whatsapp,google-calendar,agendamento-recorrente,sem-marca";

        private const string Anterior3 = Anterior2 +
            ",relatorios-avancados,comissoes,multiunidade,permissoes-avancadas" +
            ",recursos-reservaveis";

        private const string Anterior4 = Anterior3 +
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
            Aplicar(migrationBuilder, "BASIC", Anterior1);
            Aplicar(migrationBuilder, "PLATINUM", Anterior2);
            Aplicar(migrationBuilder, "ULTIMATE", Anterior3);
            Aplicar(migrationBuilder, "CUSTOM", Anterior4);
        }

        private static void Aplicar(MigrationBuilder builder, string codigo, string recursos) =>
            builder.Sql($"UPDATE planos SET recursos = '{recursos}' WHERE codigo = '{codigo}';");
    }
}

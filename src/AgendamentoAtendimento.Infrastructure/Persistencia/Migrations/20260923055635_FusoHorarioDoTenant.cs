using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <summary>
    /// Até aqui a Api gravava a hora de parede da empresa como se fosse UTC: o
    /// atendimento das 09:00 de São Paulo ia para o banco como 09:00Z. Agora todo
    /// timestamptz é instante de verdade, e o horário de funcionamento é lido no fuso do
    /// tenant — então o que já está gravado precisa virar o instante que sempre quis dizer.
    ///
    /// Só mexe no que é hora de parede:
    /// - agendamentos.inicio e agendamentos.fim — saíam da grade, montada em "UTC";
    /// - lembretes.quando_enviar dos lembretes de antecedência (tipo 2) — é inicio − N h.
    ///   O aviso de confirmação (tipo 1) nasce com o "agora" real e fica como está.
    ///
    /// O resto é instante desde sempre (criado_em, confirmado_em, expira_em, avisado_em,
    /// ciclos de assinatura...), e datas e horas de parede sem fuso (date, time) não
    /// mudam de significado.
    ///
    /// Fuso que o PostgreSQL não conhece cai em America/Sao_Paulo, como na Api.
    /// </summary>
    public partial class FusoHorarioDoTenant : Migration
    {
        private const string Fusos = """
            WITH fusos AS (
                SELECT t.id AS tenant_id,
                       COALESCE(
                           (SELECT z.name FROM pg_timezone_names z WHERE z.name = t.fuso_horario),
                           'America/Sao_Paulo') AS fuso
                FROM tenants t
            )
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // (x AT TIME ZONE 'UTC') tira a hora de parede que estava gravada; AT TIME ZONE
            // fuso a lê como hora da empresa e devolve o instante.
            migrationBuilder.Sql($"""
                {Fusos}
                UPDATE agendamentos a
                SET inicio = (a.inicio AT TIME ZONE 'UTC') AT TIME ZONE f.fuso,
                    fim = (a.fim AT TIME ZONE 'UTC') AT TIME ZONE f.fuso
                FROM fusos f
                WHERE a.tenant_id = f.tenant_id;
                """);

            migrationBuilder.Sql($"""
                {Fusos}
                UPDATE lembretes l
                SET quando_enviar = (l.quando_enviar AT TIME ZONE 'UTC') AT TIME ZONE f.fuso
                FROM fusos f
                WHERE l.tenant_id = f.tenant_id AND l.tipo = 2;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // O caminho de volta: o instante lido no relógio da empresa, gravado como UTC.
            migrationBuilder.Sql($"""
                {Fusos}
                UPDATE agendamentos a
                SET inicio = (a.inicio AT TIME ZONE f.fuso) AT TIME ZONE 'UTC',
                    fim = (a.fim AT TIME ZONE f.fuso) AT TIME ZONE 'UTC'
                FROM fusos f
                WHERE a.tenant_id = f.tenant_id;
                """);

            migrationBuilder.Sql($"""
                {Fusos}
                UPDATE lembretes l
                SET quando_enviar = (l.quando_enviar AT TIME ZONE f.fuso) AT TIME ZONE 'UTC'
                FROM fusos f
                WHERE l.tenant_id = f.tenant_id AND l.tipo = 2;
                """);
        }
    }
}

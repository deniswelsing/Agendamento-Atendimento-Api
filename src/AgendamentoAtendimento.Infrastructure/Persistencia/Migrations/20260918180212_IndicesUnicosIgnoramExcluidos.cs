using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class IndicesUnicosIgnoramExcluidos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_usuarios_tenant_id_email",
                table: "usuarios");

            migrationBuilder.DropIndex(
                name: "ix_refresh_tokens_token_hash",
                table: "refresh_tokens");

            migrationBuilder.DropIndex(
                name: "ix_perfis_tenant_id_nome",
                table: "perfis");

            migrationBuilder.DropIndex(
                name: "ix_perfil_permissoes_tenant_id_perfil_id_permissao",
                table: "perfil_permissoes");

            migrationBuilder.DropIndex(
                name: "ix_horarios_staff_tenant_id_usuario_id_dia_da_semana",
                table: "horarios_staff");

            migrationBuilder.DropIndex(
                name: "ix_horarios_funcionamento_tenant_id_dia_da_semana",
                table: "horarios_funcionamento");

            migrationBuilder.DropIndex(
                name: "ix_formas_pagamento_tenant_id_codigo",
                table: "formas_pagamento");

            migrationBuilder.DropIndex(
                name: "ix_excecoes_horario_funcionamento_tenant_id_data",
                table: "excecoes_horario_funcionamento");

            migrationBuilder.CreateIndex(
                name: "ix_usuarios_tenant_id_email",
                table: "usuarios",
                columns: new[] { "tenant_id", "email" },
                unique: true,
                filter: "excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true,
                filter: "excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_perfis_tenant_id_nome",
                table: "perfis",
                columns: new[] { "tenant_id", "nome" },
                unique: true,
                filter: "excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_perfil_permissoes_tenant_id_perfil_id_permissao",
                table: "perfil_permissoes",
                columns: new[] { "tenant_id", "perfil_id", "permissao" },
                unique: true,
                filter: "excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_horarios_staff_tenant_id_usuario_id_dia_da_semana",
                table: "horarios_staff",
                columns: new[] { "tenant_id", "usuario_id", "dia_da_semana" },
                unique: true,
                filter: "excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_horarios_funcionamento_tenant_id_dia_da_semana",
                table: "horarios_funcionamento",
                columns: new[] { "tenant_id", "dia_da_semana" },
                unique: true,
                filter: "excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_formas_pagamento_tenant_id_codigo",
                table: "formas_pagamento",
                columns: new[] { "tenant_id", "codigo" },
                unique: true,
                filter: "excluido = false");

            migrationBuilder.CreateIndex(
                name: "ix_excecoes_horario_funcionamento_tenant_id_data",
                table: "excecoes_horario_funcionamento",
                columns: new[] { "tenant_id", "data" },
                unique: true,
                filter: "excluido = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_usuarios_tenant_id_email",
                table: "usuarios");

            migrationBuilder.DropIndex(
                name: "ix_refresh_tokens_token_hash",
                table: "refresh_tokens");

            migrationBuilder.DropIndex(
                name: "ix_perfis_tenant_id_nome",
                table: "perfis");

            migrationBuilder.DropIndex(
                name: "ix_perfil_permissoes_tenant_id_perfil_id_permissao",
                table: "perfil_permissoes");

            migrationBuilder.DropIndex(
                name: "ix_horarios_staff_tenant_id_usuario_id_dia_da_semana",
                table: "horarios_staff");

            migrationBuilder.DropIndex(
                name: "ix_horarios_funcionamento_tenant_id_dia_da_semana",
                table: "horarios_funcionamento");

            migrationBuilder.DropIndex(
                name: "ix_formas_pagamento_tenant_id_codigo",
                table: "formas_pagamento");

            migrationBuilder.DropIndex(
                name: "ix_excecoes_horario_funcionamento_tenant_id_data",
                table: "excecoes_horario_funcionamento");

            migrationBuilder.CreateIndex(
                name: "ix_usuarios_tenant_id_email",
                table: "usuarios",
                columns: new[] { "tenant_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_perfis_tenant_id_nome",
                table: "perfis",
                columns: new[] { "tenant_id", "nome" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_perfil_permissoes_tenant_id_perfil_id_permissao",
                table: "perfil_permissoes",
                columns: new[] { "tenant_id", "perfil_id", "permissao" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_horarios_staff_tenant_id_usuario_id_dia_da_semana",
                table: "horarios_staff",
                columns: new[] { "tenant_id", "usuario_id", "dia_da_semana" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_horarios_funcionamento_tenant_id_dia_da_semana",
                table: "horarios_funcionamento",
                columns: new[] { "tenant_id", "dia_da_semana" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_formas_pagamento_tenant_id_codigo",
                table: "formas_pagamento",
                columns: new[] { "tenant_id", "codigo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_excecoes_horario_funcionamento_tenant_id_data",
                table: "excecoes_horario_funcionamento",
                columns: new[] { "tenant_id", "data" },
                unique: true);
        }
    }
}

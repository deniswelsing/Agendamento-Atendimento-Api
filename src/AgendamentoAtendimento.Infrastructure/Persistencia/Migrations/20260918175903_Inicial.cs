using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class Inicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    usuario_id = table.Column<long>(type: "bigint", nullable: true),
                    entidade = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    chave_entidade = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    operacao = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    alteracoes = table.Column<string>(type: "jsonb", nullable: true),
                    produto = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    criado_por_id = table.Column<long>(type: "bigint", nullable: true),
                    atualizado_por_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "clientes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tipo = table.Column<int>(type: "integer", nullable: false),
                    nome = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    sobrenome = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    razao_social = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    nome_fantasia = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    inscricao_estadual = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    responsavel = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    documento = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    telefone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    celular = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    whats_app = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    logradouro = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    numero = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    complemento = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    bairro = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    municipio = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    estado = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    pais = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    cep = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    latitude = table.Column<double>(type: "double precision", nullable: true),
                    longitude = table.Column<double>(type: "double precision", nullable: true),
                    ativo = table.Column<bool>(type: "boolean", nullable: false),
                    vip = table.Column<bool>(type: "boolean", nullable: false),
                    observacoes = table.Column<string>(type: "text", nullable: true),
                    foto_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    aceita_email = table.Column<bool>(type: "boolean", nullable: false),
                    aceita_whats_app = table.Column<bool>(type: "boolean", nullable: false),
                    aceita_marketing = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_clientes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "eventos_gateway",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    gateway = table.Column<int>(type: "integer", nullable: false),
                    evento_externo_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tipo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: true),
                    assinatura_id = table.Column<long>(type: "bigint", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    processado = table.Column<bool>(type: "boolean", nullable: false),
                    processado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    erro = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    criado_por_id = table.Column<long>(type: "bigint", nullable: true),
                    atualizado_por_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eventos_gateway", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "excecoes_horario_funcionamento",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data = table.Column<DateOnly>(type: "date", nullable: false),
                    fechado = table.Column<bool>(type: "boolean", nullable: false),
                    abertura = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    fechamento = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    pausa_inicio = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    pausa_fim = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    motivo = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
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
                    table.PrimaryKey("pk_excecoes_horario_funcionamento", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "formas_pagamento",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    nome = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    codigo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ativa = table.Column<bool>(type: "boolean", nullable: false),
                    permite_parcelamento = table.Column<bool>(type: "boolean", nullable: false),
                    maximo_parcelas = table.Column<int>(type: "integer", nullable: false),
                    taxa_percentual = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    taxa_fixa = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    dias_para_liquidacao = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_formas_pagamento", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "horarios_funcionamento",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    dia_da_semana = table.Column<int>(type: "integer", nullable: false),
                    abertura = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    fechamento = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    pausa_inicio = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    pausa_fim = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    tipo_dia = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    aberto = table.Column<bool>(type: "boolean", nullable: false),
                    intervalo_slot_minutos = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_horarios_funcionamento", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "itens_catalogo",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tipo = table.Column<int>(type: "integer", nullable: false),
                    nome = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    descricao = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    categoria = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    preco = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    custo = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    duracao_minutos = table.Column<int>(type: "integer", nullable: true),
                    estoque = table.Column<int>(type: "integer", nullable: true),
                    codigo_de_barras = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    imagem_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ativo = table.Column<bool>(type: "boolean", nullable: false),
                    comissao_percentual = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    taxa_percentual = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
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
                    table.PrimaryKey("pk_itens_catalogo", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "perfis",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    nome = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    descricao = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    de_sistema = table.Column<bool>(type: "boolean", nullable: false),
                    administrador = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_perfis", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "planos",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    codigo = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    nome = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    descricao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    preco_mensal_usd = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    preco_anual_usd = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    usuarios_incluidos = table.Column<int>(type: "integer", nullable: false),
                    limite_usuarios = table.Column<int>(type: "integer", nullable: true),
                    ordem = table.Column<int>(type: "integer", nullable: false),
                    ativo = table.Column<bool>(type: "boolean", nullable: false),
                    is_custom = table.Column<bool>(type: "boolean", nullable: false),
                    recursos = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    paddle_price_id_mensal = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    paddle_price_id_anual = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    play_product_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    play_base_plan_id_mensal = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    play_base_plan_id_anual = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    criado_por_id = table.Column<long>(type: "bigint", nullable: true),
                    atualizado_por_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_planos", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    slug = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    nome_empresa = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    documento = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    moeda = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    fuso_horario = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    idioma_padrao = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ativo = table.Column<bool>(type: "boolean", nullable: false),
                    referencia_externa = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    criado_por_id = table.Column<long>(type: "bigint", nullable: true),
                    atualizado_por_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "perfil_permissoes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    perfil_id = table.Column<long>(type: "bigint", nullable: false),
                    permissao = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
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
                    table.PrimaryKey("pk_perfil_permissoes", x => x.id);
                    table.ForeignKey(
                        name: "fk_perfil_permissoes_perfis_perfil_id",
                        column: x => x.perfil_id,
                        principalTable: "perfis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "usuarios",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    nome = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    senha_hash = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    perfil_id = table.Column<long>(type: "bigint", nullable: false),
                    ativo = table.Column<bool>(type: "boolean", nullable: false),
                    convite_pendente = table.Column<bool>(type: "boolean", nullable: false),
                    token_convite = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    convite_expira_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ocupa_assento = table.Column<bool>(type: "boolean", nullable: false),
                    foto_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ultimo_login_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ultimo_login_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    atendente = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_usuarios", x => x.id);
                    table.ForeignKey(
                        name: "fk_usuarios_perfis_perfil_id",
                        column: x => x.perfil_id,
                        principalTable: "perfis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assinaturas",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    plano_id = table.Column<long>(type: "bigint", nullable: false),
                    ciclo = table.Column<int>(type: "integer", nullable: false),
                    gateway = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    assentos_contratados = table.Column<int>(type: "integer", nullable: false),
                    inicio_ciclo_atual = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fim_ciclo_atual = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    proxima_cobranca = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ultimo_pagamento_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    valor_ultima_cobranca_usd = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    fim_periodo_de_graca = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelamento_agendado = table.Column<bool>(type: "boolean", nullable: false),
                    cancelada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    motivo_cancelamento = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    paddle_subscription_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    paddle_customer_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    play_purchase_token_plano = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    play_purchase_token_assentos = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    gerenciamento_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    criado_por_id = table.Column<long>(type: "bigint", nullable: true),
                    atualizado_por_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assinaturas", x => x.id);
                    table.ForeignKey(
                        name: "fk_assinaturas_planos_plano_id",
                        column: x => x.plano_id,
                        principalTable: "planos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_assinaturas_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agendamentos",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    cliente_id = table.Column<long>(type: "bigint", nullable: false),
                    inicio = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fim = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    responsavel_id = table.Column<long>(type: "bigint", nullable: true),
                    observacoes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    local_atendimento = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    motivo_cancelamento = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    iniciado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    concluido_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    venda_id = table.Column<long>(type: "bigint", nullable: true),
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
                    table.PrimaryKey("pk_agendamentos", x => x.id);
                    table.ForeignKey(
                        name: "fk_agendamentos_clientes_cliente_id",
                        column: x => x.cliente_id,
                        principalTable: "clientes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_agendamentos_usuarios_responsavel_id",
                        column: x => x.responsavel_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "excecoes_horario_staff",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    usuario_id = table.Column<long>(type: "bigint", nullable: false),
                    data = table.Column<DateOnly>(type: "date", nullable: false),
                    dia_inteiro = table.Column<bool>(type: "boolean", nullable: false),
                    inicio = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    fim = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    motivo = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
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
                    table.PrimaryKey("pk_excecoes_horario_staff", x => x.id);
                    table.ForeignKey(
                        name: "fk_excecoes_horario_staff_usuarios_usuario_id",
                        column: x => x.usuario_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "horarios_staff",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    usuario_id = table.Column<long>(type: "bigint", nullable: false),
                    dia_da_semana = table.Column<int>(type: "integer", nullable: false),
                    inicio = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    fim = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    pausa_inicio = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    pausa_fim = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    trabalha = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_horarios_staff", x => x.id);
                    table.ForeignKey(
                        name: "fk_horarios_staff_usuarios_usuario_id",
                        column: x => x.usuario_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    usuario_id = table.Column<long>(type: "bigint", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    expira_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revogado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    produto_origem = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    criado_por_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
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
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_usuarios_usuario_id",
                        column: x => x.usuario_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assinatura_produtos",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    assinatura_id = table.Column<long>(type: "bigint", nullable: false),
                    produto_chave = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ativo = table.Column<bool>(type: "boolean", nullable: false),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    criado_por_id = table.Column<long>(type: "bigint", nullable: true),
                    atualizado_por_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assinatura_produtos", x => x.id);
                    table.ForeignKey(
                        name: "fk_assinatura_produtos_assinaturas_assinatura_id",
                        column: x => x.assinatura_id,
                        principalTable: "assinaturas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agendamento_itens",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    agendamento_id = table.Column<long>(type: "bigint", nullable: false),
                    item_catalogo_id = table.Column<long>(type: "bigint", nullable: false),
                    nome = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    duracao_minutos = table.Column<int>(type: "integer", nullable: false),
                    quantidade = table.Column<int>(type: "integer", nullable: false),
                    preco_unitario = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
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
                    table.PrimaryKey("pk_agendamento_itens", x => x.id);
                    table.ForeignKey(
                        name: "fk_agendamento_itens_agendamentos_agendamento_id",
                        column: x => x.agendamento_id,
                        principalTable: "agendamentos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agendamento_itens_itens_catalogo_item_catalogo_id",
                        column: x => x.item_catalogo_id,
                        principalTable: "itens_catalogo",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vendas",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    cliente_id = table.Column<long>(type: "bigint", nullable: false),
                    agendamento_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    total_bruto = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    total_descontos = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    total_liquido = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    desconto_geral = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    total_impostos = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    total_pago = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    total_estornado = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    observacao = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    finalizada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_vendas", x => x.id);
                    table.ForeignKey(
                        name: "fk_vendas_agendamentos_agendamento_id",
                        column: x => x.agendamento_id,
                        principalTable: "agendamentos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_vendas_clientes_cliente_id",
                        column: x => x.cliente_id,
                        principalTable: "clientes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pagamentos",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    venda_id = table.Column<long>(type: "bigint", nullable: false),
                    forma_pagamento_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    valor = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    valor_taxa = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    valor_liquido = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    parcela = table.Column<int>(type: "integer", nullable: false),
                    total_parcelas = table.Column<int>(type: "integer", nullable: false),
                    confirmado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    previsao_liquidacao = table.Column<DateOnly>(type: "date", nullable: true),
                    autorizacao = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    observacao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_pagamentos", x => x.id);
                    table.ForeignKey(
                        name: "fk_pagamentos_formas_pagamento_forma_pagamento_id",
                        column: x => x.forma_pagamento_id,
                        principalTable: "formas_pagamento",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pagamentos_vendas_venda_id",
                        column: x => x.venda_id,
                        principalTable: "vendas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "venda_itens",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    venda_id = table.Column<long>(type: "bigint", nullable: false),
                    item_catalogo_id = table.Column<long>(type: "bigint", nullable: false),
                    tipo = table.Column<int>(type: "integer", nullable: false),
                    nome = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    quantidade = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    preco_unitario = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    desconto_valor = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    taxa_percentual = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
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
                    table.PrimaryKey("pk_venda_itens", x => x.id);
                    table.ForeignKey(
                        name: "fk_venda_itens_itens_catalogo_item_catalogo_id",
                        column: x => x.item_catalogo_id,
                        principalTable: "itens_catalogo",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_venda_itens_vendas_venda_id",
                        column: x => x.venda_id,
                        principalTable: "vendas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agendamento_itens_agendamento_id",
                table: "agendamento_itens",
                column: "agendamento_id");

            migrationBuilder.CreateIndex(
                name: "ix_agendamento_itens_item_catalogo_id",
                table: "agendamento_itens",
                column: "item_catalogo_id");

            migrationBuilder.CreateIndex(
                name: "ix_agendamentos_cliente_id",
                table: "agendamentos",
                column: "cliente_id");

            migrationBuilder.CreateIndex(
                name: "ix_agendamentos_responsavel_id",
                table: "agendamentos",
                column: "responsavel_id");

            migrationBuilder.CreateIndex(
                name: "ix_agendamentos_tenant_id_inicio",
                table: "agendamentos",
                columns: new[] { "tenant_id", "inicio" });

            migrationBuilder.CreateIndex(
                name: "ix_agendamentos_tenant_id_responsavel_id_inicio",
                table: "agendamentos",
                columns: new[] { "tenant_id", "responsavel_id", "inicio" });

            migrationBuilder.CreateIndex(
                name: "ix_assinatura_produtos_assinatura_id_produto_chave",
                table: "assinatura_produtos",
                columns: new[] { "assinatura_id", "produto_chave" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assinaturas_paddle_subscription_id",
                table: "assinaturas",
                column: "paddle_subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_assinaturas_plano_id",
                table: "assinaturas",
                column: "plano_id");

            migrationBuilder.CreateIndex(
                name: "ix_assinaturas_tenant_id",
                table: "assinaturas",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_tenant_id_criado_em",
                table: "audit_logs",
                columns: new[] { "tenant_id", "criado_em" });

            migrationBuilder.CreateIndex(
                name: "ix_clientes_tenant_id_documento",
                table: "clientes",
                columns: new[] { "tenant_id", "documento" });

            migrationBuilder.CreateIndex(
                name: "ix_clientes_tenant_id_tipo",
                table: "clientes",
                columns: new[] { "tenant_id", "tipo" });

            migrationBuilder.CreateIndex(
                name: "ix_eventos_gateway_gateway_evento_externo_id",
                table: "eventos_gateway",
                columns: new[] { "gateway", "evento_externo_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_excecoes_horario_funcionamento_tenant_id_data",
                table: "excecoes_horario_funcionamento",
                columns: new[] { "tenant_id", "data" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_excecoes_horario_staff_tenant_id_usuario_id_data",
                table: "excecoes_horario_staff",
                columns: new[] { "tenant_id", "usuario_id", "data" });

            migrationBuilder.CreateIndex(
                name: "ix_excecoes_horario_staff_usuario_id",
                table: "excecoes_horario_staff",
                column: "usuario_id");

            migrationBuilder.CreateIndex(
                name: "ix_formas_pagamento_tenant_id_codigo",
                table: "formas_pagamento",
                columns: new[] { "tenant_id", "codigo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_horarios_funcionamento_tenant_id_dia_da_semana",
                table: "horarios_funcionamento",
                columns: new[] { "tenant_id", "dia_da_semana" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_horarios_staff_tenant_id_usuario_id_dia_da_semana",
                table: "horarios_staff",
                columns: new[] { "tenant_id", "usuario_id", "dia_da_semana" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_horarios_staff_usuario_id",
                table: "horarios_staff",
                column: "usuario_id");

            migrationBuilder.CreateIndex(
                name: "ix_itens_catalogo_tenant_id_tipo_ativo",
                table: "itens_catalogo",
                columns: new[] { "tenant_id", "tipo", "ativo" });

            migrationBuilder.CreateIndex(
                name: "ix_pagamentos_forma_pagamento_id",
                table: "pagamentos",
                column: "forma_pagamento_id");

            migrationBuilder.CreateIndex(
                name: "ix_pagamentos_tenant_id_status",
                table: "pagamentos",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_pagamentos_venda_id",
                table: "pagamentos",
                column: "venda_id");

            migrationBuilder.CreateIndex(
                name: "ix_perfil_permissoes_perfil_id",
                table: "perfil_permissoes",
                column: "perfil_id");

            migrationBuilder.CreateIndex(
                name: "ix_perfil_permissoes_tenant_id_perfil_id_permissao",
                table: "perfil_permissoes",
                columns: new[] { "tenant_id", "perfil_id", "permissao" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_perfis_tenant_id_nome",
                table: "perfis",
                columns: new[] { "tenant_id", "nome" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_planos_codigo",
                table: "planos",
                column: "codigo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_usuario_id",
                table: "refresh_tokens",
                column: "usuario_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenants_referencia_externa",
                table: "tenants",
                column: "referencia_externa");

            migrationBuilder.CreateIndex(
                name: "ix_tenants_slug",
                table: "tenants",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usuarios_perfil_id",
                table: "usuarios",
                column: "perfil_id");

            migrationBuilder.CreateIndex(
                name: "ix_usuarios_tenant_id_email",
                table: "usuarios",
                columns: new[] { "tenant_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_venda_itens_item_catalogo_id",
                table: "venda_itens",
                column: "item_catalogo_id");

            migrationBuilder.CreateIndex(
                name: "ix_venda_itens_venda_id",
                table: "venda_itens",
                column: "venda_id");

            migrationBuilder.CreateIndex(
                name: "ix_vendas_agendamento_id",
                table: "vendas",
                column: "agendamento_id");

            migrationBuilder.CreateIndex(
                name: "ix_vendas_cliente_id",
                table: "vendas",
                column: "cliente_id");

            migrationBuilder.CreateIndex(
                name: "ix_vendas_tenant_id_criado_em",
                table: "vendas",
                columns: new[] { "tenant_id", "criado_em" });

            migrationBuilder.CreateIndex(
                name: "ix_vendas_tenant_id_status",
                table: "vendas",
                columns: new[] { "tenant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agendamento_itens");

            migrationBuilder.DropTable(
                name: "assinatura_produtos");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "eventos_gateway");

            migrationBuilder.DropTable(
                name: "excecoes_horario_funcionamento");

            migrationBuilder.DropTable(
                name: "excecoes_horario_staff");

            migrationBuilder.DropTable(
                name: "horarios_funcionamento");

            migrationBuilder.DropTable(
                name: "horarios_staff");

            migrationBuilder.DropTable(
                name: "pagamentos");

            migrationBuilder.DropTable(
                name: "perfil_permissoes");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "venda_itens");

            migrationBuilder.DropTable(
                name: "assinaturas");

            migrationBuilder.DropTable(
                name: "formas_pagamento");

            migrationBuilder.DropTable(
                name: "itens_catalogo");

            migrationBuilder.DropTable(
                name: "vendas");

            migrationBuilder.DropTable(
                name: "planos");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.DropTable(
                name: "agendamentos");

            migrationBuilder.DropTable(
                name: "clientes");

            migrationBuilder.DropTable(
                name: "usuarios");

            migrationBuilder.DropTable(
                name: "perfis");
        }
    }
}

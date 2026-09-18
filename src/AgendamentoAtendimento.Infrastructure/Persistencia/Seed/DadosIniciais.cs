using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.MultiTenancy;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Seguranca;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Seed;

/// <summary>
/// Dados mínimos para a Api subir: os planos (globais) e, opcionalmente, um tenant de
/// demonstração com time, horários, catálogo e assinatura ativa.
/// </summary>
public static class DadosIniciais
{
    public const string SlugDemo = "route-servicos";
    public const string EmailAdminDemo = "denis@routeservicos.com";

    /// <summary>Aplica as permissões já normalizadas pelo catálogo.</summary>
    private static void AtribuirPermissoes(Perfil perfil, IEnumerable<string> permissoes)
    {
        foreach (var permissao in Permissoes.ComTelasImplicadas(Permissoes.Sanitizar(permissoes)))
        {
            perfil.Permissoes.Add(new PerfilPermissao { Permissao = permissao });
        }
    }

    /// <summary>Planos espelhando a tabela `Planos` do PetShop.Route. Preços em USD.</summary>
    public static async Task GarantirPlanosAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.Planos.AnyAsync(ct))
        {
            return;
        }

        db.Planos.AddRange(
            new Plano
            {
                Codigo = "BASIC", Nome = "Basic", Ordem = 1,
                Descricao = "Para começar, com o essencial",
                PrecoMensalUsd = 0m, PrecoAnualUsd = 0m,
                UsuariosIncluidos = 1, LimiteUsuarios = 1,
                Recursos = CatalogoRecursos.ListaAteNivel(1),
            },
            new Plano
            {
                Codigo = "PLATINUM", Nome = "Platinum", Ordem = 2,
                Descricao = "Para operações em crescimento",
                PrecoMensalUsd = 99m, PrecoAnualUsd = 948m,
                UsuariosIncluidos = 5, LimiteUsuarios = 25,
                Recursos = CatalogoRecursos.ListaAteNivel(2),
                PaddlePriceIdMensal = "pri_platinum_mensal", PaddlePriceIdAnual = "pri_platinum_anual",
                PlayProductId = "plano_platinum",
                PlayBasePlanIdMensal = "mensal", PlayBasePlanIdAnual = "anual",
            },
            new Plano
            {
                Codigo = "ULTIMATE", Nome = "Ultimate", Ordem = 3,
                Descricao = "Para negócios estabelecidos",
                PrecoMensalUsd = 199m, PrecoAnualUsd = 1908m,
                UsuariosIncluidos = 15, LimiteUsuarios = null,
                Recursos = CatalogoRecursos.ListaAteNivel(3),
                PaddlePriceIdMensal = "pri_ultimate_mensal", PaddlePriceIdAnual = "pri_ultimate_anual",
                PlayProductId = "plano_ultimate",
                PlayBasePlanIdMensal = "mensal", PlayBasePlanIdAnual = "anual",
            },
            new Plano
            {
                Codigo = "CUSTOM", Nome = "Custom", Ordem = 4,
                Descricao = "Sob consulta, com gerente dedicado",
                PrecoMensalUsd = null, PrecoAnualUsd = null,
                UsuariosIncluidos = 15, LimiteUsuarios = null, IsCustom = true,
                Recursos = CatalogoRecursos.ListaAteNivel(4),
            });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Tenant de demonstração. Só roda quando o banco ainda não tem nenhum tenant, para
    /// não sujar um ambiente real.
    /// </summary>
    public static async Task GarantirTenantDemoAsync(
        AppDbContext db, ContextoAtual contexto, string senhaAdmin, CancellationToken ct = default)
    {
        await GarantirPlanosAsync(db, ct);

        contexto.IgnorarFiltroDeTenant = true;
        try
        {
            if (await db.Tenants.AnyAsync(t => t.Slug == SlugDemo, ct))
            {
                return;
            }

            var tenant = new Tenant
            {
                Slug = SlugDemo,
                NomeEmpresa = "Route Serviços",
                Moeda = "BRL",
                FusoHorario = "America/Sao_Paulo",
            };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(ct);

            tenant.ReferenciaExterna = $"tenant-{tenant.Id}";
            contexto.AssumirTenant(tenant.Id, tenant.Slug);
            contexto.IgnorarFiltroDeTenant = false;

            var admin = new Perfil
            {
                Nome = "Administrador", DeSistema = true, Administrador = true,
                Descricao = "Acesso total ao tenant",
            };
            admin.Permissoes.Add(new PerfilPermissao { Permissao = Permissoes.Coringa });

            var atendimento = new Perfil { Nome = "Atendimento", DeSistema = true, Descricao = "Agenda e clientes" };
            AtribuirPermissoes(atendimento, new[]
            {
                "dashboard.ver",
                "agenda.ver", "agenda.criar", "agenda.editar", "agenda.concluir",
                "clientes.ver", "clientes.criar", "clientes.editar",
                "catalogo.ver",
                "vendas.ver", "vendas.criar",
                "horarios.ver",
            });

            var financeiro = new Perfil { Nome = "Financeiro", DeSistema = true, Descricao = "Vendas e recebimentos" };
            AtribuirPermissoes(financeiro, new[]
            {
                "dashboard.ver",
                "clientes.ver", "catalogo.ver",
                "vendas.ver", "vendas.criar", "vendas.editar", "vendas.finalizar", "vendas.cancelar",
                "financeiro.ver", "financeiro.receber", "financeiro.estornar", "financeiro.formas",
            });

            db.Perfis.AddRange(admin, atendimento, financeiro);
            await db.SaveChangesAsync(ct);

            var usuarios = new List<Usuario>
            {
                new() { Nome = "Denis Welsing", Email = EmailAdminDemo, PerfilId = admin.Id, SenhaHash = HashSenha.Gerar(senhaAdmin) },
                new() { Nome = "Bruna Alves", Email = "bruna@routeservicos.com", PerfilId = atendimento.Id, SenhaHash = HashSenha.Gerar(senhaAdmin) },
                new() { Nome = "Caio Meireles", Email = "caio@routeservicos.com", PerfilId = atendimento.Id, SenhaHash = HashSenha.Gerar(senhaAdmin) },
                new() { Nome = "Íris Camargo", Email = "iris@routeservicos.com", PerfilId = atendimento.Id, SenhaHash = HashSenha.Gerar(senhaAdmin) },
                new() { Nome = "Rita Nogueira", Email = "rita@routeservicos.com", PerfilId = financeiro.Id, SenhaHash = HashSenha.Gerar(senhaAdmin), Atendente = false },
            };
            db.Usuarios.AddRange(usuarios);
            await db.SaveChangesAsync(ct);

            // Funcionamento: segunda a sexta 08-18 com pausa, sábado 09-13, domingo fechado.
            for (var dia = DayOfWeek.Sunday; dia <= DayOfWeek.Saturday; dia++)
            {
                var util = dia is >= DayOfWeek.Monday and <= DayOfWeek.Friday;
                db.HorariosFuncionamento.Add(new HorarioFuncionamento
                {
                    DiaDaSemana = dia,
                    Aberto = dia != DayOfWeek.Sunday,
                    Abertura = dia == DayOfWeek.Sunday ? null : new TimeOnly(util ? 8 : 9, 0),
                    Fechamento = dia == DayOfWeek.Sunday ? null : new TimeOnly(util ? 18 : 13, 0),
                    PausaInicio = util ? new TimeOnly(12, 0) : null,
                    PausaFim = util ? new TimeOnly(13, 0) : null,
                    IntervaloSlotMinutos = 30,
                });
            }

            foreach (var atendente in usuarios.Where(u => u.Atendente))
            {
                for (var dia = DayOfWeek.Monday; dia <= DayOfWeek.Friday; dia++)
                {
                    db.HorariosStaff.Add(new HorarioStaff
                    {
                        UsuarioId = atendente.Id,
                        DiaDaSemana = dia,
                        Inicio = new TimeOnly(8, 0),
                        Fim = new TimeOnly(17, 0),
                        PausaInicio = new TimeOnly(12, 0),
                        PausaFim = new TimeOnly(13, 0),
                    });
                }
            }

            db.FormasPagamento.AddRange(
                new FormaPagamento { Nome = "Dinheiro", Codigo = "DINHEIRO" },
                new FormaPagamento { Nome = "PIX", Codigo = "PIX" },
                new FormaPagamento { Nome = "Cartão de débito", Codigo = "DEBITO", TaxaPercentual = 1.99m, DiasParaLiquidacao = 1 },
                new FormaPagamento { Nome = "Cartão de crédito", Codigo = "CREDITO", TaxaPercentual = 3.49m, DiasParaLiquidacao = 30, PermiteParcelamento = true, MaximoParcelas = 12 });

            db.ItensCatalogo.AddRange(
                new ItemCatalogo { Tipo = TipoItem.Servico, Nome = "Consultoria inicial", Preco = 320m, DuracaoMinutos = 45, ComissaoPercentual = 10m },
                new ItemCatalogo { Tipo = TipoItem.Servico, Nome = "Auditoria de estoque", Preco = 890m, DuracaoMinutos = 90, ComissaoPercentual = 12m },
                new ItemCatalogo { Tipo = TipoItem.Servico, Nome = "Treinamento de equipe", Preco = 640m, DuracaoMinutos = 60, ComissaoPercentual = 12m },
                new ItemCatalogo { Tipo = TipoItem.Servico, Nome = "Revisão de contrato", Preco = 410m, DuracaoMinutos = 45, ComissaoPercentual = 8m },
                new ItemCatalogo { Tipo = TipoItem.Produto, Nome = "Leitor de código de barras", Preco = 289.90m, Custo = 180m, Estoque = 12 },
                new ItemCatalogo { Tipo = TipoItem.Produto, Nome = "Bobina térmica 80mm (cx. 30)", Preco = 174.50m, Custo = 96m, Estoque = 48 });

            db.Clientes.AddRange(
                new Cliente
                {
                    Tipo = TipoCliente.Empresa, RazaoSocial = "Atacadão Vale Verde Comércio Ltda.",
                    NomeFantasia = "Atacadão Vale Verde", Documento = "12884310000142",
                    Responsavel = "Cláudia Ferrari", Email = "contato@valeverde.com.br",
                    Celular = "51998124477", Municipio = "Porto Alegre", Estado = "RS", Vip = true,
                },
                new Cliente
                {
                    Tipo = TipoCliente.Pessoa, Nome = "Marina", Sobrenome = "Salgado",
                    Documento = "20455188011", Email = "marina.salgado@email.com",
                    Celular = "51996771082", Municipio = "Canoas", Estado = "RS",
                },
                new Cliente
                {
                    Tipo = TipoCliente.Empresa, RazaoSocial = "Studio Lumina Produções ME",
                    NomeFantasia = "Studio Lumina", Documento = "31702559000108",
                    Responsavel = "Heitor Lima", Email = "contato@studiolumina.com.br",
                    Celular = "51302188 90", Municipio = "Porto Alegre", Estado = "RS",
                });

            await db.SaveChangesAsync(ct);

            var platinum = await db.Planos.FirstAsync(p => p.Codigo == "PLATINUM", ct);
            var agora = DateTimeOffset.UtcNow;
            var assinatura = new Assinatura
            {
                TenantId = tenant.Id,
                PlanoId = platinum.Id,
                Ciclo = CicloCobranca.Mensal,
                Gateway = GatewayPagamento.Paddle,
                Status = StatusAssinatura.Ativa,
                AssentosContratados = 8,
                InicioCicloAtual = agora,
                FimCicloAtual = agora.AddMonths(1),
                ProximaCobranca = agora.AddMonths(1),
                ValorUltimaCobrancaUsd = PrecificacaoAssinatura
                    .Calcular(platinum, CicloCobranca.Mensal, 8).Total,
            };
            // A assinatura vale para os dois produtos da suíte.
            foreach (var produto in Produtos.Todos)
            {
                assinatura.Produtos.Add(new AssinaturaProduto { ProdutoChave = produto });
            }
            db.Assinaturas.Add(assinatura);

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            contexto.IgnorarFiltroDeTenant = false;
        }
    }
}

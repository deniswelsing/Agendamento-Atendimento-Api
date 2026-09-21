using System.Linq.Expressions;
using System.Text.Json;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.Auditoria;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Common;
using AgendamentoAtendimento.Domain.MultiTenancy;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace AgendamentoAtendimento.Infrastructure.Persistencia;

public class AppDbContext : DbContext
{
    private readonly IContextoAtual _contexto;

    public AppDbContext(DbContextOptions<AppDbContext> options, IContextoAtual contexto)
        : base(options)
    {
        _contexto = contexto;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Perfil> Perfis => Set<Perfil>();
    public DbSet<PerfilPermissao> PerfilPermissoes => Set<PerfilPermissao>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Cliente> Clientes => Set<Cliente>();
    public DbSet<ItemCatalogo> ItensCatalogo => Set<ItemCatalogo>();
    public DbSet<ExecutorDeServico> ExecutoresDeServico => Set<ExecutorDeServico>();
    public DbSet<ConfiguracaoPaginaPublica> PaginasPublicas => Set<ConfiguracaoPaginaPublica>();
    public DbSet<Agendamento> Agendamentos => Set<Agendamento>();
    public DbSet<Turno> Turnos => Set<Turno>();
    public DbSet<EntradaListaDeEspera> ListaDeEspera => Set<EntradaListaDeEspera>();
    public DbSet<ConfiguracaoDeLembrete> ConfiguracoesDeLembrete => Set<ConfiguracaoDeLembrete>();
    public DbSet<LembreteDeAgendamento> Lembretes => Set<LembreteDeAgendamento>();
    public DbSet<AgendamentoItem> AgendamentoItens => Set<AgendamentoItem>();
    public DbSet<HorarioFuncionamento> HorariosFuncionamento => Set<HorarioFuncionamento>();
    public DbSet<ExcecaoHorarioFuncionamento> ExcecoesHorarioFuncionamento => Set<ExcecaoHorarioFuncionamento>();
    public DbSet<HorarioStaff> HorariosStaff => Set<HorarioStaff>();
    public DbSet<ExcecaoHorarioStaff> ExcecoesHorarioStaff => Set<ExcecaoHorarioStaff>();
    public DbSet<Venda> Vendas => Set<Venda>();
    public DbSet<VendaItem> VendaItens => Set<VendaItem>();
    public DbSet<FormaPagamento> FormasPagamento => Set<FormaPagamento>();
    public DbSet<Pagamento> Pagamentos => Set<Pagamento>();
    public DbSet<Cobranca> Cobrancas => Set<Cobranca>();
    public DbSet<Plano> Planos => Set<Plano>();
    public DbSet<Assinatura> Assinaturas => Set<Assinatura>();
    public DbSet<AssinaturaProduto> AssinaturaProdutos => Set<AssinaturaProduto>();
    public DbSet<EventoGateway> EventosGateway => Set<EventoGateway>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        foreach (var tipo in modelBuilder.Model.GetEntityTypes())
        {
            // Dinheiro em numeric(18,2): float em coluna de valor é erro caro.
            foreach (var propriedade in tipo.GetProperties()
                         .Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
            {
                propriedade.SetColumnType("numeric(18,2)");
            }

            if (typeof(IEntidadeDeTenant).IsAssignableFrom(tipo.ClrType))
            {
                AplicarFiltroDeTenant(modelBuilder, tipo.ClrType);
            }
        }
    }

    /// <summary>
    /// Todo SELECT em entidade de tenant já sai filtrado pelo tenant da requisição e sem os
    /// registros excluídos logicamente. Nenhum repositório precisa lembrar disso.
    /// </summary>
    private void AplicarFiltroDeTenant(ModelBuilder modelBuilder, Type clrType)
    {
        var parametro = Expression.Parameter(clrType, "e");
        var contexto = Expression.Constant(this);
        // Lido a cada consulta: o filtro acompanha o tenant da requisição corrente.
        var contextoAtual = Expression.Property(contexto, nameof(Contexto));
        var tenantAtual = Expression.Property(contextoAtual, nameof(IContextoAtual.TenantId));

        Expression corpo = Expression.OrElse(
            Expression.Property(contextoAtual, nameof(IContextoAtual.IgnorarFiltroDeTenant)),
            Expression.Equal(
                Expression.Convert(Expression.Property(parametro, nameof(IEntidadeDeTenant.TenantId)), typeof(long?)),
                tenantAtual));

        if (typeof(IExclusaoLogica).IsAssignableFrom(clrType))
        {
            corpo = Expression.AndAlso(
                corpo,
                Expression.Not(Expression.Property(parametro, nameof(IExclusaoLogica.Excluido))));
        }

        modelBuilder.Entity(clrType).HasQueryFilter(Expression.Lambda(corpo, parametro));
    }

    /// <summary>Exposto para o filtro global — a expressão acima lê esta propriedade.</summary>
    public IContextoAtual Contexto => _contexto;

    public override int SaveChanges()
        => SaveChangesAsync().GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var agora = DateTimeOffset.UtcNow;
        var trilha = new List<AuditLog>();

        foreach (var entrada in ChangeTracker.Entries<Entidade>())
        {
            if (entrada.State is EntityState.Added)
            {
                entrada.Entity.CriadoEm = agora;
                entrada.Entity.CriadoPorId ??= _contexto.UsuarioId;
                PreencherTenant(entrada);
            }
            else if (entrada.State is EntityState.Modified)
            {
                entrada.Entity.AtualizadoEm = agora;
                entrada.Entity.AtualizadoPorId = _contexto.UsuarioId;
            }

            // Exclusão de entidade com soft delete vira UPDATE.
            if (entrada.State is EntityState.Deleted && entrada.Entity is IExclusaoLogica logica)
            {
                entrada.State = EntityState.Modified;
                logica.Excluido = true;
                logica.ExcluidoEm = agora;
            }

            if (entrada.Entity is not AuditLog && entrada.Entity is IEntidadeDeTenant)
            {
                var registro = MontarAuditoria(entrada);
                if (registro is not null)
                {
                    trilha.Add(registro);
                }
            }
        }

        if (trilha.Count > 0)
        {
            AuditLogs.AddRange(trilha);
        }

        return await base.SaveChangesAsync(cancellationToken);
    }

    private void PreencherTenant(EntityEntry<Entidade> entrada)
    {
        if (entrada.Entity is not IEntidadeDeTenant doTenant || doTenant.TenantId != 0)
        {
            return;
        }

        doTenant.TenantId = _contexto.TenantId
            ?? throw new InvalidOperationException(
                $"Tentativa de gravar {entrada.Entity.GetType().Name} sem tenant no contexto.");
    }

    private AuditLog? MontarAuditoria(EntityEntry<Entidade> entrada)
    {
        var operacao = entrada.State switch
        {
            EntityState.Added => "INSERT",
            EntityState.Modified => "UPDATE",
            EntityState.Deleted => "DELETE",
            _ => null,
        };
        if (operacao is null)
        {
            return null;
        }

        var alteracoes = new Dictionary<string, object?>();
        foreach (var propriedade in entrada.Properties)
        {
            var nome = propriedade.Metadata.Name;
            if (nome.Contains("Senha", StringComparison.OrdinalIgnoreCase) ||
                nome.Contains("Token", StringComparison.OrdinalIgnoreCase))
            {
                continue; // nunca registrar segredo na trilha
            }

            if (entrada.State is EntityState.Modified && !propriedade.IsModified)
            {
                continue;
            }

            alteracoes[nome] = propriedade.CurrentValue;
        }

        return new AuditLog
        {
            TenantId = (entrada.Entity as IEntidadeDeTenant)?.TenantId ?? _contexto.TenantId ?? 0,
            UsuarioId = _contexto.UsuarioId,
            Entidade = entrada.Entity.GetType().Name,
            ChaveEntidade = entrada.Entity.Id.ToString(),
            Operacao = operacao,
            Produto = _contexto.Produto,
            Ip = _contexto.Ip,
            Alteracoes = alteracoes.Count == 0 ? null : JsonSerializer.Serialize(alteracoes),
        };
    }
}

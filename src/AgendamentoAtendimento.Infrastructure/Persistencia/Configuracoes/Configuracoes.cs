using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.Auditoria;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.MultiTenancy;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Domain.Vendas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgendamentoAtendimento.Infrastructure.Persistencia.Configuracoes;

/// <summary>
/// Filtro dos índices únicos em entidades com exclusão lógica: uma linha já excluída não
/// pode continuar segurando a chave (um e-mail removido precisa poder ser cadastrado de novo).
/// </summary>
internal static class Indices
{
    public const string SomenteAtivos = "excluido = false";
}

public class TenantConfig : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.Property(t => t.Slug).HasMaxLength(60).IsRequired();
        b.Property(t => t.NomeEmpresa).HasMaxLength(200).IsRequired();
        b.Property(t => t.Documento).HasMaxLength(20);
        b.Property(t => t.Moeda).HasMaxLength(3).IsRequired();
        b.Property(t => t.FusoHorario).HasMaxLength(60).IsRequired();
        b.Property(t => t.IdiomaPadrao).HasMaxLength(10).IsRequired();
        b.Property(t => t.ReferenciaExterna).HasMaxLength(64);
        // Inteiro, como os outros enums do esquema: o valor é estável e o nome não.
        b.Property(t => t.ModoDeOcupacao).HasConversion<int>().IsRequired();
        b.HasIndex(t => t.Slug).IsUnique();
        b.HasIndex(t => t.ReferenciaExterna);
    }
}

public class PerfilConfig : IEntityTypeConfiguration<Perfil>
{
    public void Configure(EntityTypeBuilder<Perfil> b)
    {
        b.Property(p => p.Nome).HasMaxLength(80).IsRequired();
        b.Property(p => p.Descricao).HasMaxLength(300);
        b.HasIndex(p => new { p.TenantId, p.Nome }).IsUnique().HasFilter(Indices.SomenteAtivos);
        b.HasMany(p => p.Permissoes).WithOne(x => x.Perfil!).HasForeignKey(x => x.PerfilId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class PerfilPermissaoConfig : IEntityTypeConfiguration<PerfilPermissao>
{
    public void Configure(EntityTypeBuilder<PerfilPermissao> b)
    {
        b.Property(p => p.Permissao).HasMaxLength(80).IsRequired();
        b.HasIndex(p => new { p.TenantId, p.PerfilId, p.Permissao }).IsUnique().HasFilter(Indices.SomenteAtivos);
    }
}

public class UsuarioConfig : IEntityTypeConfiguration<Usuario>
{
    public void Configure(EntityTypeBuilder<Usuario> b)
    {
        b.Property(u => u.Nome).HasMaxLength(150).IsRequired();
        b.Property(u => u.Email).HasMaxLength(200).IsRequired();
        b.Property(u => u.SenhaHash).HasMaxLength(400);
        b.Property(u => u.TokenConvite).HasMaxLength(120);
        b.Property(u => u.FotoUrl).HasMaxLength(500);
        b.Property(u => u.UltimoLoginIp).HasMaxLength(64);
        b.HasIndex(u => new { u.TenantId, u.Email }).IsUnique().HasFilter(Indices.SomenteAtivos);
        b.HasOne(u => u.Perfil).WithMany(p => p.Usuarios).HasForeignKey(u => u.PerfilId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class RefreshTokenConfig : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();
        b.Property(t => t.ProdutoOrigem).HasMaxLength(60).IsRequired();
        b.Property(t => t.CriadoPorIp).HasMaxLength(64);
        b.HasIndex(t => t.TokenHash).IsUnique().HasFilter(Indices.SomenteAtivos);
        b.HasOne(t => t.Usuario).WithMany().HasForeignKey(t => t.UsuarioId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ClienteConfig : IEntityTypeConfiguration<Cliente>
{
    public void Configure(EntityTypeBuilder<Cliente> b)
    {
        b.Property(c => c.Nome).HasMaxLength(150);
        b.Property(c => c.Sobrenome).HasMaxLength(150);
        b.Property(c => c.RazaoSocial).HasMaxLength(250);
        b.Property(c => c.NomeFantasia).HasMaxLength(250);
        b.Property(c => c.InscricaoEstadual).HasMaxLength(30);
        b.Property(c => c.Responsavel).HasMaxLength(150);
        b.Property(c => c.Documento).HasMaxLength(20);
        b.Property(c => c.Email).HasMaxLength(200);
        b.Property(c => c.Telefone).HasMaxLength(30);
        b.Property(c => c.Celular).HasMaxLength(30);
        b.Property(c => c.WhatsApp).HasMaxLength(30);
        b.Property(c => c.Logradouro).HasMaxLength(250);
        b.Property(c => c.Numero).HasMaxLength(20);
        b.Property(c => c.Complemento).HasMaxLength(120);
        b.Property(c => c.Bairro).HasMaxLength(120);
        b.Property(c => c.Municipio).HasMaxLength(120);
        b.Property(c => c.Estado).HasMaxLength(60);
        b.Property(c => c.Pais).HasMaxLength(60);
        b.Property(c => c.Cep).HasMaxLength(20);
        b.Property(c => c.FotoUrl).HasMaxLength(500);
        b.Ignore(c => c.NomeExibicao);
        b.HasIndex(c => new { c.TenantId, c.Documento });
        b.HasIndex(c => new { c.TenantId, c.Tipo });
    }
}

public class ItemCatalogoConfig : IEntityTypeConfiguration<ItemCatalogo>
{
    public void Configure(EntityTypeBuilder<ItemCatalogo> b)
    {
        b.Property(i => i.Nome).HasMaxLength(200).IsRequired();
        b.Property(i => i.Descricao).HasMaxLength(1000);
        b.Property(i => i.Categoria).HasMaxLength(120);
        b.Property(i => i.CodigoDeBarras).HasMaxLength(60);
        b.Property(i => i.ImagemUrl).HasMaxLength(500);
        b.Ignore(i => i.Agendavel);
        b.HasIndex(i => new { i.TenantId, i.Tipo, i.Ativo });
    }
}

public class AgendamentoConfig : IEntityTypeConfiguration<Agendamento>
{
    public void Configure(EntityTypeBuilder<Agendamento> b)
    {
        b.Property(a => a.Observacoes).HasMaxLength(1000);
        b.Property(a => a.LocalAtendimento).HasMaxLength(250);
        b.Property(a => a.MotivoCancelamento).HasMaxLength(500);
        b.Property(a => a.CodigoPublico).HasMaxLength(20);
        b.Ignore(a => a.DuracaoMinutos);
        b.HasOne(a => a.Cliente).WithMany().HasForeignKey(a => a.ClienteId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(a => a.Responsavel).WithMany().HasForeignKey(a => a.ResponsavelId)
            .OnDelete(DeleteBehavior.SetNull);
        b.HasMany(a => a.Itens).WithOne(i => i.Agendamento!).HasForeignKey(i => i.AgendamentoId)
            .OnDelete(DeleteBehavior.Cascade);
        // A consulta mais quente da agenda: período + responsável.
        b.HasIndex(a => new { a.TenantId, a.Inicio });
        b.HasIndex(a => new { a.TenantId, a.ResponsavelId, a.Inicio });
        // O código é o que protege a consulta e o cancelamento de quem não tem conta.
        // Único para que dois clientes nunca caiam no agendamento um do outro.
        b.HasIndex(a => a.CodigoPublico)
            .IsUnique()
            .HasFilter("codigo_publico IS NOT NULL AND excluido = false");
    }
}

public class AgendamentoItemConfig : IEntityTypeConfiguration<AgendamentoItem>
{
    public void Configure(EntityTypeBuilder<AgendamentoItem> b)
    {
        b.Property(i => i.Nome).HasMaxLength(200).IsRequired();
        b.HasOne(i => i.ItemCatalogo).WithMany().HasForeignKey(i => i.ItemCatalogoId)
            .OnDelete(DeleteBehavior.Restrict);
        // Quem presta ESTE serviço. Sair do time não apaga o histórico do agendamento.
        b.HasOne(i => i.Responsavel).WithMany().HasForeignKey(i => i.ResponsavelId)
            .OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(i => new { i.TenantId, i.ResponsavelId });
    }
}

public class HorarioFuncionamentoConfig : IEntityTypeConfiguration<HorarioFuncionamento>
{
    public void Configure(EntityTypeBuilder<HorarioFuncionamento> b)
    {
        b.Property(h => h.TipoDia).HasMaxLength(30).IsRequired();
        b.HasIndex(h => new { h.TenantId, h.DiaDaSemana }).IsUnique().HasFilter(Indices.SomenteAtivos);
    }
}

public class ExcecaoHorarioFuncionamentoConfig : IEntityTypeConfiguration<ExcecaoHorarioFuncionamento>
{
    public void Configure(EntityTypeBuilder<ExcecaoHorarioFuncionamento> b)
    {
        b.Property(e => e.Motivo).HasMaxLength(250);
        b.HasIndex(e => new { e.TenantId, e.Data }).IsUnique().HasFilter(Indices.SomenteAtivos);
    }
}

public class HorarioStaffConfig : IEntityTypeConfiguration<HorarioStaff>
{
    public void Configure(EntityTypeBuilder<HorarioStaff> b)
    {
        b.HasOne(h => h.Usuario).WithMany().HasForeignKey(h => h.UsuarioId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(h => new { h.TenantId, h.UsuarioId, h.DiaDaSemana }).IsUnique().HasFilter(Indices.SomenteAtivos);
    }
}

public class ExcecaoHorarioStaffConfig : IEntityTypeConfiguration<ExcecaoHorarioStaff>
{
    public void Configure(EntityTypeBuilder<ExcecaoHorarioStaff> b)
    {
        b.Property(e => e.Motivo).HasMaxLength(250);
        b.HasOne(e => e.Usuario).WithMany().HasForeignKey(e => e.UsuarioId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(e => new { e.TenantId, e.UsuarioId, e.Data });
    }
}

public class ExecutorDeServicoConfig : IEntityTypeConfiguration<ExecutorDeServico>
{
    public void Configure(EntityTypeBuilder<ExecutorDeServico> b)
    {
        b.HasOne(e => e.ItemCatalogo).WithMany().HasForeignKey(e => e.ItemCatalogoId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(e => e.Usuario).WithMany().HasForeignKey(e => e.UsuarioId)
            .OnDelete(DeleteBehavior.Cascade);
        // A mesma pessoa não entra duas vezes no mesmo serviço.
        b.HasIndex(e => new { e.TenantId, e.ItemCatalogoId, e.UsuarioId })
            .IsUnique().HasFilter(Indices.SomenteAtivos);
    }
}

public class ConfiguracaoPaginaPublicaConfig : IEntityTypeConfiguration<ConfiguracaoPaginaPublica>
{
    public void Configure(EntityTypeBuilder<ConfiguracaoPaginaPublica> b)
    {
        b.Property(c => c.Slug).HasMaxLength(60).IsRequired();
        b.Property(c => c.TituloPublico).HasMaxLength(200);
        b.Property(c => c.Mensagem).HasMaxLength(1000);
        b.Property(c => c.Endereco).HasMaxLength(300);
        b.Property(c => c.TelefoneContato).HasMaxLength(30);
        // O slug é a chave do endereço público: precisa ser único no sistema inteiro,
        // não por tenant — dois iguais apontariam para empresas diferentes.
        b.HasIndex(c => c.Slug).IsUnique().HasFilter(Indices.SomenteAtivos);
        // Uma página por empresa.
        b.HasIndex(c => c.TenantId).IsUnique().HasFilter(Indices.SomenteAtivos);
    }
}

public class VendaConfig : IEntityTypeConfiguration<Venda>
{
    public void Configure(EntityTypeBuilder<Venda> b)
    {
        b.Property(v => v.Observacao).HasMaxLength(1000);
        b.Ignore(v => v.SaldoAberto);
        b.Ignore(v => v.TotalComissao);
        b.HasOne(v => v.Vendedor).WithMany().HasForeignKey(v => v.VendedorId)
            .OnDelete(DeleteBehavior.SetNull);
        b.HasOne(v => v.Cliente).WithMany().HasForeignKey(v => v.ClienteId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(v => v.Agendamento).WithMany().HasForeignKey(v => v.AgendamentoId)
            .OnDelete(DeleteBehavior.SetNull);
        b.HasMany(v => v.Itens).WithOne(i => i.Venda!).HasForeignKey(i => i.VendaId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasMany(v => v.Pagamentos).WithOne(p => p.Venda!).HasForeignKey(p => p.VendaId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(v => new { v.TenantId, v.CriadoEm });
        b.HasIndex(v => new { v.TenantId, v.Status });
    }
}

public class VendaItemConfig : IEntityTypeConfiguration<VendaItem>
{
    public void Configure(EntityTypeBuilder<VendaItem> b)
    {
        b.Property(i => i.Nome).HasMaxLength(200).IsRequired();
        b.Property(i => i.Quantidade).HasColumnType("numeric(18,3)");
        b.Ignore(i => i.TotalBruto);
        b.Ignore(i => i.TotalLiquido);
        b.Ignore(i => i.ComissaoValor);
        b.HasOne(i => i.ItemCatalogo).WithMany().HasForeignKey(i => i.ItemCatalogoId)
            .OnDelete(DeleteBehavior.Restrict);
        // Quem leva a comissão deste item. Sair do time não apaga o histórico da venda.
        b.HasOne(i => i.Vendedor).WithMany().HasForeignKey(i => i.VendedorId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class FormaPagamentoConfig : IEntityTypeConfiguration<FormaPagamento>
{
    public void Configure(EntityTypeBuilder<FormaPagamento> b)
    {
        b.Property(f => f.Nome).HasMaxLength(80).IsRequired();
        b.Property(f => f.Codigo).HasMaxLength(30).IsRequired();
        b.HasIndex(f => new { f.TenantId, f.Codigo }).IsUnique().HasFilter(Indices.SomenteAtivos);
    }
}

public class PagamentoConfig : IEntityTypeConfiguration<Pagamento>
{
    public void Configure(EntityTypeBuilder<Pagamento> b)
    {
        b.Property(p => p.Autorizacao).HasMaxLength(120);
        b.Property(p => p.Observacao).HasMaxLength(500);
        b.Property(p => p.Nsu).HasMaxLength(60);
        b.Property(p => p.Bandeira).HasMaxLength(40);
        b.Property(p => p.UltimosDigitos).HasMaxLength(4);
        b.Property(p => p.AdquirenteChave).HasMaxLength(40);
        b.Ignore(p => p.DivergenciaDaTaxa);
        b.HasOne(p => p.FormaPagamento).WithMany().HasForeignKey(p => p.FormaPagamentoId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(p => new { p.TenantId, p.Status });
        // A conciliação procura pelo NSU: é por ele que a transação é achada no extrato.
        b.HasIndex(p => new { p.TenantId, p.Nsu });
    }
}

public class CobrancaConfig : IEntityTypeConfiguration<Cobranca>
{
    public void Configure(EntityTypeBuilder<Cobranca> b)
    {
        b.Property(c => c.ChaveIdempotencia).HasMaxLength(80).IsRequired();
        b.Property(c => c.AdquirenteChave).HasMaxLength(40);
        b.Property(c => c.TerminalSerie).HasMaxLength(60);
        b.Property(c => c.Nsu).HasMaxLength(60);
        b.Property(c => c.CodigoAutorizacao).HasMaxLength(120);
        b.Property(c => c.Bandeira).HasMaxLength(40);
        b.Property(c => c.UltimosDigitos).HasMaxLength(4);
        b.Property(c => c.TransacaoExternaId).HasMaxLength(120);
        b.Property(c => c.PixCopiaECola).HasMaxLength(2000);
        b.Property(c => c.MotivoRecusa).HasMaxLength(300);
        b.Ignore(c => c.EstaAberta);

        b.HasOne(c => c.Venda).WithMany().HasForeignKey(c => c.VendaId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(c => c.FormaPagamento).WithMany().HasForeignKey(c => c.FormaPagamentoId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(c => c.Pagamento).WithMany().HasForeignKey(c => c.PagamentoId)
            .OnDelete(DeleteBehavior.SetNull);

        // É este índice que impede a cobrança dupla: a mesma chave nunca abre duas.
        b.HasIndex(c => new { c.TenantId, c.ChaveIdempotencia })
            .IsUnique().HasFilter(Indices.SomenteAtivos);
        b.HasIndex(c => new { c.TenantId, c.VendaId, c.Status });
    }
}

public class PlanoConfig : IEntityTypeConfiguration<Plano>
{
    public void Configure(EntityTypeBuilder<Plano> b)
    {
        b.Property(p => p.Codigo).HasMaxLength(40).IsRequired();
        b.Property(p => p.Nome).HasMaxLength(80).IsRequired();
        b.Property(p => p.Descricao).HasMaxLength(500);
        b.Property(p => p.Recursos).HasMaxLength(2000);
        b.Property(p => p.PaddlePriceIdMensal).HasMaxLength(100);
        b.Property(p => p.PaddlePriceIdAnual).HasMaxLength(100);
        b.Property(p => p.PlayProductId).HasMaxLength(100);
        b.Property(p => p.PlayBasePlanIdMensal).HasMaxLength(100);
        b.Property(p => p.PlayBasePlanIdAnual).HasMaxLength(100);
        b.HasIndex(p => p.Codigo).IsUnique();
    }
}

public class AssinaturaConfig : IEntityTypeConfiguration<Assinatura>
{
    public void Configure(EntityTypeBuilder<Assinatura> b)
    {
        b.Property(a => a.MotivoCancelamento).HasMaxLength(500);
        b.Property(a => a.PaddleSubscriptionId).HasMaxLength(100);
        b.Property(a => a.PaddleCustomerId).HasMaxLength(100);
        b.Property(a => a.PlayPurchaseTokenPlano).HasMaxLength(400);
        b.Property(a => a.PlayPurchaseTokenAssentos).HasMaxLength(400);
        b.Property(a => a.GerenciamentoUrl).HasMaxLength(500);
        b.Ignore(a => a.LiberaAcesso);
        b.HasOne(a => a.Tenant).WithMany().HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(a => a.Plano).WithMany().HasForeignKey(a => a.PlanoId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasMany(a => a.Produtos).WithOne(p => p.Assinatura!).HasForeignKey(p => p.AssinaturaId)
            .OnDelete(DeleteBehavior.Cascade);
        // Um tenant tem uma assinatura corrente; o histórico fica em EventoGateway.
        b.HasIndex(a => a.TenantId).IsUnique();
        b.HasIndex(a => a.PaddleSubscriptionId);
    }
}

public class AssinaturaProdutoConfig : IEntityTypeConfiguration<AssinaturaProduto>
{
    public void Configure(EntityTypeBuilder<AssinaturaProduto> b)
    {
        b.Property(p => p.ProdutoChave).HasMaxLength(60).IsRequired();
        b.HasIndex(p => new { p.AssinaturaId, p.ProdutoChave }).IsUnique();
    }
}

public class EventoGatewayConfig : IEntityTypeConfiguration<EventoGateway>
{
    public void Configure(EntityTypeBuilder<EventoGateway> b)
    {
        b.Property(e => e.EventoExternoId).HasMaxLength(200).IsRequired();
        b.Property(e => e.Tipo).HasMaxLength(100).IsRequired();
        b.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();
        b.Property(e => e.Erro).HasMaxLength(2000);
        // Chave de idempotência: webhook reentregue não processa duas vezes.
        b.HasIndex(e => new { e.Gateway, e.EventoExternoId }).IsUnique();
    }
}

public class AuditLogConfig : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.Property(a => a.Entidade).HasMaxLength(120).IsRequired();
        b.Property(a => a.ChaveEntidade).HasMaxLength(60).IsRequired();
        b.Property(a => a.Operacao).HasMaxLength(10).IsRequired();
        b.Property(a => a.Produto).HasMaxLength(60);
        b.Property(a => a.Ip).HasMaxLength(64);
        b.Property(a => a.Alteracoes).HasColumnType("jsonb");
        b.HasIndex(a => new { a.TenantId, a.CriadoEm });
    }
}

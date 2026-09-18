using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>
/// Uma chamada com tudo que a interface precisa para se desenhar: quem é o usuário, quais
/// telas ele vê, o que pode fazer em cada uma, os rótulos dos enums e as listas de apoio.
///
/// O aplicativo não guarda nenhuma dessas decisões — ele lê daqui e obedece.
/// </summary>
[Route("api/bootstrap")]
public class BootstrapController : ControllerBaseApi
{
    private readonly AppDbContext _db;
    private readonly AssinaturaService _assinaturas;

    public BootstrapController(AppDbContext db, AssinaturaService assinaturas)
    {
        _db = db;
        _assinaturas = assinaturas;
    }

    [HttpGet]
    [Authorize]
    public async Task<ActionResult<BootstrapDto>> Obter(CancellationToken ct)
    {
        var usuario = NaoNulo(
            await _db.Usuarios.AsNoTracking()
                .Include(u => u.Perfil!).ThenInclude(p => p.Permissoes)
                .FirstOrDefaultAsync(u => u.Id == UsuarioId, ct),
            "Usuário não encontrado.");

        var tenant = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == TenantId, ct);

        // O produto que está chamando precisa estar coberto pela assinatura compartilhada.
        var acesso = await _assinaturas.VerificarAcessoAsync(Produto, ct);
        if (!acesso.Ok && acesso.Motivo != MotivoRecusa.SemAssinatura)
        {
            throw new AssinaturaExigidaException(
                acesso.Mensagem ?? "Assinatura inativa.", acesso.Motivo);
        }

        var permissoes = AuthController.PermissoesDe(usuario);
        var assinatura = await _assinaturas.ObterAtualAsync(ct);
        var assentosEmUso = await _assinaturas.AssentosEmUsoAsync(ct);

        var podeVerTime = Permissoes.Permite(permissoes, "time.ver");
        var podeVerFinanceiro = Permissoes.Permite(permissoes, "financeiro.ver");
        var podeVerHorarios = Permissoes.Permite(permissoes, "horarios.ver");

        var time = podeVerTime
            ? await _db.Usuarios.AsNoTracking().Include(u => u.Perfil)
                .Where(u => u.Ativo).OrderBy(u => u.Nome).ToListAsync(ct)
            : new List<Usuario>();

        var formas = podeVerFinanceiro
            ? await _db.FormasPagamento.AsNoTracking().Where(f => f.Ativa)
                .OrderBy(f => f.Nome).ToListAsync(ct)
            : new List<FormaPagamento>();

        var horarios = podeVerHorarios
            ? await _db.HorariosFuncionamento.AsNoTracking()
                .OrderBy(h => h.DiaDaSemana).ToListAsync(ct)
            : new List<HorarioFuncionamento>();

        return Ok(new BootstrapDto(
            Usuario: new UsuarioDto(
                usuario.Id, usuario.Nome, usuario.Email, usuario.Perfil?.Nome, usuario.PerfilId,
                permissoes, Permissoes.TelasVisiveis(permissoes), usuario.FotoUrl,
                usuario.Perfil?.Administrador ?? false, usuario.Atendente),
            Tenant: new TenantDto(
                tenant.Id, tenant.Slug, tenant.NomeEmpresa, tenant.Moeda,
                tenant.FusoHorario, tenant.IdiomaPadrao),
            Assinatura: assinatura?.ParaDto(assentosEmUso),
            CatalogoPermissoes: Permissoes.Modulos.Select(m => m.ParaDto()).ToList(),
            Time: time.Select(u => u.ParaDto()).ToList(),
            FormasPagamento: formas.Select(f => f.ParaDto()).ToList(),
            HorarioFuncionamento: horarios.Select(h => h.ParaDto()).ToList(),
            Opcoes: MontarOpcoes()));
    }

    /// <summary>
    /// Rótulos dos enums. Vêm do servidor para que o app não precise traduzir nada e para
    /// que um valor novo apareça sem precisar de nova versão na loja.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<OpcaoDto>> MontarOpcoes() =>
        new Dictionary<string, IReadOnlyList<OpcaoDto>>
        {
            ["tipoCliente"] = new List<OpcaoDto>
            {
                new(nameof(TipoCliente.Pessoa), "Pessoa física"),
                new(nameof(TipoCliente.Empresa), "Empresa"),
            },
            ["tipoItem"] = new List<OpcaoDto>
            {
                new(nameof(TipoItem.Servico), "Serviço"),
                new(nameof(TipoItem.Produto), "Produto"),
            },
            ["statusAgendamento"] = new List<OpcaoDto>
            {
                new(nameof(StatusAgendamento.Agendado), "Agendado"),
                new(nameof(StatusAgendamento.Confirmado), "Confirmado"),
                new(nameof(StatusAgendamento.EmAtendimento), "Em atendimento"),
                new(nameof(StatusAgendamento.Concluido), "Concluído"),
                new(nameof(StatusAgendamento.Cancelado), "Cancelado"),
                new(nameof(StatusAgendamento.NaoCompareceu), "Não compareceu"),
            },
            ["statusVenda"] = new List<OpcaoDto>
            {
                new(nameof(StatusVenda.Aberta), "Aberta"),
                new(nameof(StatusVenda.AguardandoPagamento), "Aguardando pagamento"),
                new(nameof(StatusVenda.Paga), "Paga"),
                new(nameof(StatusVenda.Cancelada), "Cancelada"),
                new(nameof(StatusVenda.Estornada), "Estornada"),
            },
            ["cicloCobranca"] = new List<OpcaoDto>
            {
                new(nameof(CicloCobranca.Mensal), "Mensal"),
                new(nameof(CicloCobranca.Anual), "Anual"),
            },
            ["gatewayPagamento"] = new List<OpcaoDto>
            {
                new(nameof(GatewayPagamento.Paddle), "Paddle"),
                new(nameof(GatewayPagamento.GooglePlay), "Google Play"),
                new(nameof(GatewayPagamento.Manual), "Cobrança manual"),
            },
            ["diaDaSemana"] = new List<OpcaoDto>
            {
                new(nameof(DayOfWeek.Sunday), "Domingo"),
                new(nameof(DayOfWeek.Monday), "Segunda-feira"),
                new(nameof(DayOfWeek.Tuesday), "Terça-feira"),
                new(nameof(DayOfWeek.Wednesday), "Quarta-feira"),
                new(nameof(DayOfWeek.Thursday), "Quinta-feira"),
                new(nameof(DayOfWeek.Friday), "Sexta-feira"),
                new(nameof(DayOfWeek.Saturday), "Sábado"),
            },
        };
}

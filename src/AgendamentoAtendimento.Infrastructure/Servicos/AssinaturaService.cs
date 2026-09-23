using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Infrastructure.Servicos;

/// <summary>Motivo pelo qual a Api recusou uma operação de assinatura.</summary>
public enum MotivoRecusa
{
    Nenhum = 0,
    SemAssinatura,
    AssinaturaInativa,
    ProdutoNaoCoberto,
    SemAssentoLivre,
    AcimaDoLimiteDoPlano,
    RecursoForaDoPlano,
    /// <summary>Aumentar assentos é compra: passa pelo checkout, nunca por um PUT.</summary>
    AssentosExigemPagamento,
}

public sealed record ResultadoAssinatura(bool Ok, MotivoRecusa Motivo, string? Mensagem = null)
{
    public static readonly ResultadoAssinatura Sucesso = new(true, MotivoRecusa.Nenhum);
}

/// <summary>
/// Regras da assinatura compartilhada: entitlement por produto, assentos do time e preço.
/// Tudo é decidido aqui; o app só mostra o que este serviço devolve.
/// </summary>
public class AssinaturaService
{
    private readonly AppDbContext _db;
    private readonly CacheDeAssinatura? _cache;

    /// <summary>O retrato já lido nesta requisição (o serviço é por escopo).</summary>
    private (long TenantId, SituacaoDaAssinatura Situacao)? _daRequisicao;

    public AssinaturaService(AppDbContext db, CacheDeAssinatura? cache = null)
    {
        _db = db;
        _cache = cache;
    }

    /// <summary>
    /// A assinatura do tenant da requisição. `Assinatura` não é entidade de tenant (não
    /// passa pelo filtro global), então o filtro é daqui: sem ele, o `FirstOrDefault`
    /// devolvia a assinatura da primeira empresa do banco para todas as outras — e
    /// alterar assentos em uma mexia na de outra.
    /// </summary>
    public async Task<Assinatura?> ObterAtualAsync(CancellationToken ct = default)
    {
        if (_db.Contexto.TenantId is not { } tenantId)
        {
            return null;
        }

        return await _db.Assinaturas
            .Include(a => a.Plano)
            .Include(a => a.Produtos)
            .FirstOrDefaultAsync(a => a.TenantId == tenantId, ct);
    }

    /// <summary>
    /// O retrato leve da assinatura, para a checagem que roda em toda requisição. Sai do
    /// cache por tenant quando há um; senão, é uma consulta enxuta, sem plano nem rastreio.
    /// </summary>
    public async Task<SituacaoDaAssinatura> SituacaoAsync(CancellationToken ct = default)
    {
        if (_db.Contexto.TenantId is not { } tenantId)
        {
            return SituacaoDaAssinatura.Nenhuma;
        }

        if (_daRequisicao is { } lidaAgora && lidaAgora.TenantId == tenantId)
        {
            return lidaAgora.Situacao;
        }

        if (_cache is not null && _cache.TentarObter(tenantId, out var guardada))
        {
            _daRequisicao = (tenantId, guardada);
            return guardada;
        }

        var lida = await _db.Assinaturas.AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .Select(a => new
            {
                a.Status,
                a.FimPeriodoDeGraca,
                Produtos = a.Produtos.Select(p => new { p.ProdutoChave, p.Ativo }).ToList(),
            })
            .FirstOrDefaultAsync(ct);

        var situacao = lida is null
            ? SituacaoDaAssinatura.Nenhuma
            : new SituacaoDaAssinatura(
                true, lida.Status, lida.FimPeriodoDeGraca,
                lida.Produtos.Where(p => p.Ativo).Select(p => p.ProdutoChave).ToList(),
                // Sem produto cadastrado a assinatura vale para a suíte inteira — a mesma
                // regra de VerificarAcessoAsync.
                lida.Produtos.Count == 0);

        _cache?.Guardar(tenantId, situacao);
        _daRequisicao = (tenantId, situacao);
        return situacao;
    }

    /// <summary>Esquece o retrato em cache: a assinatura acabou de mudar.</summary>
    public void Invalidar()
    {
        _daRequisicao = null;
        if (_db.Contexto.TenantId is { } tenantId)
        {
            _cache?.Invalidar(tenantId);
        }
    }

    /// <summary>
    /// O produto que está chamando (cabeçalho `X-Produto`) está coberto e em dia?
    /// É o que transforma a assinatura em compartilhada de verdade.
    /// </summary>
    public async Task<ResultadoAssinatura> VerificarAcessoAsync(string produto, CancellationToken ct = default)
    {
        var assinatura = await ObterAtualAsync(ct);
        if (assinatura is null)
        {
            return new ResultadoAssinatura(false, MotivoRecusa.SemAssinatura,
                "Nenhuma assinatura encontrada para esta empresa.");
        }

        if (!assinatura.LiberaAcesso)
        {
            return new ResultadoAssinatura(false, MotivoRecusa.AssinaturaInativa,
                $"A assinatura está {assinatura.Status.ToString().ToLowerInvariant()}.");
        }

        var cobre = assinatura.Produtos.Count == 0 ||
                    assinatura.Produtos.Any(p => p.Ativo &&
                        string.Equals(p.ProdutoChave, produto, StringComparison.OrdinalIgnoreCase));

        return cobre
            ? ResultadoAssinatura.Sucesso
            : new ResultadoAssinatura(false, MotivoRecusa.ProdutoNaoCoberto,
                $"A assinatura atual não cobre o produto {produto}.");
    }

    /// <summary>Assentos ocupados: usuários ativos, incluindo convites pendentes.</summary>
    public async Task<int> AssentosEmUsoAsync(CancellationToken ct = default) =>
        await _db.Usuarios.CountAsync(u => u.Ativo && u.OcupaAssento, ct);

    /// <summary>
    /// Há assento livre para mais um usuário? É esta checagem que faz o convite responder
    /// 402 quando o time estoura o contratado.
    /// </summary>
    public async Task<ResultadoAssinatura> PodeAdicionarUsuarioAsync(CancellationToken ct = default)
    {
        var assinatura = await ObterAtualAsync(ct);
        if (assinatura is null)
        {
            return new ResultadoAssinatura(false, MotivoRecusa.SemAssinatura,
                "Nenhuma assinatura encontrada para esta empresa.");
        }

        var emUso = await AssentosEmUsoAsync(ct);
        if (emUso < assinatura.AssentosContratados)
        {
            return ResultadoAssinatura.Sucesso;
        }

        var preco = PrecificacaoAssinatura.PrecoAssentoAdicional(assinatura.Ciclo);
        var periodo = assinatura.Ciclo == CicloCobranca.Mensal ? "mês" : "ano";
        return new ResultadoAssinatura(false, MotivoRecusa.SemAssentoLivre,
            $"Todos os {assinatura.AssentosContratados} assentos estão ocupados. " +
            $"Cada usuário adicional custa US$ {preco:0.00} por {periodo}.");
    }

    /// <summary>
    /// Recursos liberados para o tenant. Sem assinatura, vale o menor plano ativo: é o que
    /// o app mostra enquanto a empresa ainda não contratou.
    /// </summary>
    public async Task<IReadOnlyList<string>> RecursosLiberadosAsync(CancellationToken ct = default)
    {
        var assinatura = await ObterAtualAsync(ct);
        var plano = assinatura?.Plano
            ?? await _db.Planos.AsNoTracking()
                .Where(p => p.Ativo).OrderBy(p => p.Ordem).FirstOrDefaultAsync(ct);

        return plano?.ChavesDeRecurso() ?? Array.Empty<string>();
    }

    /// <summary>
    /// O plano do tenant libera este recurso? Quando não libera, a mensagem já diz em qual
    /// plano ele entra — é o texto que o app exibe no cadeado.
    /// </summary>
    public async Task<ResultadoAssinatura> VerificarRecursoAsync(
        string chave, CancellationToken ct = default)
    {
        var recurso = CatalogoRecursos.Obter(chave);
        if (recurso is null)
        {
            // Recurso desconhecido não bloqueia: quem decide o catálogo é o servidor.
            return ResultadoAssinatura.Sucesso;
        }

        var liberados = await RecursosLiberadosAsync(ct);
        if (liberados.Any(c => string.Equals(c, recurso.Chave, StringComparison.OrdinalIgnoreCase)))
        {
            return ResultadoAssinatura.Sucesso;
        }

        var planos = await _db.Planos.AsNoTracking().Where(p => p.Ativo).OrderBy(p => p.Ordem)
            .ToListAsync(ct);
        var minimo = CatalogoRecursos.MenorPlanoQueLibera(recurso.Chave, planos);

        var onde = minimo is null
            ? "Fale com a gente para liberar este recurso."
            : $"Está disponível a partir do plano {minimo.Nome}.";

        return new ResultadoAssinatura(false, MotivoRecusa.RecursoForaDoPlano,
            $"{recurso.Nome} não faz parte do plano atual. {onde}");
    }

    /// <summary>Cotação oficial. O app exibe exatamente estes números.</summary>
    public async Task<DetalhePreco?> CotarAsync(
        long planoId, CicloCobranca ciclo, int assentos, CancellationToken ct = default)
    {
        var plano = await _db.Planos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planoId, ct);
        return plano is null ? null : PrecificacaoAssinatura.Calcular(plano, ciclo, assentos);
    }

    /// <summary>
    /// Altera a quantidade de assentos pelo app. Só serve para REDUZIR: aumentar é compra,
    /// e compra só entra pelos caminhos de pagamento (webhook do Paddle e confirmação do
    /// Google Play, em <see cref="AplicarCompraConfirmadaAsync"/>). Antes, qualquer um com
    /// `assinatura.alterar` subia os assentos até o teto do plano sem pagar nada.
    ///
    /// A redução nunca fica abaixo do que o time já ocupa nem dos assentos que o plano já
    /// inclui: pedir menos que isso reduz até esse piso — o mesmo "ajusta para o mínimo"
    /// que a rota sempre fez com os assentos em uso.
    /// </summary>
    public async Task<(ResultadoAssinatura Resultado, Assinatura? Assinatura)> AlterarAssentosAsync(
        int assentos, CancellationToken ct = default)
    {
        var assinatura = await ObterAtualAsync(ct);
        if (assinatura?.Plano is null)
        {
            return (new ResultadoAssinatura(false, MotivoRecusa.SemAssinatura,
                "Nenhuma assinatura encontrada para esta empresa."), null);
        }

        var atuais = assinatura.AssentosContratados;
        if (assentos > atuais)
        {
            var preco = PrecificacaoAssinatura.PrecoAssentoAdicional(assinatura.Ciclo);
            var periodo = assinatura.Ciclo == CicloCobranca.Mensal ? "mês" : "ano";
            return (new ResultadoAssinatura(false, MotivoRecusa.AssentosExigemPagamento,
                $"Para passar de {atuais} para {assentos} assentos é preciso comprar os adicionais " +
                $"(US$ {preco:0.00} por usuário por {periodo}). Faça a compra pelo checkout da assinatura."),
                null);
        }

        var emUso = await AssentosEmUsoAsync(ct);
        var piso = Math.Max(emUso, assinatura.Plano.UsuariosIncluidos);

        // Nunca sobe por aqui: se o piso passa do contratado (dado antigo), fica como está.
        var novos = Math.Min(atuais, Math.Max(assentos, piso));
        if (novos != atuais)
        {
            assinatura.AssentosContratados = novos;
            await _db.SaveChangesAsync(ct);
            Invalidar();
        }

        return (ResultadoAssinatura.Sucesso, assinatura);
    }

    /// <summary>
    /// Aplica uma compra que o gateway confirmou: plano, ciclo e assentos. É o ÚNICO
    /// caminho que aumenta assentos. `assentos` é o total contratado (inclusos + adicionais);
    /// nulo mantém o que está. Recusa o que o plano não comporta, sem gravar nada.
    /// </summary>
    public async Task<(ResultadoAssinatura Resultado, Assinatura? Assinatura)> AplicarCompraConfirmadaAsync(
        GatewayPagamento gateway, long? planoId, CicloCobranca? ciclo, int? assentos,
        CancellationToken ct = default)
    {
        var assinatura = await ObterAtualAsync(ct);
        if (assinatura is null)
        {
            return (new ResultadoAssinatura(false, MotivoRecusa.SemAssinatura,
                "Nenhuma assinatura encontrada para esta empresa."), null);
        }

        var plano = assinatura.Plano;
        if (planoId is { } id && id != assinatura.PlanoId)
        {
            plano = await _db.Planos.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plano is null)
            {
                return (new ResultadoAssinatura(false, MotivoRecusa.SemAssinatura,
                    "Plano da compra não encontrado."), null);
            }
        }

        if (assentos is { } total)
        {
            // Nunca menos que o que o plano inclui: a compra é de adicionais.
            total = Math.Max(total, plano?.UsuariosIncluidos ?? 1);
            if (plano is not null && !plano.Suporta(total))
            {
                return (new ResultadoAssinatura(false, MotivoRecusa.AcimaDoLimiteDoPlano,
                    $"O plano {plano.Nome} aceita no máximo {plano.LimiteUsuarios} usuários."), null);
            }

            assinatura.AssentosContratados = total;
        }

        if (plano is not null)
        {
            assinatura.PlanoId = plano.Id;
            assinatura.Plano = plano;
        }

        if (ciclo is { } c)
        {
            assinatura.Ciclo = c;
        }

        assinatura.Gateway = gateway;
        assinatura.Status = StatusAssinatura.Ativa;
        assinatura.FimPeriodoDeGraca = null;
        assinatura.UltimoPagamentoEm = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        Invalidar();
        return (ResultadoAssinatura.Sucesso, assinatura);
    }

    /// <summary>
    /// Registra o evento do gateway de forma idempotente. Devolve false quando o evento já
    /// tinha sido recebido — webhook reentregue não cobra nem libera duas vezes.
    /// </summary>
    public async Task<bool> RegistrarEventoAsync(
        GatewayPagamento gateway, string eventoExternoId, string tipo, string payload,
        long? tenantId = null, CancellationToken ct = default)
    {
        var jaExiste = await _db.EventosGateway
            .AnyAsync(e => e.Gateway == gateway && e.EventoExternoId == eventoExternoId, ct);
        if (jaExiste)
        {
            return false;
        }

        _db.EventosGateway.Add(new EventoGateway
        {
            Gateway = gateway,
            EventoExternoId = eventoExternoId,
            Tipo = tipo,
            Payload = payload,
            TenantId = tenantId,
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

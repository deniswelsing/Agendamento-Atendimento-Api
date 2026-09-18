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

    public AssinaturaService(AppDbContext db) => _db = db;

    public async Task<Assinatura?> ObterAtualAsync(CancellationToken ct = default) =>
        await _db.Assinaturas
            .Include(a => a.Plano)
            .Include(a => a.Produtos)
            .FirstOrDefaultAsync(ct);

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
    /// Altera a quantidade de assentos. Nunca deixa cair abaixo do que o time já usa, e
    /// nunca passa do limite do plano.
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

        var emUso = await AssentosEmUsoAsync(ct);
        var novos = Math.Max(assentos, emUso);

        if (!assinatura.Plano.Suporta(novos))
        {
            return (new ResultadoAssinatura(false, MotivoRecusa.AcimaDoLimiteDoPlano,
                $"O plano {assinatura.Plano.Nome} aceita no máximo {assinatura.Plano.LimiteUsuarios} usuários."), null);
        }

        assinatura.AssentosContratados = novos;
        await _db.SaveChangesAsync(ct);
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

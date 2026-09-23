using System.Collections.Concurrent;
using AgendamentoAtendimento.Domain.Assinaturas;

namespace AgendamentoAtendimento.Infrastructure.Servicos;

/// <summary>
/// O retrato da assinatura que a checagem por requisição precisa: status, fim da graça e
/// produtos cobertos. Guarda o fim da graça, e não um "libera" já decidido, para que a
/// graça que acaba no meio da validade do cache pare de liberar na hora certa.
/// </summary>
public sealed record SituacaoDaAssinatura(
    bool Existe,
    StatusAssinatura Status,
    DateTimeOffset? FimPeriodoDeGraca,
    IReadOnlyList<string> ProdutosAtivos,
    bool CobreTodosOsProdutos)
{
    public static readonly SituacaoDaAssinatura Nenhuma =
        new(false, StatusAssinatura.Pendente, null, Array.Empty<string>(), true);

    public bool LiberaAcesso(DateTimeOffset agora) =>
        Assinatura.Libera(Status, FimPeriodoDeGraca, agora);

    public bool Cobre(string produto) =>
        CobreTodosOsProdutos ||
        ProdutosAtivos.Any(p => string.Equals(p, produto, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Cache curto, por tenant, da <see cref="SituacaoDaAssinatura"/>. A checagem roda em
/// toda requisição autenticada; sem cache seria uma consulta a mais em cada uma.
///
/// Quem muda a assinatura (assentos, compra confirmada, webhook) chama
/// <see cref="Invalidar"/>. A validade curta cobre o que muda por fora deste processo —
/// outra instância da Api, ou alguém mexendo direto no banco.
/// </summary>
public sealed class CacheDeAssinatura
{
    public static readonly TimeSpan Validade = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<long, (SituacaoDaAssinatura Situacao, DateTimeOffset Expira)> _porTenant = new();
    private readonly TimeProvider _relogio;

    public CacheDeAssinatura(TimeProvider? relogio = null) => _relogio = relogio ?? TimeProvider.System;

    public bool TentarObter(long tenantId, out SituacaoDaAssinatura situacao)
    {
        if (_porTenant.TryGetValue(tenantId, out var guardada) && guardada.Expira > _relogio.GetUtcNow())
        {
            situacao = guardada.Situacao;
            return true;
        }

        situacao = SituacaoDaAssinatura.Nenhuma;
        return false;
    }

    public void Guardar(long tenantId, SituacaoDaAssinatura situacao) =>
        _porTenant[tenantId] = (situacao, _relogio.GetUtcNow().Add(Validade));

    public void Invalidar(long tenantId) => _porTenant.TryRemove(tenantId, out _);
}

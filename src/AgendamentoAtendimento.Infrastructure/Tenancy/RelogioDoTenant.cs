using AgendamentoAtendimento.Infrastructure.Persistencia;

namespace AgendamentoAtendimento.Infrastructure.Tenancy;

/// <summary>
/// O relógio da empresa: que dia é hoje e que horas são LÁ, no fuso de
/// <see cref="Domain.MultiTenancy.Tenant.FusoHorario"/>.
///
/// Todo DateTimeOffset que a Api grava e devolve é um instante de verdade, em UTC. Já o
/// horário de funcionamento, a jornada, "hoje" e "o dia 28" são hora de parede da
/// empresa. É aqui — e só aqui — que um vira o outro: "abre às 08:00" em São Paulo é
/// 11:00Z, e tratar 08:00 como 08:00Z fazia a tela, que converte para o fuso do
/// aparelho, oferecer 05:00.
///
/// É por requisição (e por escopo de job): lê o fuso do tenant do contexto na primeira
/// pergunta e guarda por tenant, então um job que troca de tenant no meio do escopo
/// ganha o fuso de cada um sem reler o do anterior.
/// </summary>
public sealed class RelogioDoTenant
{
    /// <summary>O fuso de quem não disse o seu — o mesmo padrão de <c>Tenant</c>.</summary>
    public const string FusoPadrao = "America/Sao_Paulo";

    private readonly AppDbContext? _db;
    private readonly IContextoAtual? _contexto;
    private readonly TimeProvider _tempo;
    private readonly TimeZoneInfo? _fixo;
    private readonly Dictionary<long, TimeZoneInfo> _porTenant = new();

    public RelogioDoTenant(AppDbContext db, IContextoAtual contexto, TimeProvider tempo)
    {
        _db = db;
        _contexto = contexto;
        _tempo = tempo;
    }

    private RelogioDoTenant(TimeZoneInfo fuso, TimeProvider tempo)
    {
        _fixo = fuso;
        _tempo = tempo;
    }

    /// <summary>
    /// Um relógio preso a um fuso, sem banco. É o dos testes — e o de quem já sabe o
    /// fuso e não quer depender do contexto.
    /// </summary>
    public static RelogioDoTenant Fixo(string fuso, TimeProvider? tempo = null) =>
        new(ResolverFuso(fuso), tempo ?? TimeProvider.System);

    /// <summary>O fuso do tenant atual. Sem tenant no contexto, o padrão.</summary>
    public TimeZoneInfo Fuso => _fixo ?? FusoDoTenantAtual();

    /// <summary>O instante de agora. Instante é instante: em UTC, igual para todo mundo.</summary>
    public DateTimeOffset Agora() => _tempo.GetUtcNow();

    /// <summary>A data de hoje NA EMPRESA. Às 23:30 de São Paulo ainda é hoje, não amanhã.</summary>
    public DateOnly Hoje() => DataLocal(Agora());

    /// <summary>O instante visto no relógio da empresa, com o deslocamento de lá.</summary>
    public DateTimeOffset NoFuso(DateTimeOffset instante) =>
        TimeZoneInfo.ConvertTime(instante, Fuso);

    /// <summary>Em que dia da empresa este instante cai.</summary>
    public DateOnly DataLocal(DateTimeOffset instante) =>
        DateOnly.FromDateTime(NoFuso(instante).DateTime);

    /// <summary>Que horas o relógio da empresa marca neste instante.</summary>
    public TimeOnly HoraLocal(DateTimeOffset instante) =>
        TimeOnly.FromDateTime(NoFuso(instante).DateTime);

    /// <summary>
    /// A hora de parede da empresa como instante, em UTC — o Npgsql só grava
    /// timestamptz com deslocamento zero.
    ///
    /// Horário de verão tem duas pontas:
    /// - a hora que não existe (o relógio pula de 02:00 para 03:00) anda para frente,
    ///   como o próprio relógio faz: 02:30 vira 03:30;
    /// - a hora que acontece duas vezes (o relógio volta de 02:00 para 01:00) fica com a
    ///   primeira vez. Quem marcou "01:30" chega na primeira, não uma hora depois.
    /// </summary>
    public DateTimeOffset Instante(DateOnly data, TimeOnly hora)
    {
        var fuso = Fuso;
        var parede = data.ToDateTime(hora, DateTimeKind.Unspecified);

        TimeSpan deslocamento;
        if (fuso.IsAmbiguousTime(parede))
        {
            // O maior deslocamento é o instante mais cedo: a primeira vez.
            deslocamento = fuso.GetAmbiguousTimeOffsets(parede).Max();
        }
        else if (fuso.IsInvalidTime(parede))
        {
            // O deslocamento de antes do pulo leva a hora para depois dele.
            var antes = parede;
            do
            {
                antes = antes.AddMinutes(-30);
            } while (fuso.IsInvalidTime(antes));

            deslocamento = fuso.GetUtcOffset(antes);
        }
        else
        {
            deslocamento = fuso.GetUtcOffset(parede);
        }

        return new DateTimeOffset(parede.Add(-deslocamento), TimeSpan.Zero);
    }

    /// <summary>A meia-noite da empresa neste dia, como instante.</summary>
    public DateTimeOffset InicioDoDia(DateOnly data) => Instante(data, TimeOnly.MinValue);

    /// <summary>
    /// Onde o dia acaba: a meia-noite seguinte, exclusiva. Não é "início + 24h": no dia
    /// em que o horário de verão começa ou acaba, o dia tem 23 ou 25 horas.
    /// </summary>
    public DateTimeOffset FimDoDia(DateOnly data) => InicioDoDia(data.AddDays(1));

    /// <summary>
    /// O fuso pelo nome guardado no tenant. Nome vazio ou desconhecido cai no padrão, e
    /// uma máquina sem a base de fusos cai em UTC: errar por algumas horas é melhor do que
    /// derrubar a agenda inteira por um cadastro torto.
    /// </summary>
    public static TimeZoneInfo ResolverFuso(string? nome)
    {
        foreach (var candidato in new[] { nome, FusoPadrao })
        {
            if (string.IsNullOrWhiteSpace(candidato))
            {
                continue;
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidato.Trim());
            }
            catch (Exception erro) when (erro is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Tenta o próximo.
            }
        }

        return TimeZoneInfo.Utc;
    }

    private TimeZoneInfo FusoDoTenantAtual()
    {
        if (_contexto?.TenantId is not { } tenantId || _db is null)
        {
            return ResolverFuso(null);
        }

        if (_porTenant.TryGetValue(tenantId, out var lido))
        {
            return lido;
        }

        // Síncrono de propósito: é uma linha, uma vez por tenant no escopo, e deixa
        // quem pergunta "que dia é hoje?" sem precisar de await. Tenant não é entidade
        // de tenant, então o filtro global não se aplica.
        var nome = _db.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.FusoHorario)
            .FirstOrDefault();

        return _porTenant[tenantId] = ResolverFuso(nome);
    }
}

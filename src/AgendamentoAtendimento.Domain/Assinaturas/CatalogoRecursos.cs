namespace AgendamentoAtendimento.Domain.Assinaturas;

/// <summary>
/// Um recurso da plataforma e o degrau de plano a partir do qual ele é liberado.
/// <paramref name="NivelMinimo"/> casa com <see cref="Plano.Ordem"/>: 1 Basic, 2 Platinum,
/// 3 Ultimate, 4 Custom.
/// </summary>
public sealed record RecursoPlano(
    string Chave,
    string Nome,
    string Grupo,
    int NivelMinimo,
    string? Descricao = null);

/// <summary>
/// Catálogo de recursos por plano. É a fonte única do que cada degrau libera: a Api
/// responde com ele, o app só desenha o que vem daqui — nada de lista de recursos
/// escrita no cliente.
/// </summary>
public static class CatalogoRecursos
{
    /// <summary>Valor histórico de <see cref="Plano.Recursos"/> que libera tudo.</summary>
    public const string Tudo = "tudo";


    // -------------------------------------------------------------- chaves conhecidas
    public const string Agendamentos = "agendamentos";
    public const string Clientes = "clientes";
    public const string Catalogo = "catalogo";
    public const string Vendas = "vendas";
    public const string PaginaOnline = "pagina-online";
    public const string LembreteEmail = "lembrete-email";

    public const string MultiUsuario = "multi-usuario";
    public const string JornadaPorPessoa = "jornada-por-pessoa";
    public const string PoliticaCancelamento = "politica-cancelamento";
    public const string CartaoEmArquivo = "cartao-em-arquivo";
    public const string LembreteSmsWhats = "lembrete-sms-whatsapp";
    public const string GoogleCalendar = "google-calendar";
    public const string AgendamentoRecorrente = "agendamento-recorrente";
    public const string SemMarca = "sem-marca";

    public const string RelatoriosAvancados = "relatorios-avancados";
    public const string Comissoes = "comissoes";
    public const string MultiUnidade = "multiunidade";
    public const string PermissoesAvancadas = "permissoes-avancadas";
    public const string SalasEEquipamentos = "recursos-reservaveis";

    public const string Onboarding = "onboarding-dedicado";
    public const string ApiPublica = "api-publica";
    public const string GerenteDeConta = "gerente-de-conta";

    private const string GrupoAgenda = "Agenda e atendimento";
    private const string GrupoTime = "Time e operação";
    private const string GrupoGestao = "Gestão e finanças";
    private const string GrupoParceria = "Parceria";

    public static readonly IReadOnlyList<RecursoPlano> Todos = new List<RecursoPlano>
    {
        new(Agendamentos, "Agendamentos ilimitados", GrupoAgenda, 1),
        new(Clientes, "Cadastro de clientes (pessoa e empresa)", GrupoAgenda, 1),
        new(Catalogo, "Catálogo de produtos e serviços", GrupoAgenda, 1),
        new(Vendas, "Vendas e recebimentos", GrupoGestao, 1),
        new(PaginaOnline, "Página de agendamento online", GrupoAgenda, 1),
        new(LembreteEmail, "Lembretes automáticos por e-mail", GrupoAgenda, 1),

        new(MultiUsuario, "Vários usuários no time", GrupoTime, 2,
            "Cada usuário além dos inclusos entra na assinatura."),
        new(JornadaPorPessoa, "Jornada e folga por pessoa do time", GrupoTime, 2),
        new(PoliticaCancelamento, "Política de cancelamento e no-show", GrupoAgenda, 2),
        new(CartaoEmArquivo, "Cartão em arquivo", GrupoGestao, 2),
        new(LembreteSmsWhats, "Lembretes por SMS e WhatsApp", GrupoAgenda, 2),
        new(GoogleCalendar, "Sincronização com Google Calendar", GrupoAgenda, 2),
        new(AgendamentoRecorrente, "Agendamento recorrente", GrupoAgenda, 2),
        new(SemMarca, "Sem a marca da plataforma", GrupoAgenda, 2),

        new(RelatoriosAvancados, "Relatórios avançados e lucratividade", GrupoGestao, 3),
        new(Comissoes, "Comissões e folha do time", GrupoTime, 3),
        new(MultiUnidade, "Várias unidades", GrupoGestao, 3),
        new(PermissoesAvancadas, "Permissões avançadas por perfil", GrupoTime, 3),
        new(SalasEEquipamentos, "Salas e equipamentos com reserva", GrupoTime, 3),

        new(Onboarding, "Onboarding dedicado", GrupoParceria, 4),
        new(ApiPublica, "API pública", GrupoParceria, 4),
        new(GerenteDeConta, "Gerente de conta dedicado", GrupoParceria, 4),
    };

    public static readonly IReadOnlySet<string> Chaves =
        Todos.Select(r => r.Chave).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static RecursoPlano? Obter(string? chave) =>
        string.IsNullOrWhiteSpace(chave)
            ? null
            : Todos.FirstOrDefault(r => string.Equals(r.Chave, chave, StringComparison.OrdinalIgnoreCase));

    /// <summary>Chaves liberadas até um degrau, na ordem do catálogo.</summary>
    public static IReadOnlyList<string> AteNivel(int nivel) =>
        Todos.Where(r => r.NivelMinimo <= nivel).Select(r => r.Chave).ToList();

    /// <summary>Valor pronto para <see cref="Plano.Recursos"/>.</summary>
    public static string ListaAteNivel(int nivel) => string.Join(',', AteNivel(nivel));

    /// <summary>Descarta chaves desconhecidas e normaliza.</summary>
    public static IReadOnlyList<string> Sanitizar(IEnumerable<string>? chaves) =>
        (chaves ?? Enumerable.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToLowerInvariant())
            .Where(Chaves.Contains)
            .Distinct()
            .ToList();

    /// <summary>
    /// O menor plano que libera um recurso. É o que o app mostra no cadeado —
    /// "disponível no Platinum" — em vez de só bloquear.
    /// </summary>
    public static Plano? MenorPlanoQueLibera(string chave, IEnumerable<Plano> planos)
    {
        var recurso = Obter(chave);
        if (recurso is null)
        {
            return null;
        }

        return planos
            .Where(p => p.Ativo && p.Libera(chave))
            .OrderBy(p => p.Ordem)
            .FirstOrDefault();
    }
}

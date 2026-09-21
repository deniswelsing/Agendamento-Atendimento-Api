namespace AgendamentoAtendimento.Domain.Usuarios;

/// <summary>Uma ação que um perfil pode receber dentro de um módulo.</summary>
public sealed record AcaoPermissao(string Chave, string Nome, bool Destrutiva = false)
{
    /// <summary>Chave completa gravada em <see cref="PerfilPermissao.Permissao"/>.</summary>
    public string ChaveCompleta(string modulo) => $"{modulo}.{Chave}";
}

/// <summary>
/// Um módulo é uma tela do aplicativo. O admin liga ou desliga a tela inteira pela ação
/// `ver` e escolhe, dentro dela, o que cada perfil pode fazer.
/// </summary>
public sealed record ModuloPermissao(
    string Chave,
    string Nome,
    string Rota,
    IReadOnlyList<AcaoPermissao> Acoes)
{
    public IEnumerable<string> ChavesCompletas() => Acoes.Select(a => a.ChaveCompleta(Chave));

    public string ChaveVer => $"{Chave}.{Permissoes.AcaoVer}";
}

/// <summary>
/// Catálogo de permissões da plataforma. É a fonte que a tela de perfis do app renderiza:
/// o app não conhece nenhuma permissão por conta própria, ele desenha o que vem daqui.
/// </summary>
public static class Permissoes
{
    /// <summary>Permissão coringa do perfil administrador: vê tudo e faz tudo.</summary>
    public const string Coringa = "*";

    public const string AcaoVer = "ver";
    public const string AcaoCriar = "criar";
    public const string AcaoEditar = "editar";
    public const string AcaoExcluir = "excluir";

    private static readonly AcaoPermissao Ver = new(AcaoVer, "Ver a tela");
    private static readonly AcaoPermissao Criar = new(AcaoCriar, "Criar");
    private static readonly AcaoPermissao Editar = new(AcaoEditar, "Editar");
    private static readonly AcaoPermissao Excluir = new(AcaoExcluir, "Excluir", Destrutiva: true);

    public static readonly IReadOnlyList<ModuloPermissao> Modulos = new List<ModuloPermissao>
    {
        new("dashboard", "Painel", "dashboard", new[] { Ver }),
        new("agenda", "Agenda", "agenda", new[]
        {
            Ver, Criar, Editar,
            new AcaoPermissao("concluir", "Iniciar e concluir atendimento"),
            new AcaoPermissao("cancelar", "Cancelar agendamento", Destrutiva: true),
        }),
        new("clientes", "Clientes", "clientes", new[] { Ver, Criar, Editar, Excluir }),
        new("catalogo", "Catálogo", "catalogo", new[] { Ver, Criar, Editar, Excluir }),
        new("vendas", "Vendas", "vendas", new[]
        {
            Ver, Criar, Editar,
            new AcaoPermissao("finalizar", "Finalizar venda"),
            new AcaoPermissao("cancelar", "Cancelar venda", Destrutiva: true),
        }),
        new("financeiro", "Financeiro", "financeiro", new[]
        {
            Ver,
            new AcaoPermissao("receber", "Registrar recebimento"),
            new AcaoPermissao("estornar", "Estornar recebimento", Destrutiva: true),
            new AcaoPermissao("formas", "Configurar formas de pagamento"),
        }),
        new("horarios", "Horários de funcionamento", "horarios", new[] { Ver, Editar }),
        new("pagina-online", "Página de agendamento online", "paginaOnline", new[]
        {
            Ver, Editar,
            new AcaoPermissao("aprovar", "Aprovar pedidos feitos pelo cliente"),
        }),
        new("lembretes", "Lembretes e confirmação", "lembretes", new[]
        {
            Ver, Editar,
            new AcaoPermissao("enviar", "Disparar a fila de avisos"),
        }),
        new("time", "Time", "time", new[]
        {
            Ver,
            new AcaoPermissao("convidar", "Convidar usuário"),
            Editar,
            new AcaoPermissao("remover", "Remover do time", Destrutiva: true),
        }),
        new("perfis", "Perfis e permissões", "perfis", new[] { Ver, Criar, Editar, Excluir }),
        new("assinatura", "Assinatura", "assinatura", new[]
        {
            Ver,
            new AcaoPermissao("alterar", "Contratar, trocar plano e assentos"),
        }),
        new("configuracoes", "Configurações", "configuracoes", new[] { Ver, Editar }),
    };

    /// <summary>Todas as chaves válidas, usadas para validar o que o admin envia.</summary>
    public static readonly IReadOnlySet<string> Todas =
        Modulos.SelectMany(m => m.ChavesCompletas()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool Existe(string? permissao) =>
        !string.IsNullOrWhiteSpace(permissao) &&
        (permissao == Coringa || Todas.Contains(permissao));

    /// <summary>Descarta chaves desconhecidas e normaliza.</summary>
    public static IReadOnlyList<string> Sanitizar(IEnumerable<string>? permissoes) =>
        (permissoes ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().ToLowerInvariant())
            .Where(p => Todas.Contains(p))
            .Distinct()
            .ToList();

    /// <summary>
    /// Uma ação só vale se a tela dela também estiver liberada: sem `agenda.ver` não
    /// adianta ter `agenda.criar`. Fecha o buraco de um perfil "cego que edita".
    /// </summary>
    public static IReadOnlyList<string> ComTelasImplicadas(IEnumerable<string> permissoes)
    {
        var conjunto = new HashSet<string>(permissoes, StringComparer.OrdinalIgnoreCase);
        foreach (var modulo in Modulos)
        {
            if (conjunto.Any(p => p.StartsWith(modulo.Chave + ".", StringComparison.OrdinalIgnoreCase)))
            {
                conjunto.Add(modulo.ChaveVer);
            }
        }
        return conjunto.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>Telas visíveis para um conjunto de permissões.</summary>
    public static IReadOnlyList<string> TelasVisiveis(IEnumerable<string> permissoes)
    {
        var conjunto = new HashSet<string>(permissoes, StringComparer.OrdinalIgnoreCase);
        if (conjunto.Contains(Coringa))
        {
            return Modulos.Select(m => m.Chave).ToList();
        }
        return Modulos.Where(m => conjunto.Contains(m.ChaveVer)).Select(m => m.Chave).ToList();
    }

    public static bool Permite(IEnumerable<string> permissoes, string exigida)
    {
        var conjunto = permissoes as IReadOnlySet<string>
            ?? new HashSet<string>(permissoes, StringComparer.OrdinalIgnoreCase);
        return conjunto.Contains(Coringa) || conjunto.Contains(exigida);
    }
}

using System.Security.Cryptography;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Infrastructure.Servicos;

/// <summary>Por que um pedido público foi recusado. O controller traduz em status HTTP.</summary>
public enum RecusaPublica
{
    Nenhuma = 0,
    PaginaNaoEncontrada,
    ForaDoPlano,
    AntecedenciaInsuficiente,
    ForaDaJanela,
    ServicoIndisponivel,
    HorarioIndisponivel,
    LimiteDiario,
    DadosIncompletos,
}

public sealed record ResultadoPublico(bool Ok, RecusaPublica Motivo = RecusaPublica.Nenhuma,
    string? Mensagem = null)
{
    public static readonly ResultadoPublico Sucesso = new(true);
}

/// <summary>
/// A página pública de agendamento. Tudo aqui roda **sem token**, então o serviço assume
/// o tenant pelo slug e nunca pelo que o chamador diz ser.
///
/// As regras de recusa moram aqui, e não no controller, porque são as mesmas que o app
/// precisa explicar ao dono da empresa quando ele configura a página.
/// </summary>
public class PaginaPublicaService
{
    private readonly AppDbContext _db;
    private readonly ContextoAtual _contexto;
    private readonly DisponibilidadeService _disponibilidade;
    private readonly AssinaturaService _assinaturas;
    private readonly RelogioDoTenant _relogio;

    public PaginaPublicaService(
        AppDbContext db,
        ContextoAtual contexto,
        DisponibilidadeService disponibilidade,
        AssinaturaService assinaturas,
        RelogioDoTenant relogio)
    {
        _db = db;
        _contexto = contexto;
        _disponibilidade = disponibilidade;
        _assinaturas = assinaturas;
        _relogio = relogio;
    }

    /// <summary>
    /// Acha a página pelo slug e **assume o tenant dela**. É a única entrada: enquanto
    /// isso não roda, nenhuma consulta pública enxerga dado de ninguém.
    ///
    /// Página desligada responde como página inexistente, de propósito: quem desliga não
    /// quer que o endereço antigo continue confirmando que a empresa existe.
    /// </summary>
    public async Task<ConfiguracaoPaginaPublica?> AssumirPorSlugAsync(
        string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var normalizado = NormalizarSlug(slug);

        _contexto.IgnorarFiltroDeTenant = true;
        ConfiguracaoPaginaPublica? pagina;
        try
        {
            pagina = await _db.PaginasPublicas.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Slug == normalizado && p.Ativa, ct);
        }
        finally
        {
            _contexto.IgnorarFiltroDeTenant = false;
        }

        if (pagina is null)
        {
            return null;
        }

        _contexto.AssumirTenant(pagina.TenantId, normalizado);

        // O plano do tenant é que libera a página — não o de quem está chamando, que não
        // tem plano nenhum. Sem o recurso, o endereço simplesmente não existe.
        var recurso = await _assinaturas.VerificarRecursoAsync(CatalogoRecursos.PaginaOnline, ct);
        if (!recurso.Ok)
        {
            _contexto.TenantId = null;
            _contexto.TenantSlug = null;
            return null;
        }

        return pagina;
    }

    /// <summary>Serviços que a página oferece. Só serviço ativo e marcado como visível.</summary>
    public Task<List<ItemCatalogo>> ServicosPublicosAsync(CancellationToken ct = default) =>
        _db.ItensCatalogo.AsNoTracking()
            .Where(i => i.Tipo == TipoItem.Servico && i.Ativo && i.VisivelOnline
                        && i.DuracaoMinutos != null && i.DuracaoMinutos > 0)
            .OrderBy(i => i.Nome)
            .ToListAsync(ct);

    /// <summary>
    /// A faixa de datas que a página aceita. Antes do começo é antecedência de menos;
    /// depois do fim é janela de mais.
    ///
    /// As datas são as do calendário da empresa, que é o que o cliente vê na página:
    /// às 22h de São Paulo "hoje" ainda é hoje, mesmo já sendo amanhã em UTC.
    /// </summary>
    public (DateOnly Primeira, DateOnly Ultima) JanelaPublica(
        ConfiguracaoPaginaPublica pagina, DateTimeOffset agora)
    {
        var comeca = agora.AddHours(pagina.AntecedenciaMinimaHoras);
        var primeira = _relogio.DataLocal(comeca);
        var ultima = _relogio.DataLocal(agora).AddDays(pagina.JanelaMaximaDias);
        return (primeira, ultima > primeira ? ultima : primeira);
    }

    /// <summary>
    /// Encaixes que a página pode oferecer num dia. É a mesma disponibilidade do app —
    /// inclusive quem presta o serviço — menos o que a antecedência mínima já comeu.
    /// </summary>
    public async Task<(ResultadoPublico Resultado, DiaDaAgenda? Dia)> DisponibilidadeAsync(
        ConfiguracaoPaginaPublica pagina,
        DateOnly data,
        IReadOnlyCollection<long> itensIds,
        long? responsavelId,
        DateTimeOffset agora,
        CancellationToken ct = default)
    {
        var servicos = await ValidarServicosAsync(itensIds, ct);
        if (servicos is null)
        {
            return (new ResultadoPublico(false, RecusaPublica.ServicoIndisponivel,
                "Algum serviço não está disponível para agendamento online."), null);
        }

        var (primeira, ultima) = JanelaPublica(pagina, agora);
        if (data > ultima)
        {
            return (new ResultadoPublico(false, RecusaPublica.ForaDaJanela,
                $"A agenda online vai até {ultima:dd/MM/yyyy}."), null);
        }

        var duracao = servicos.Sum(s => s.DuracaoMinutos ?? 0);
        var escolher = pagina.PermiteEscolherProfissional ? responsavelId : null;
        var dia = await _disponibilidade.ObterDiaAsync(data, duracao, escolher, ct, itensIds);

        // Corta o que já não cabe na antecedência mínima. O dia inteiro pode sumir, e
        // está certo: o cliente vê "sem horário hoje" em vez de marcar para daqui a pouco.
        var limite = agora.AddHours(pagina.AntecedenciaMinimaHoras);
        var livres = dia.Livres.Where(s => s.Inicio >= limite).ToList();

        // Dia sem horário não pode ser o fim da conversa com quem está marcando sozinho:
        // ou ele acha os próximos dias aqui, ou fecha a página. As mesmas regras valem —
        // a janela da agenda online e a antecedência mínima.
        var sugestoes = new List<SugestaoDeDia>();
        if (livres.Count == 0)
        {
            for (var i = 1; i <= 30 && sugestoes.Count < 3; i++)
            {
                var outro = data.AddDays(i);
                if (outro > ultima)
                {
                    break;
                }

                var diaSeguinte = await _disponibilidade.ObterDiaAsync(
                    outro, duracao, escolher, ct, itensIds);
                var horarios = diaSeguinte.Livres.Where(s => s.Inicio >= limite).Take(3).ToList();

                if (horarios.Count > 0)
                {
                    sugestoes.Add(new SugestaoDeDia(outro, horarios));
                }
            }
        }

        return (ResultadoPublico.Sucesso, dia with { Livres = livres, Sugestoes = sugestoes });
    }

    /// <summary>
    /// Grava o pedido do cliente. Devolve o agendamento já com o código de consulta.
    ///
    /// Reaproveita o cliente pelo e-mail quando ele já existe: quem marca pela segunda vez
    /// não vira um cadastro novo, e o histórico continua sendo dele.
    /// </summary>
    public async Task<(ResultadoPublico Resultado, Agendamento? Agendamento)> AgendarAsync(
        ConfiguracaoPaginaPublica pagina,
        string nome,
        string? email,
        string? telefone,
        IReadOnlyCollection<long> itensIds,
        DateTimeOffset inicioPedido,
        long? responsavelId,
        string? observacoes,
        DateTimeOffset agora,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nome))
        {
            return (new ResultadoPublico(false, RecusaPublica.DadosIncompletos,
                "Informe seu nome."), null);
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return (new ResultadoPublico(false, RecusaPublica.DadosIncompletos,
                "Informe seu e-mail: é por ele que a confirmação chega."), null);
        }

        if (pagina.ExigeTelefone && string.IsNullOrWhiteSpace(telefone))
        {
            return (new ResultadoPublico(false, RecusaPublica.DadosIncompletos,
                "Informe seu telefone."), null);
        }

        // O mesmo serviço duas vezes contaria a duração dobrada na grade e simples no
        // agendamento gravado: a conta tem de ser uma só.
        itensIds = itensIds.Distinct().ToList();

        var servicos = await ValidarServicosAsync(itensIds, ct);
        if (servicos is null)
        {
            return (new ResultadoPublico(false, RecusaPublica.ServicoIndisponivel,
                "Algum serviço não está disponível para agendamento online."), null);
        }

        var inicio = inicioPedido.ToUniversalTime();
        var limite = agora.AddHours(pagina.AntecedenciaMinimaHoras);
        if (inicio < limite)
        {
            return (new ResultadoPublico(false, RecusaPublica.AntecedenciaInsuficiente,
                $"Agendamentos online precisam de {pagina.AntecedenciaMinimaHoras}h de "
                + "antecedência. Escolha um horário mais adiante ou fale com a gente."), null);
        }

        var (_, ultima) = JanelaPublica(pagina, agora);
        if (_relogio.DataLocal(inicio) > ultima)
        {
            return (new ResultadoPublico(false, RecusaPublica.ForaDaJanela,
                $"A agenda online vai até {ultima:dd/MM/yyyy}."), null);
        }

        var emailNormalizado = email.Trim().ToLowerInvariant();
        if (await EstourouLimiteAsync(pagina, emailNormalizado, agora, ct))
        {
            return (new ResultadoPublico(false, RecusaPublica.LimiteDiario,
                "Você já tem pedidos demais para hoje. Fale com a gente para marcar outro."),
                null);
        }

        var duracao = servicos.Sum(s => s.DuracaoMinutos ?? 0);
        var fim = inicio.AddMinutes(duracao);
        var escolhido = pagina.PermiteEscolherProfissional ? responsavelId : null;

        var responsavel = escolhido
            ?? await EscolherResponsavelAsync(inicio, duracao, itensIds, ct);

        if (responsavel is null ||
            !await _disponibilidade.EstaLivreAsync(inicio, fim, responsavel.Value, null, ct, itensIds))
        {
            return (new ResultadoPublico(false, RecusaPublica.HorarioIndisponivel,
                "Esse horário acabou de ser ocupado. Escolha outro."), null);
        }

        var cliente = await GarantirClienteAsync(nome, emailNormalizado, telefone, ct);

        var agendamento = new Agendamento
        {
            ClienteId = cliente.Id,
            Inicio = inicio,
            Fim = fim,
            // Com aprovação ligada o pedido já segura o horário, mas ainda não é compromisso.
            Status = pagina.ExigeAprovacao
                ? StatusAgendamento.PendenteAprovacao
                : StatusAgendamento.Agendado,
            ResponsavelId = responsavel,
            Observacoes = observacoes,
            Origem = OrigemAgendamento.Online,
            CodigoPublico = CodigoDeAcesso.Gerar(),
        };

        // Na ordem pedida e cada um na sua etapa: é a sequência que a grade validou. Sem
        // `Ordem`, todos caíam na etapa 0 — "ao mesmo tempo" —, e a pessoa ficava presa só
        // pelo serviço mais longo, com o resto do atendimento livre para outro cliente.
        var ordem = 0;
        foreach (var servico in itensIds.Select(id => servicos.First(s => s.Id == id)))
        {
            agendamento.Itens.Add(new AgendamentoItem
            {
                ItemCatalogoId = servico.Id,
                Nome = servico.Nome,
                DuracaoMinutos = servico.DuracaoMinutos ?? 0,
                PrecoUnitario = servico.Preco,
                Ordem = ordem++,
                ResponsavelId = responsavel,
            });
        }

        _db.Agendamentos.Add(agendamento);
        await _db.SaveChangesAsync(ct);

        return (ResultadoPublico.Sucesso, agendamento);
    }

    /// <summary>Consulta pelo código entregue ao cliente. Sem o código, nada é devolvido.</summary>
    public Task<Agendamento?> PorCodigoAsync(string codigo, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(codigo)
            ? Task.FromResult<Agendamento?>(null)
            : _db.Agendamentos
                .Include(a => a.Itens)
                .Include(a => a.Cliente)
                .Include(a => a.Responsavel)
                .FirstOrDefaultAsync(a => a.CodigoPublico == codigo.Trim().ToUpperInvariant(), ct);

    /// <summary>
    /// Slug livre? O próprio tenant não conta contra si mesmo: renomear para o que já é
    /// seu não pode dar conflito.
    /// </summary>
    public async Task<bool> SlugLivreAsync(
        string slug, long? ignorarTenantId = null, CancellationToken ct = default)
    {
        var normalizado = NormalizarSlug(slug);
        if (normalizado.Length < 3)
        {
            return false;
        }

        _contexto.IgnorarFiltroDeTenant = true;
        try
        {
            return !await _db.PaginasPublicas.AsNoTracking().AnyAsync(
                p => p.Slug == normalizado && p.TenantId != ignorarTenantId, ct);
        }
        finally
        {
            _contexto.IgnorarFiltroDeTenant = false;
        }
    }

    /// <summary>
    /// Deixa o texto no formato do endereço: minúsculas, sem acento e com hífen no lugar
    /// de qualquer outra coisa.
    /// </summary>
    public static string NormalizarSlug(string bruto)
    {
        if (string.IsNullOrWhiteSpace(bruto))
        {
            return string.Empty;
        }

        var semAcento = bruto.Trim().ToLowerInvariant()
            .Normalize(System.Text.NormalizationForm.FormD);

        var limpo = new System.Text.StringBuilder(semAcento.Length);
        foreach (var c in semAcento)
        {
            var categoria = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (categoria == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                limpo.Append(c);
            }
            else if (limpo.Length > 0 && limpo[^1] != '-')
            {
                limpo.Append('-');
            }
        }

        return limpo.ToString().Trim('-');
    }

    /// <summary>Null quando algum id pedido não é um serviço público válido.</summary>
    private async Task<List<ItemCatalogo>?> ValidarServicosAsync(
        IReadOnlyCollection<long> itensIds, CancellationToken ct)
    {
        if (itensIds.Count == 0)
        {
            return null;
        }

        var distintos = itensIds.Distinct().ToList();
        var servicos = await _db.ItensCatalogo.AsNoTracking()
            .Where(i => distintos.Contains(i.Id) && i.Tipo == TipoItem.Servico
                        && i.Ativo && i.VisivelOnline)
            .ToListAsync(ct);

        return servicos.Count == distintos.Count ? servicos : null;
    }

    private async Task<long?> EscolherResponsavelAsync(
        DateTimeOffset inicio, int duracao, IReadOnlyCollection<long> itensIds, CancellationToken ct)
    {
        var data = _relogio.DataLocal(inicio);
        var dia = await _disponibilidade.ObterDiaAsync(data, duracao, null, ct, itensIds);
        return dia.Livres.FirstOrDefault(s => s.Inicio == inicio)?.ResponsavelId;
    }

    private async Task<bool> EstourouLimiteAsync(
        ConfiguracaoPaginaPublica pagina, string email, DateTimeOffset agora, CancellationToken ct)
    {
        if (pagina.LimiteDiarioPorCliente <= 0)
        {
            return false;
        }

        var desde = agora.AddDays(-1);
        var pedidos = await _db.Agendamentos.AsNoTracking()
            .CountAsync(a => a.Origem == OrigemAgendamento.Online
                             && a.CriadoEm >= desde
                             && a.Cliente!.Email == email
                             && a.Status != StatusAgendamento.Cancelado, ct);

        return pedidos >= pagina.LimiteDiarioPorCliente;
    }

    private async Task<Cliente> GarantirClienteAsync(
        string nome, string email, string? telefone, CancellationToken ct)
    {
        var existente = await _db.Clientes.FirstOrDefaultAsync(c => c.Email == email, ct);
        if (existente is not null)
        {
            // Telefone novo vale: quem marcou agora deu o número mais recente.
            if (!string.IsNullOrWhiteSpace(telefone))
            {
                existente.Celular = telefone.Trim();
            }

            return existente;
        }

        var partes = nome.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cliente = new Cliente
        {
            Tipo = TipoCliente.Pessoa,
            Nome = partes[0],
            Sobrenome = partes.Length > 1 ? partes[1] : null,
            Email = email,
            Celular = string.IsNullOrWhiteSpace(telefone) ? null : telefone.Trim(),
        };

        _db.Clientes.Add(cliente);
        await _db.SaveChangesAsync(ct);
        return cliente;
    }
}

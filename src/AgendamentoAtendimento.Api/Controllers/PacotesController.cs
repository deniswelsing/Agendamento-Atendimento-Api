using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Pacotes;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>
/// Pacotes pré-pagos: o cliente paga um punhado de atendimentos adiantado e vai usando.
///
/// Duas formas de vender: um modelo de prateleira ("Mensal 4 sessões") ou um pacote
/// montado para aquele cliente. São o mesmo objeto — o modelo só preenche os campos.
/// </summary>
[Route("api/pacotes")]
public class PacotesController : ControllerBaseApi
{
    private readonly AppDbContext _db;
    private readonly PacoteAgendaService _agenda;
    private readonly RecorrenciaDePacotesService _recorrencia;
    private readonly DisponibilidadeService _disponibilidade;
    private readonly RelogioDoTenant _relogio;

    public PacotesController(
        AppDbContext db,
        PacoteAgendaService agenda,
        RecorrenciaDePacotesService recorrencia,
        DisponibilidadeService disponibilidade,
        RelogioDoTenant relogio)
    {
        _db = db;
        _agenda = agenda;
        _recorrencia = recorrencia;
        _disponibilidade = disponibilidade;
        _relogio = relogio;
    }

    /// <summary>Hoje no calendário da empresa — é nele que os ciclos começam e vencem.</summary>
    private DateOnly Hoje => _relogio.Hoje();

    // ------------------------------------------------------------ modelos
    [HttpGet("modelos")]
    [RequerPermissao("pacotes.ver")]
    public async Task<ActionResult<IReadOnlyList<PacoteModeloDto>>> Modelos(
        [FromQuery] bool incluirInativos = false, CancellationToken ct = default)
    {
        var modelos = await _db.PacoteModelos.AsNoTracking()
            .Where(m => incluirInativos || m.Ativo)
            .OrderBy(m => m.Nome)
            .ToListAsync(ct);

        var itens = await ItensPorModeloAsync(modelos.Select(m => m.Id).ToList(), ct);

        return Ok(modelos.Select(m => Montar(m, itens.GetValueOrDefault(m.Id, new()))).ToList());
    }

    [HttpPost("modelos")]
    [RequerPermissao("pacotes.criar")]
    public async Task<ActionResult<PacoteModeloDto>> CriarModelo(
        PacoteModeloRequest req, CancellationToken ct)
    {
        ValidarModelo(req);
        await GarantirServicosAsync(req.ItensIds, ct);

        var modelo = new PacoteModelo
        {
            Nome = req.Nome.Trim(), Descricao = req.Descricao?.Trim(),
            Quantidade = req.Quantidade, Preco = req.Preco,
            Recorrencia = req.Recorrencia, Ativo = req.Ativo,
        };
        _db.PacoteModelos.Add(modelo);
        await _db.SaveChangesAsync(ct);

        await TrocarItensDoModeloAsync(modelo.Id, req.ItensIds, ct);

        var itens = await ItensDoModeloAsync(modelo.Id, ct);
        return Ok(Montar(modelo, itens));
    }

    [HttpPut("modelos/{id:long}")]
    [RequerPermissao("pacotes.editar")]
    public async Task<ActionResult<PacoteModeloDto>> SalvarModelo(
        long id, PacoteModeloRequest req, CancellationToken ct)
    {
        ValidarModelo(req);

        var modelo = NaoNulo(
            await _db.PacoteModelos.FirstOrDefaultAsync(m => m.Id == id, ct),
            "Modelo de pacote não encontrado.");
        await GarantirServicosAsync(req.ItensIds, ct);

        modelo.Nome = req.Nome.Trim();
        modelo.Descricao = req.Descricao?.Trim();
        modelo.Quantidade = req.Quantidade;
        modelo.Preco = req.Preco;
        modelo.Recorrencia = req.Recorrencia;
        modelo.Ativo = req.Ativo;
        await _db.SaveChangesAsync(ct);

        await TrocarItensDoModeloAsync(modelo.Id, req.ItensIds, ct);

        return Ok(Montar(modelo, await ItensDoModeloAsync(modelo.Id, ct)));
    }

    /// <summary>
    /// Desativa o modelo. Não apaga: os pacotes já vendidos apontam para ele, e sumir com
    /// o nome deixaria o histórico dizendo "pacote de coisa nenhuma".
    /// </summary>
    [HttpDelete("modelos/{id:long}")]
    [RequerPermissao("pacotes.editar")]
    public async Task<ActionResult<PacoteModeloDto>> DesativarModelo(long id, CancellationToken ct)
    {
        var modelo = NaoNulo(
            await _db.PacoteModelos.FirstOrDefaultAsync(m => m.Id == id, ct),
            "Modelo de pacote não encontrado.");

        modelo.Ativo = false;
        await _db.SaveChangesAsync(ct);

        return Ok(Montar(modelo, await ItensDoModeloAsync(modelo.Id, ct)));
    }

    // ------------------------------------------------------------- pacotes
    [HttpGet]
    [RequerPermissao("pacotes.ver")]
    public async Task<ActionResult<IReadOnlyList<PacoteDto>>> Listar(
        [FromQuery] StatusDePacote? status, CancellationToken ct = default)
    {
        var pacotes = await _db.Pacotes.AsNoTracking()
            .Where(p => status == null || p.Status == status)
            .OrderBy(p => p.Status).ThenBy(p => p.FimDoCicloAtual)
            .ToListAsync(ct);

        return Ok(await MontarVariosAsync(pacotes, ct));
    }

    [HttpGet("{id:long}")]
    [RequerPermissao("pacotes.ver")]
    public async Task<ActionResult<PacoteDto>> Obter(long id, CancellationToken ct)
    {
        var pacote = NaoNulo(
            await _db.Pacotes.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct),
            "Pacote não encontrado.");

        return Ok(await MontarAsync(pacote, ct));
    }

    [HttpPost]
    [RequerPermissao("pacotes.criar")]
    public async Task<ActionResult<PacoteDto>> Criar(PacoteRequest req, CancellationToken ct)
    {
        var (nome, quantidade, preco, recorrencia, itensIds) = await ResolverAsync(req, ct);

        if (quantidade <= 0)
        {
            throw new RegraDeNegocioException(
                "O pacote precisa de ao menos um atendimento.", "PACOTE_SEM_QUANTIDADE");
        }

        if (itensIds.Count == 0)
        {
            throw new RegraDeNegocioException(
                "O pacote precisa de ao menos um serviço.", "PACOTE_SEM_SERVICO");
        }

        await GarantirServicosAsync(itensIds, ct);

        var inicio = req.InicioDoCicloAtual ?? Hoje;
        var pacote = new Pacote
        {
            PacoteModeloId = req.PacoteModeloId,
            Nome = nome, QuantidadePorCliente = quantidade, PrecoPorCliente = preco,
            Recorrencia = recorrencia, InicioDoCicloAtual = inicio,
        };
        pacote.FimDoCicloAtual = pacote.FimCalculado(inicio);

        _db.Pacotes.Add(pacote);
        await _db.SaveChangesAsync(ct);

        foreach (var itemId in itensIds.Distinct())
        {
            _db.PacoteItens.Add(new PacoteItem { PacoteId = pacote.Id, ItemCatalogoId = itemId });
        }
        await _db.SaveChangesAsync(ct);

        return Ok(await MontarAsync(pacote, ct));
    }

    // ------------------------------------------------- clientes no pacote
    /// <summary>
    /// Põe um cliente no pacote e abre o saldo do ciclo. A preferência de horário entra
    /// aqui porque é dela que sai a agenda inteira dele, na chamada seguinte.
    /// </summary>
    [HttpPost("{id:long}/clientes")]
    [RequerPermissao("pacotes.editar")]
    public async Task<ActionResult<PacoteClienteDto>> Entrar(
        long id, EntrarNoPacoteRequest req, CancellationToken ct)
    {
        var pacote = NaoNulo(
            await _db.Pacotes.FirstOrDefaultAsync(p => p.Id == id, ct),
            "Pacote não encontrado.");

        if (pacote.Status != StatusDePacote.Ativo)
        {
            throw new RegraDeNegocioException(
                "Este pacote não está ativo.", "PACOTE_INATIVO");
        }

        var cliente = NaoNulo(
            await _db.Clientes.FirstOrDefaultAsync(c => c.Id == req.ClienteId, ct),
            "Cliente não encontrado.");

        // Um cliente num pacote só: duas bolsas de sessões para a mesma pessoa não teriam
        // como decidir de qual sai o atendimento de hoje.
        // Vínculo que sobrou "ativo" num pacote que já acabou (gravado antes de a
        // varredura passar a encerrá-lo) não prende ninguém: é fechado aqui.
        var vinculosAntigos = await _db.PacoteClientes
            .Where(c => c.ClienteId == cliente.Id && c.Ativo
                        && c.Pacote!.Status != StatusDePacote.Ativo)
            .ToListAsync(ct);
        foreach (var antigo in vinculosAntigos)
        {
            antigo.Ativo = false;
        }

        if (vinculosAntigos.Count > 0)
        {
            // Antes do novo vínculo: o índice único de vínculo ativo não pode ver os dois.
            await _db.SaveChangesAsync(ct);
        }

        var jaEstaEm = await _db.PacoteClientes.AsNoTracking()
            .Where(c => c.ClienteId == cliente.Id && c.Ativo
                        && c.Pacote!.Status == StatusDePacote.Ativo)
            .Select(c => c.PacoteId)
            .FirstOrDefaultAsync(ct);
        if (jaEstaEm != 0)
        {
            var nomeDoOutro = await _db.Pacotes.AsNoTracking()
                .Where(p => p.Id == jaEstaEm).Select(p => p.Nome).FirstOrDefaultAsync(ct);
            throw new RegraDeNegocioException(
                $"{cliente.NomeExibicao} já está no pacote \"{nomeDoOutro}\". "
                + "Tire-o de lá antes de pôr em outro.",
                "CLIENTE_JA_TEM_PACOTE");
        }

        await GarantirProfissionalAsync(req.ResponsavelPreferidoId, ct);

        var vinculo = new PacoteCliente
        {
            PacoteId = pacote.Id, ClienteId = cliente.Id,
            DiaDaSemana = req.DiaDaSemana, Hora = req.Hora,
            ResponsavelPreferidoId = req.ResponsavelPreferidoId,
        };
        _db.PacoteClientes.Add(vinculo);
        await _db.SaveChangesAsync(ct);

        _db.CiclosDePacote.Add(new CicloDoCliente
        {
            PacoteClienteId = vinculo.Id, Ciclo = pacote.CicloAtual,
            Inicio = pacote.InicioDoCicloAtual, Fim = pacote.FimDoCicloAtual,
            QuantidadeContratada = pacote.QuantidadePorCliente,
        });
        await _db.SaveChangesAsync(ct);

        return Ok(await MontarClienteAsync(vinculo, ct));
    }

    /// <summary>
    /// A agenda que a preferência do cliente gera: uma proposta por sessão que falta,
    /// cada uma no encaixe mais perto da hora combinada, com quem presta e está livre.
    /// </summary>
    [HttpGet("clientes/{pacoteClienteId:long}/propostas")]
    [RequerPermissao("pacotes.ver")]
    public async Task<ActionResult<IReadOnlyList<PropostaDePacoteDto>>> Propostas(
        long pacoteClienteId, [FromQuery] DateOnly? apartirDe, CancellationToken ct = default)
    {
        await GarantirVinculoAsync(pacoteClienteId, ct);
        var propostas = await _agenda.ProporAsync(pacoteClienteId, apartirDe ?? Hoje, ct);

        return Ok(propostas.Select(p => new PropostaDePacoteDto(
            p.Data, p.Inicio, p.Fim, p.ResponsavelId, p.ResponsavelNome,
            p.Candidatos.Select(c => new PessoaResumoDto(c.UsuarioId, c.Nome)).ToList(),
            p.TemEncaixe, p.Observacao)).ToList());
    }

    /// <summary>
    /// Marca uma das propostas. Uma por chamada, de propósito: o time confere data a
    /// data, e marcar tudo de uma vez esconderia o dia em que ninguém podia.
    /// </summary>
    [HttpPost("clientes/{pacoteClienteId:long}/agendamentos")]
    [RequerPermissao("pacotes.agendar")]
    public async Task<ActionResult<AgendamentoDto>> Marcar(
        long pacoteClienteId, MarcarDoPacoteRequest req, CancellationToken ct)
    {
        var vinculo = NaoNulo(
            await _db.PacoteClientes.FirstOrDefaultAsync(c => c.Id == pacoteClienteId, ct),
            "Cliente do pacote não encontrado.");

        var pacote = NaoNulo(
            await _db.Pacotes.FirstOrDefaultAsync(p => p.Id == vinculo.PacoteId, ct),
            "Pacote não encontrado.");

        var ciclo = NaoNulo(
            await _db.CiclosDePacote
                .Where(c => c.PacoteClienteId == vinculo.Id && !c.Encerrado)
                .OrderByDescending(c => c.Ciclo).FirstOrDefaultAsync(ct),
            "Este cliente não tem ciclo aberto no pacote.");

        // A sessão pertence ao ciclo que a paga, então ela acontece dentro dele. Marcar
        // fora deixaria o saldo deste ciclo pagar um atendimento do seguinte — e num
        // pacote sem recorrência, o estorno do que sobrou sairia com o atendimento ainda
        // marcado lá na frente.
        // Em UTC, como o resto da agenda: o Npgsql recusa gravar DateTimeOffset com outro
        // deslocamento em timestamptz, e o 500 vinha só na hora de salvar.
        var inicio = req.Inicio.ToUniversalTime();
        // O ciclo é de datas da empresa, então o dia da sessão também.
        var dia = _relogio.DataLocal(inicio);
        if (dia < ciclo.Inicio || dia > ciclo.Fim)
        {
            throw new RegraDeNegocioException(
                $"Este ciclo vai de {ciclo.Inicio:dd/MM/yyyy} a {ciclo.Fim:dd/MM/yyyy}. "
                + "O que não couber nele vira crédito no próximo — ou estorno, se não houver.",
                "FORA_DO_CICLO");
        }

        // Saldo e agenda conferidos sob a trava: dois toques em "marcar" ao mesmo tempo
        // gastavam a mesma sessão duas vezes, ou punham a pessoa em dois lugares.
        await using var trava = await _db.TravarAgendaAsync(ct);

        var marcados = await _db.Agendamentos.CountAsync(
            a => a.PacoteClienteId == vinculo.Id && a.PacoteCiclo == ciclo.Ciclo
                 && a.Status != StatusAgendamento.Cancelado, ct);
        if (marcados >= ciclo.Total)
        {
            throw new RegraDeNegocioException(
                $"O saldo do ciclo acabou: {marcados} de {ciclo.Total} já marcados.",
                "PACOTE_SEM_SALDO");
        }

        var itens = await _db.PacoteItens.AsNoTracking()
            .Where(i => i.PacoteId == pacote.Id).Select(i => i.ItemCatalogoId).ToListAsync(ct);

        var servicos = await _db.ItensCatalogo.AsNoTracking()
            .Where(i => itens.Contains(i.Id)).ToListAsync(ct);

        // Serviço sem duração no catálogo não bloqueia o pacote: meia hora é o padrão
        // da grade, e é melhor marcar com ela do que não marcar.
        var duracao = servicos.Sum(s => s.DuracaoMinutos ?? 0);
        if (duracao <= 0)
        {
            duracao = 30;
        }
        var fim = inicio.AddMinutes(duracao);

        var responsavel = req.ResponsavelId ?? vinculo.ResponsavelPreferidoId;
        if (responsavel is null)
        {
            var livres = await _disponibilidade.QuemPodePrestarAsync(
                itens.FirstOrDefault(), inicio, fim, null, ct, inicio, fim);
            responsavel = livres.FirstOrDefault()?.UsuarioId;
        }

        if (responsavel is null)
        {
            throw new RegraDeNegocioException(
                "Ninguém que preste estes serviços está livre neste horário.",
                "SEM_RESPONSAVEL_LIVRE");
        }

        // Quem vai atender tem de prestar cada serviço e estar livre na janela dele — a
        // mesma regra do agendamento avulso. Sem isto, a pessoa escolhida (ou a preferida
        // do cliente) era gravada por cima de outro compromisso, fora da jornada ou num
        // dia em que a empresa está fechada.
        var cursor = inicio;
        foreach (var servico in servicos.Cast<ItemCatalogo?>().DefaultIfEmpty())
        {
            var fimDoServico = servico is null
                ? fim
                : cursor.AddMinutes(servico.DuracaoMinutos ?? 30);
            var pode = await _disponibilidade.PodePrestarAsync(
                responsavel.Value, servico?.Id, cursor, fimDoServico, null, ct, inicio, fim);
            if (!pode)
            {
                throw new RegraDeNegocioException(
                    servico is null
                        ? "A pessoa escolhida não está livre nesse horário."
                        : $"A pessoa escolhida não pode atender \"{servico.Nome}\" nesse horário.",
                    "RESPONSAVEL_INDISPONIVEL");
            }

            cursor = fimDoServico;
        }

        var agendamento = new Agendamento
        {
            ClienteId = vinculo.ClienteId, Inicio = inicio, Fim = fim,
            Status = StatusAgendamento.Agendado, ResponsavelId = responsavel,
            PacoteClienteId = vinculo.Id, PacoteCiclo = ciclo.Ciclo,
            Observacoes = $"Pacote: {pacote.Nome}",
        };

        var ordem = 0;
        foreach (var servico in servicos)
        {
            agendamento.Itens.Add(new AgendamentoItem
            {
                ItemCatalogoId = servico.Id, Nome = servico.Nome,
                DuracaoMinutos = servico.DuracaoMinutos ?? 30, Quantidade = 1,
                // Pré-pago: o preço já foi cobrado no pacote. Repetir aqui somaria a
                // mesma sessão duas vezes no faturamento.
                PrecoUnitario = 0m,
                Ordem = ordem++, ResponsavelId = responsavel,
            });
        }

        _db.Agendamentos.Add(agendamento);
        await _db.SaveChangesAsync(ct);
        await trava.ConfirmarAsync(ct);

        var completo = await _db.Agendamentos.AsNoTracking()
            .Include(a => a.Cliente).Include(a => a.Responsavel)
            .Include(a => a.Itens).ThenInclude(i => i.Responsavel)
            .FirstAsync(a => a.Id == agendamento.Id, ct);

        return Ok(completo.ParaDto());
    }

    /// <summary>
    /// Tira o cliente do pacote e acerta o que sobrou: crédito quando há recorrência à
    /// frente, estorno quando não há. Os horários marcados voltam para a grade.
    /// </summary>
    [HttpDelete("clientes/{pacoteClienteId:long}")]
    [RequerPermissao("pacotes.encerrar")]
    public async Task<ActionResult<CicloDoClienteDto>> Sair(
        long pacoteClienteId, CancellationToken ct)
    {
        // Sem isto, o id de outra empresa respondia 204 — nada mudava, mas a rota dizia
        // que tinha dado certo.
        await GarantirVinculoAsync(pacoteClienteId, ct);
        var ciclo = await _recorrencia.EncerrarVinculoAsync(pacoteClienteId, Hoje, ct);
        if (ciclo is null)
        {
            return NoContent();
        }

        return Ok(Montar(ciclo));
    }

    // ---------------------------------------------------------- varredura
    /// <summary>
    /// A varredura diária, sob demanda. O job a chama sozinho todo dia; esta rota existe
    /// para a tela mostrar o que vem por aí sem esperar a meia-noite.
    /// </summary>
    [HttpPost("varredura")]
    [RequerPermissao("pacotes.editar")]
    public async Task<ActionResult<VarreduraDePacotesDto>> Varrer(
        [FromQuery] DateOnly? data, CancellationToken ct = default)
    {
        var hoje = data ?? Hoje;
        var r = await _recorrencia.VarrerAsync(hoje, ct);

        var resumo = r.Avisos.Count == 0 && r.CiclosEncerrados == 0
            ? "Nenhum pacote vence nos próximos 7 dias."
            : $"{r.Avisos.Count} pacote(s) a vencer, {r.CiclosEncerrados} ciclo(s) "
              + $"encerrado(s), {r.EstornosGerados} estorno(s).";

        return Ok(new VarreduraDePacotesDto(
            hoje,
            r.Avisos.Select(a => new AvisoDeRenovacaoDto(
                a.PacoteId, a.Nome, a.Ciclo, a.Vence, a.DiasAteVencer, a.Clientes,
                a.Recorrencia, a.Texto)).ToList(),
            r.CiclosEncerrados, r.CiclosAbertos, r.PacotesEncerrados,
            r.EstornosGerados, r.ValorEstornado, resumo));
    }

    // ------------------------------------------------------------ apoio
    private static void ValidarModelo(PacoteModeloRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nome))
        {
            throw new RegraDeNegocioException("O modelo precisa de um nome.", "NOME_OBRIGATORIO");
        }

        if (req.Quantidade <= 0)
        {
            throw new RegraDeNegocioException(
                "O modelo precisa de ao menos um atendimento.", "PACOTE_SEM_QUANTIDADE");
        }

        if (req.Preco < 0)
        {
            throw new RegraDeNegocioException("O preço não pode ser negativo.", "PRECO_INVALIDO");
        }
    }

    /// <summary>
    /// O que o pacote vai ser: do modelo quando vem de um, do pedido quando é montado
    /// para o cliente. O pedido vence o modelo — é o que permite vender o "Mensal 4"
    /// com um preço combinado sem criar outro modelo.
    /// </summary>
    private async Task<(string Nome, int Quantidade, decimal Preco,
        RecorrenciaDePacote Recorrencia, IReadOnlyList<long> Itens)> ResolverAsync(
        PacoteRequest req, CancellationToken ct)
    {
        if (req.PacoteModeloId is not { } modeloId)
        {
            return (req.Nome.Trim(), req.QuantidadePorCliente, req.PrecoPorCliente,
                req.Recorrencia, req.ItensIds);
        }

        var modelo = NaoNulo(
            await _db.PacoteModelos.AsNoTracking().FirstOrDefaultAsync(m => m.Id == modeloId, ct),
            "Modelo de pacote não encontrado.");

        var itens = req.ItensIds.Count > 0
            ? req.ItensIds
            : await _db.PacoteModeloItens.AsNoTracking()
                .Where(i => i.PacoteModeloId == modeloId)
                .Select(i => i.ItemCatalogoId).ToListAsync(ct);

        return (
            string.IsNullOrWhiteSpace(req.Nome) ? modelo.Nome : req.Nome.Trim(),
            req.QuantidadePorCliente > 0 ? req.QuantidadePorCliente : modelo.Quantidade,
            req.PrecoPorCliente > 0 ? req.PrecoPorCliente : modelo.Preco,
            req.Recorrencia != RecorrenciaDePacote.Nenhuma ? req.Recorrencia : modelo.Recorrencia,
            itens);
    }

    /// <summary>
    /// Todo id de serviço que vai ser gravado existe nesta empresa. O filtro de tenant
    /// esconde na leitura, mas não impede gravar: sem esta conferência o pacote de uma
    /// empresa guardava o serviço de outra e o mostrava vazio.
    /// </summary>
    private async Task GarantirServicosAsync(IReadOnlyList<long> itensIds, CancellationToken ct)
    {
        var distintos = itensIds.Distinct().ToList();
        var existem = await _db.ItensCatalogo.AsNoTracking()
            .CountAsync(i => distintos.Contains(i.Id) && i.Tipo == TipoItem.Servico, ct);
        if (existem != distintos.Count)
        {
            throw new RegraDeNegocioException(
                "Algum serviço não existe ou não é um serviço.", "SERVICO_INVALIDO");
        }
    }

    /// <summary>O profissional preferido é alguém do time desta empresa.</summary>
    private async Task GarantirProfissionalAsync(long? usuarioId, CancellationToken ct)
    {
        if (usuarioId is { } id && !await _db.Usuarios.AsNoTracking().AnyAsync(u => u.Id == id, ct))
        {
            throw new NaoEncontradoException("Profissional não encontrado.");
        }
    }

    private async Task GarantirVinculoAsync(long pacoteClienteId, CancellationToken ct)
    {
        if (!await _db.PacoteClientes.AsNoTracking().AnyAsync(c => c.Id == pacoteClienteId, ct))
        {
            throw new NaoEncontradoException("Cliente do pacote não encontrado.");
        }
    }

    private async Task TrocarItensDoModeloAsync(
        long modeloId, IReadOnlyList<long> itensIds, CancellationToken ct)
    {
        var atuais = await _db.PacoteModeloItens
            .Where(i => i.PacoteModeloId == modeloId).ToListAsync(ct);
        _db.PacoteModeloItens.RemoveRange(atuais);

        foreach (var itemId in itensIds.Distinct())
        {
            _db.PacoteModeloItens.Add(new PacoteModeloItem
            {
                PacoteModeloId = modeloId, ItemCatalogoId = itemId,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<Dictionary<long, List<ItemDoPacoteDto>>> ItensPorModeloAsync(
        IReadOnlyCollection<long> modelos, CancellationToken ct)
    {
        if (modelos.Count == 0)
        {
            return new Dictionary<long, List<ItemDoPacoteDto>>();
        }

        var linhas = await _db.PacoteModeloItens.AsNoTracking()
            .Where(i => modelos.Contains(i.PacoteModeloId))
            .Join(_db.ItensCatalogo.AsNoTracking(), i => i.ItemCatalogoId, c => c.Id,
                (i, c) => new { i.PacoteModeloId, Dto = new ItemDoPacoteDto(
                    c.Id, c.Nome, c.DuracaoMinutos ?? 0, c.Preco) })
            .ToListAsync(ct);

        return linhas.GroupBy(x => x.PacoteModeloId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Dto).ToList());
    }

    private async Task<List<ItemDoPacoteDto>> ItensDoModeloAsync(long modeloId, CancellationToken ct) =>
        (await ItensPorModeloAsync(new[] { modeloId }, ct)).GetValueOrDefault(modeloId, new());

    private static PacoteModeloDto Montar(PacoteModelo m, List<ItemDoPacoteDto> itens)
    {
        var valor = m.Quantidade <= 0
            ? 0m
            : decimal.Round(m.Preco / m.Quantidade, 2, MidpointRounding.AwayFromZero);

        // Sem o preço no texto: escrito aqui ele sairia na cultura do servidor — que é a
        // invariante, e imprimia "¤1.200,00" —, enquanto o app já sabe a moeda do tenant e
        // recebe `Preco` como número. O texto é do servidor, a moeda é de quem lê.
        return new PacoteModeloDto(
            m.Id, m.Nome, m.Descricao, m.Quantidade, m.Preco, m.Recorrencia, m.Ativo,
            itens, valor,
            $"{m.Quantidade} atendimento(s) · {Periodo(m.Recorrencia)}");
    }

    private static string Periodo(RecorrenciaDePacote r) => r switch
    {
        RecorrenciaDePacote.Nenhuma => "sem recorrência",
        RecorrenciaDePacote.Semanal => "semanal",
        RecorrenciaDePacote.Quinzenal => "quinzenal",
        RecorrenciaDePacote.Mensal => "mensal",
        RecorrenciaDePacote.Trimestral => "trimestral",
        RecorrenciaDePacote.Semestral => "semestral",
        _ => "anual",
    };

    private async Task<PacoteDto> MontarAsync(Pacote p, CancellationToken ct) =>
        (await MontarVariosAsync(new[] { p }, ct))[0];

    /// <summary>
    /// Os pacotes com itens e clientes, em poucas consultas para a lista inteira. Montar
    /// um a um fazia duas consultas por pacote e mais quatro por cliente de cada um — a
    /// tela de pacotes crescia em idas ao banco junto com a carteira.
    /// </summary>
    private async Task<List<PacoteDto>> MontarVariosAsync(
        IReadOnlyList<Pacote> pacotes, CancellationToken ct)
    {
        var ids = pacotes.Select(p => p.Id).ToList();

        var itens = (await _db.PacoteItens.AsNoTracking()
                .Where(i => ids.Contains(i.PacoteId))
                .Join(_db.ItensCatalogo.AsNoTracking(), i => i.ItemCatalogoId, c => c.Id,
                    (i, c) => new
                    {
                        i.PacoteId,
                        Dto = new ItemDoPacoteDto(c.Id, c.Nome, c.DuracaoMinutos ?? 0, c.Preco),
                    })
                .ToListAsync(ct))
            .ToLookup(x => x.PacoteId, x => x.Dto);

        var vinculos = await _db.PacoteClientes.AsNoTracking()
            .Where(c => ids.Contains(c.PacoteId))
            .OrderByDescending(c => c.Ativo)
            .ToListAsync(ct);

        var clientes = (await MontarClientesAsync(vinculos, ct)).ToLookup(c => c.PacoteId);

        return pacotes.Select(p =>
        {
            var doPacote = clientes[p.Id].ToList();
            var dias = p.DiasAteVencer(Hoje);
            var resumo = p.EhRecorrente
                ? $"{p.QuantidadePorCliente} por ciclo · {Periodo(p.Recorrencia)} · "
                  + $"{doPacote.Count(c => c.Ativo)} cliente(s)"
                : $"{p.QuantidadePorCliente} atendimento(s) · sem recorrência · "
                  + $"{doPacote.Count(c => c.Ativo)} cliente(s)";

            return new PacoteDto(
                p.Id, p.PacoteModeloId, p.Nome, p.QuantidadePorCliente, p.PrecoPorCliente,
                p.Recorrencia, p.Status, p.CicloAtual, p.InicioDoCicloAtual, p.FimDoCicloAtual,
                p.EhRecorrente, p.ValorPorAtendimento, dias, itens[p.Id].ToList(), doPacote,
                resumo);
        }).ToList();
    }

    private async Task<PacoteClienteDto> MontarClienteAsync(PacoteCliente c, CancellationToken ct) =>
        (await MontarClientesAsync(new[] { c }, ct))[0];

    /// <summary>
    /// Cada cliente no pacote com o ciclo aberto e o que falta marcar. Uma consulta por
    /// tipo de dado — nome, responsável, ciclo, marcados —, e não por cliente.
    /// </summary>
    private async Task<List<PacoteClienteDto>> MontarClientesAsync(
        IReadOnlyList<PacoteCliente> vinculos, CancellationToken ct)
    {
        if (vinculos.Count == 0)
        {
            return new List<PacoteClienteDto>();
        }

        var vinculosIds = vinculos.Select(v => v.Id).ToList();
        var clientesIds = vinculos.Select(v => v.ClienteId).Distinct().ToList();
        var responsaveisIds = vinculos.Select(v => v.ResponsavelPreferidoId)
            .OfType<long>().Distinct().ToList();

        var clientes = await _db.Clientes.AsNoTracking()
            .Where(x => clientesIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);

        var responsaveis = responsaveisIds.Count == 0
            ? new Dictionary<long, string>()
            : await _db.Usuarios.AsNoTracking()
                .Where(u => responsaveisIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.Nome, ct);

        // O ciclo aberto de cada vínculo é o de maior número entre os não encerrados.
        var ciclos = (await _db.CiclosDePacote.AsNoTracking()
                .Where(x => vinculosIds.Contains(x.PacoteClienteId) && !x.Encerrado)
                .ToListAsync(ct))
            .GroupBy(x => x.PacoteClienteId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Ciclo).First());

        var marcados = (await _db.Agendamentos.AsNoTracking()
                .Where(a => a.PacoteClienteId != null
                            && vinculosIds.Contains(a.PacoteClienteId.Value)
                            && a.Status != StatusAgendamento.Cancelado)
                .GroupBy(a => new { a.PacoteClienteId, a.PacoteCiclo })
                .Select(g => new { g.Key.PacoteClienteId, g.Key.PacoteCiclo, Quantos = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => (x.PacoteClienteId!.Value, x.PacoteCiclo), x => x.Quantos);

        return vinculos.Select(c =>
        {
            var ciclo = ciclos.GetValueOrDefault(c.Id);
            var jaMarcados = ciclo is null
                ? 0
                : marcados.GetValueOrDefault((c.Id, (int?)ciclo.Ciclo), 0);

            var preferencia = c.DiaDaSemana is { } dia
                ? $"Toda {NomeDoDia(dia)}" + (c.Hora is { } h ? $" às {h:HH\\:mm}" : string.Empty)
                : string.Empty;

            return new PacoteClienteDto(
                c.Id, c.PacoteId, c.ClienteId,
                clientes.GetValueOrDefault(c.ClienteId)?.NomeExibicao ?? "—",
                c.DiaDaSemana, c.Hora, c.ResponsavelPreferidoId,
                c.ResponsavelPreferidoId is { } rid ? responsaveis.GetValueOrDefault(rid) : null,
                c.Ativo,
                ciclo is null ? null : Montar(ciclo), preferencia,
                ciclo is null ? 0 : Math.Max(0, ciclo.Total - jaMarcados));
        }).ToList();
    }

    private static CicloDoClienteDto Montar(CicloDoCliente c) => new(
        c.Ciclo, c.Inicio, c.Fim, c.QuantidadeContratada, c.CreditoRecebido,
        c.QuantidadeUsada, c.Total, c.Disponivel, c.Encerrado,
        c.CreditoCedido, c.EstornoQuantidade, c.EstornoValor);

    private static string NomeDoDia(DayOfWeek dia) => dia switch
    {
        DayOfWeek.Sunday => "domingo",
        DayOfWeek.Monday => "segunda",
        DayOfWeek.Tuesday => "terça",
        DayOfWeek.Wednesday => "quarta",
        DayOfWeek.Thursday => "quinta",
        DayOfWeek.Friday => "sexta",
        _ => "sábado",
    };
}

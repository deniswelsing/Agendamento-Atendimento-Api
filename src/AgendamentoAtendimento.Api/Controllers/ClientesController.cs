using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Api.Contratos;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgendamentoAtendimento.Api.Controllers;

/// <summary>Clientes: pessoas físicas e empresas.</summary>
[Route("api/clientes")]
public class ClientesController : ControllerBaseApi
{
    private readonly AppDbContext _db;

    public ClientesController(AppDbContext db) => _db = db;

    [HttpGet]
    [RequerPermissao("clientes.ver")]
    public async Task<ActionResult<PaginaDto<ClienteDto>>> Listar(
        [FromQuery] string? busca,
        [FromQuery] TipoCliente? tipoCliente,
        [FromQuery] bool? somenteAtivos,
        [FromQuery] bool? somenteVip,
        [FromQuery] ParametrosDePagina? pagina,
        CancellationToken ct = default)
    {
        var p = pagina ?? new ParametrosDePagina();
        var consulta = _db.Clientes.AsNoTracking().AsQueryable();

        if (tipoCliente is { } tipo)
        {
            consulta = consulta.Where(c => c.Tipo == tipo);
        }

        if (somenteAtivos ?? true)
        {
            consulta = consulta.Where(c => c.Ativo);
        }

        if (somenteVip == true)
        {
            consulta = consulta.Where(c => c.Vip);
        }

        if (!string.IsNullOrWhiteSpace(busca))
        {
            var termo = BuscaTextual.PadraoContem(busca);
            const string escape = BuscaTextual.Escape;
            // Só vai ao documento quando o texto É um documento: tirar os dígitos de
            // qualquer busca fazia um nome com um número trazer quem tem aquele dígito
            // no CPF/CNPJ.
            var digitos = BuscaTextual.DigitosDeDocumento(busca);
            consulta = consulta.Where(c =>
                EF.Functions.ILike(c.Nome ?? string.Empty, termo, escape) ||
                EF.Functions.ILike(c.Sobrenome ?? string.Empty, termo, escape) ||
                // O nome como a pessoa o digita: "Marina Salgado" não está inteiro em
                // coluna nenhuma, e a busca pelo nome completo não achava ninguém.
                EF.Functions.ILike((c.Nome ?? string.Empty) + " " + (c.Sobrenome ?? string.Empty), termo, escape) ||
                EF.Functions.ILike(c.RazaoSocial ?? string.Empty, termo, escape) ||
                EF.Functions.ILike(c.NomeFantasia ?? string.Empty, termo, escape) ||
                EF.Functions.ILike(c.Email ?? string.Empty, termo, escape) ||
                (digitos != null && c.Documento != null && c.Documento.Contains(digitos)));
        }

        var total = await consulta.CountAsync(ct);
        var itens = await consulta
            .OrderBy(c => c.Tipo == TipoCliente.Empresa ? c.NomeFantasia ?? c.RazaoSocial : c.Nome)
            // Desempate estável: com nomes iguais, cada página ordenava do seu jeito e a
            // paginação repetia uns clientes e nunca mostrava outros.
            .ThenBy(c => c.Sobrenome)
            .ThenBy(c => c.Id)
            .Skip(p.Pular)
            .Take(p.TamanhoSeguro)
            .ToListAsync(ct);

        return Ok(new PaginaDto<ClienteDto>(
            itens.Select(c => c.ParaDto()).ToList(), p.PaginaSegura, p.TamanhoSeguro, total));
    }

    [HttpGet("{id:long}")]
    [RequerPermissao("clientes.ver")]
    public async Task<ActionResult<ClienteDto>> Obter(long id, CancellationToken ct)
    {
        var cliente = NaoNulo(
            await _db.Clientes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct),
            "Cliente não encontrado.");

        return Ok(cliente.ParaDto());
    }

    [HttpPost]
    [RequerPermissao("clientes.criar")]
    public async Task<ActionResult<ClienteDto>> Criar(ClienteRequest req, CancellationToken ct)
    {
        Validar(req, anterior: null);

        var cliente = new Cliente();
        cliente.Aplicar(req);

        _db.Clientes.Add(cliente);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Obter), new { id = cliente.Id }, cliente.ParaDto());
    }

    [HttpPut("{id:long}")]
    [RequerPermissao("clientes.editar")]
    public async Task<ActionResult<ClienteDto>> Atualizar(long id, ClienteRequest req, CancellationToken ct)
    {
        var cliente = NaoNulo(
            await _db.Clientes.FirstOrDefaultAsync(c => c.Id == id, ct),
            "Cliente não encontrado.");
        Validar(req, cliente);

        cliente.Aplicar(req);
        await _db.SaveChangesAsync(ct);

        return Ok(cliente.ParaDto());
    }

    [HttpDelete("{id:long}")]
    [RequerPermissao("clientes.excluir")]
    public async Task<IActionResult> Remover(long id, CancellationToken ct)
    {
        var cliente = NaoNulo(
            await _db.Clientes.FirstOrDefaultAsync(c => c.Id == id, ct),
            "Cliente não encontrado.");

        var temAgendamento = await _db.Agendamentos.AnyAsync(a =>
            a.ClienteId == id && a.Inicio >= DateTimeOffset.UtcNow &&
            a.Status != Domain.Agenda.StatusAgendamento.Cancelado, ct);

        if (temAgendamento)
        {
            throw new RegraDeNegocioException(
                "Este cliente tem agendamentos futuros. Cancele-os antes de excluir.",
                "CLIENTE_COM_AGENDA");
        }

        // Excluir é para cadastro feito por engano. Cliente com história — venda, atendimento,
        // pacote, fila — se desativa: excluído, as vendas dele sumiam das listas (e uma venda
        // aberta, com saldo a receber, ficava inalcançável), mas continuavam contando no
        // painel.
        var temHistorico =
            await _db.Vendas.AnyAsync(v => v.ClienteId == id, ct)
            || await _db.Agendamentos.AnyAsync(a => a.ClienteId == id, ct)
            || await _db.PacoteClientes.AnyAsync(p => p.ClienteId == id, ct)
            || await _db.ListaDeEspera.AnyAsync(e => e.ClienteId == id, ct);
        if (temHistorico)
        {
            throw new RegraDeNegocioException(
                "Este cliente já tem vendas ou atendimentos. Desative o cadastro em vez de excluir.",
                "CLIENTE_COM_HISTORICO");
        }

        _db.Clientes.Remove(cliente);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Empresa exige razão social; pessoa exige nome. O resto é opcional.</summary>
    /// <param name="anterior">
    /// O cadastro como está gravado. Formato de e-mail, telefone e documento só é conferido
    /// no que muda: um cadastro antigo continua editável mesmo com um contato que hoje seria
    /// recusado.
    /// </param>
    private static void Validar(ClienteRequest req, Cliente? anterior)
    {
        // O conversor de enum aceita qualquer número: 99 criava um cliente sem tipo e sem nome.
        if (!Enum.IsDefined(req.TipoCliente))
        {
            throw new RegraDeNegocioException("Tipo de cliente inválido.", "TIPO_CLIENTE");
        }

        if (req.TipoCliente == TipoCliente.Empresa && string.IsNullOrWhiteSpace(req.RazaoSocial))
        {
            throw new RegraDeNegocioException("Informe a razão social da empresa.", "RAZAO_SOCIAL");
        }

        if (req.TipoCliente == TipoCliente.Pessoa && string.IsNullOrWhiteSpace(req.Nome))
        {
            throw new RegraDeNegocioException("Informe o nome do cliente.", "NOME");
        }

        if (string.IsNullOrWhiteSpace(req.Email) && string.IsNullOrWhiteSpace(req.Celular))
        {
            throw new RegraDeNegocioException(
                "Informe ao menos um contato: e-mail ou celular.", "CONTATO");
        }

        static bool Mudou(string? novo, string? gravado) =>
            !string.Equals(novo?.Trim(), gravado?.Trim(), StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(req.Email) && Mudou(req.Email, anterior?.Email)
            && !Validacoes.EmailValido(req.Email))
        {
            throw new RegraDeNegocioException("O e-mail informado não é válido.", "EMAIL_INVALIDO");
        }

        foreach (var (telefone, gravado, campo) in new[]
                 {
                     (req.Celular, anterior?.Celular, "O celular"),
                     (req.Telefone, anterior?.Telefone, "O telefone"),
                     (req.WhatsApp, anterior?.WhatsApp, "O WhatsApp"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(telefone) && Mudou(telefone, gravado)
                && !Validacoes.TelefoneValido(telefone))
            {
                throw new RegraDeNegocioException($"{campo} informado não é válido.", "TELEFONE_INVALIDO");
            }
        }

        // O documento é gravado só com os dígitos: é assim que se compara com o anterior.
        if (!string.IsNullOrWhiteSpace(req.Documento)
            && (new string(req.Documento.Where(char.IsDigit).ToArray()) != (anterior?.Documento ?? string.Empty)
                || req.Documento.Any(char.IsLetter)))
        {
            ValidarDocumento(req.Documento, req.Pais);
        }

        Validacoes.Cabe(req.Nome, 150, "O nome");
        Validacoes.Cabe(req.Sobrenome, 150, "O sobrenome");
        Validacoes.Cabe(req.RazaoSocial, 250, "A razão social");
        Validacoes.Cabe(req.NomeFantasia, 250, "O nome fantasia");
        Validacoes.Cabe(req.InscricaoEstadual, 30, "A inscrição estadual");
        Validacoes.Cabe(req.Responsavel, 150, "O responsável");
        Validacoes.Cabe(req.Email, 200, "O e-mail");
        Validacoes.Cabe(req.Telefone, 30, "O telefone");
        Validacoes.Cabe(req.Celular, 30, "O celular");
        Validacoes.Cabe(req.WhatsApp, 30, "O WhatsApp");
        Validacoes.Cabe(req.Logradouro, 250, "O logradouro");
        Validacoes.Cabe(req.Numero, 20, "O número");
        Validacoes.Cabe(req.Complemento, 120, "O complemento");
        Validacoes.Cabe(req.Bairro, 120, "O bairro");
        Validacoes.Cabe(req.Municipio, 120, "O município");
        Validacoes.Cabe(req.Estado, 60, "O estado");
        Validacoes.Cabe(req.Pais, 60, "O país");
        Validacoes.Cabe(req.Cep, 20, "O CEP");
        Validacoes.Cabe(req.FotoUrl, 500, "O endereço da foto");
    }

    /// <summary>
    /// CPF ou CNPJ: só dígitos e a pontuação deles — letras sumiam em silêncio ("abc"
    /// virava documento vazio). No Brasil, 11 ou 14 dígitos.
    /// </summary>
    private static void ValidarDocumento(string documento, string? pais)
    {
        var texto = documento.Trim();
        if (!texto.All(c => char.IsDigit(c) || c is '.' or '-' or '/' or ' '))
        {
            throw new RegraDeNegocioException(
                "O documento deve ter só números (e a pontuação de CPF ou CNPJ).", "DOCUMENTO_INVALIDO");
        }

        var digitos = texto.Count(char.IsDigit);
        var noBrasil = string.IsNullOrWhiteSpace(pais) || pais.Trim().ToUpperInvariant() is "BR" or "BRASIL";
        if (noBrasil && digitos is not (11 or 14))
        {
            throw new RegraDeNegocioException(
                "Informe o CPF (11 dígitos) ou o CNPJ (14 dígitos) completo.", "DOCUMENTO_INVALIDO");
        }

        if (digitos > 20)
        {
            throw new RegraDeNegocioException(
                "O documento pode ter no máximo 20 dígitos.", "DOCUMENTO_INVALIDO");
        }
    }
}

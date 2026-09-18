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
            var termo = $"%{busca.Trim()}%";
            var digitos = new string(busca.Where(char.IsDigit).ToArray());
            consulta = consulta.Where(c =>
                EF.Functions.ILike(c.Nome ?? string.Empty, termo) ||
                EF.Functions.ILike(c.Sobrenome ?? string.Empty, termo) ||
                EF.Functions.ILike(c.RazaoSocial ?? string.Empty, termo) ||
                EF.Functions.ILike(c.NomeFantasia ?? string.Empty, termo) ||
                EF.Functions.ILike(c.Email ?? string.Empty, termo) ||
                (digitos != string.Empty && c.Documento != null && c.Documento.Contains(digitos)));
        }

        var total = await consulta.CountAsync(ct);
        var itens = await consulta
            .OrderBy(c => c.Tipo == TipoCliente.Empresa ? c.NomeFantasia ?? c.RazaoSocial : c.Nome)
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
        Validar(req);

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
        Validar(req);

        var cliente = NaoNulo(
            await _db.Clientes.FirstOrDefaultAsync(c => c.Id == id, ct),
            "Cliente não encontrado.");

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

        _db.Clientes.Remove(cliente);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Empresa exige razão social; pessoa exige nome. O resto é opcional.</summary>
    private static void Validar(ClienteRequest req)
    {
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
    }
}

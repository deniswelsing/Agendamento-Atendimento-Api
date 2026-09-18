using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Domain.Vendas;
using AgendamentoAtendimento.Infrastructure.Servicos;

namespace AgendamentoAtendimento.Api.Contratos;

/// <summary>Entidade para DTO. Nenhuma entidade sai da Api sem passar por aqui.</summary>
public static class Mapeamentos
{
    public static ClienteDto ParaDto(this Cliente c) => new(
        c.Id, c.Tipo, c.Nome, c.Sobrenome, c.RazaoSocial, c.NomeFantasia, c.InscricaoEstadual,
        c.Responsavel, c.Documento, c.Email, c.Telefone, c.Celular, c.WhatsApp, c.Logradouro,
        c.Numero, c.Complemento, c.Bairro, c.Municipio, c.Estado, c.Pais, c.Cep, c.Latitude,
        c.Longitude, c.Ativo, c.Vip, c.Observacoes, c.FotoUrl, c.NomeExibicao);

    public static void Aplicar(this Cliente c, ClienteRequest r)
    {
        c.Tipo = r.TipoCliente;
        c.Nome = r.Nome;
        c.Sobrenome = r.Sobrenome;
        c.RazaoSocial = r.RazaoSocial;
        c.NomeFantasia = r.NomeFantasia;
        c.InscricaoEstadual = r.InscricaoEstadual;
        c.Responsavel = r.Responsavel;
        c.Documento = string.IsNullOrWhiteSpace(r.Documento)
            ? null
            : new string(r.Documento.Where(char.IsDigit).ToArray());
        c.Email = r.Email;
        c.Telefone = r.Telefone;
        c.Celular = r.Celular;
        c.WhatsApp = r.WhatsApp;
        c.Logradouro = r.Logradouro;
        c.Numero = r.Numero;
        c.Complemento = r.Complemento;
        c.Bairro = r.Bairro;
        c.Municipio = r.Municipio;
        c.Estado = r.Estado;
        c.Pais = r.Pais;
        c.Cep = r.Cep;
        c.Latitude = r.Latitude;
        c.Longitude = r.Longitude;
        c.Ativo = r.IsAtivo;
        c.Vip = r.IsVip;
        c.Observacoes = r.Observacoes;
        c.FotoUrl = r.FotoUrl;
    }

    public static ItemCatalogoDto ParaDto(this ItemCatalogo i) => new(
        i.Id, i.Tipo, i.Nome, i.Descricao, i.Categoria, i.Preco, i.Custo, i.DuracaoMinutos,
        i.Estoque, i.CodigoDeBarras, i.ImagemUrl, i.Ativo, i.ComissaoPercentual, i.TaxaPercentual);

    public static void Aplicar(this ItemCatalogo i, ItemCatalogoRequest r)
    {
        i.Tipo = r.Tipo;
        i.Nome = r.Nome;
        i.Descricao = r.Descricao;
        i.Categoria = r.Categoria;
        i.Preco = r.Preco;
        i.Custo = r.Custo;
        // Duração só faz sentido em serviço; estoque só em produto.
        i.DuracaoMinutos = r.Tipo == TipoItem.Servico ? r.DuracaoMinutos : null;
        i.Estoque = r.Tipo == TipoItem.Produto ? r.Estoque : null;
        i.CodigoDeBarras = r.CodigoDeBarras;
        i.ImagemUrl = r.ImagemUrl;
        i.Ativo = r.IsAtivo;
        i.ComissaoPercentual = r.ComissaoPercentual;
        i.TaxaPercentual = r.TaxaPercentual;
    }

    public static AgendamentoDto ParaDto(this Agendamento a) => new(
        a.Id, a.ClienteId, a.Cliente?.NomeExibicao ?? string.Empty,
        a.Cliente?.Tipo ?? TipoCliente.Pessoa, a.Inicio, a.Fim, a.Status,
        a.ResponsavelId, a.Responsavel?.Nome,
        a.Itens.Select(i => new ItemAgendadoDto(
            i.ItemCatalogoId, i.Nome, i.DuracaoMinutos, i.Quantidade, i.PrecoUnitario)).ToList(),
        a.Observacoes, a.LocalAtendimento, a.VendaId,
        a.Itens.Sum(i => i.PrecoUnitario * i.Quantidade));

    public static SlotDto ParaDto(this SlotDisponivel s) =>
        new(s.Inicio, s.Fim, s.ResponsavelId, s.ResponsavelNome);

    public static DiaDaAgendaDto ParaDto(this DiaDaAgenda d) => new(
        d.Data, d.Aberto, d.Abertura, d.Fechamento, d.PausaInicio, d.PausaFim,
        d.MotivoFechado, d.IntervaloSlotMinutos, d.TotalAgendamentos,
        d.Livres.Select(s => s.ParaDto()).ToList());

    public static VendaDto ParaDto(this Venda v) => new(
        v.Id, v.ClienteId, v.Cliente?.NomeExibicao ?? string.Empty, v.Status, v.CriadoEm,
        v.AgendamentoId, v.TotalBruto, v.TotalDescontos, v.DescontoGeral, v.TotalLiquido,
        v.TotalPago, v.SaldoAberto, v.Observacao,
        v.Itens.Select(i => new VendaItemDto(
            i.Id, i.ItemCatalogoId, i.Tipo, i.Nome, i.Quantidade, i.PrecoUnitario,
            i.DescontoValor, i.TotalLiquido)).ToList(),
        v.Pagamentos.Select(p => new PagamentoDto(
            p.Id, p.FormaPagamentoId, p.FormaPagamento?.Nome ?? string.Empty, p.Status,
            p.Valor, p.ValorTaxa, p.ValorLiquido, p.Parcela, p.TotalParcelas,
            p.ConfirmadoEm, p.PrevisaoLiquidacao, p.Autorizacao)).ToList());

    public static FormaPagamentoDto ParaDto(this FormaPagamento f) => new(
        f.Id, f.Nome, f.Codigo, f.Ativa, f.PermiteParcelamento, f.MaximoParcelas,
        f.TaxaPercentual, f.TaxaFixa, f.DiasParaLiquidacao);

    public static HorarioFuncionamentoDto ParaDto(this HorarioFuncionamento h) => new(
        h.Id, h.DiaDaSemana, h.Aberto, h.Abertura, h.Fechamento, h.PausaInicio, h.PausaFim,
        h.TipoDia, h.IntervaloSlotMinutos);

    public static ExcecaoFuncionamentoDto ParaDto(this ExcecaoHorarioFuncionamento e) => new(
        e.Id, e.Data, e.Fechado, e.Abertura, e.Fechamento, e.PausaInicio, e.PausaFim, e.Motivo);

    public static HorarioStaffDto ParaDto(this HorarioStaff h) => new(
        h.Id, h.UsuarioId, h.Usuario?.Nome ?? string.Empty, h.DiaDaSemana, h.Inicio, h.Fim,
        h.PausaInicio, h.PausaFim, h.Trabalha);

    public static AusenciaStaffDto ParaDto(this ExcecaoHorarioStaff e) => new(
        e.Id, e.UsuarioId, e.Usuario?.Nome ?? string.Empty, e.Data, e.DiaInteiro,
        e.Inicio, e.Fim, e.Motivo);

    public static MembroTimeDto ParaDto(this Usuario u) => new(
        u.Id, u.Nome, u.Email, u.PerfilId, u.Perfil?.Nome ?? string.Empty, u.Ativo,
        u.OcupaAssento, u.ConvitePendente, u.Atendente, u.FotoUrl, u.UltimoLoginEm);

    public static PerfilDto ParaDto(this Perfil p)
    {
        var permissoes = p.Administrador
            ? new List<string> { Permissoes.Coringa }
            : p.Permissoes.Select(x => x.Permissao).OrderBy(x => x, StringComparer.Ordinal).ToList();

        return new PerfilDto(
            p.Id, p.Nome, p.Descricao, p.DeSistema, p.Administrador,
            permissoes, Permissoes.TelasVisiveis(permissoes), p.Usuarios.Count);
    }

    public static ModuloPermissaoDto ParaDto(this ModuloPermissao m) => new(
        m.Chave, m.Nome, m.Rota,
        m.Acoes.Select(a => new AcaoPermissaoDto(
            a.Chave, a.ChaveCompleta(m.Chave), a.Nome, a.Destrutiva)).ToList());

    /// <summary>
    /// O catálogo inteiro resolvido contra um plano: cada recurso sai marcado como incluso
    /// ou não, com o nome do menor plano que o libera. É o que a tela de planos desenha —
    /// o app não conhece nenhum recurso por conta própria.
    /// </summary>
    public static IReadOnlyList<RecursoDto> CatalogoPara(Plano plano, IEnumerable<Plano> todos)
    {
        var inclusos = plano.ChavesDeRecurso()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lista = todos.ToList();

        return CatalogoRecursos.Todos.Select(r => new RecursoDto(
            r.Chave, r.Nome, r.Grupo, r.Descricao,
            inclusos.Contains(r.Chave), r.NivelMinimo,
            CatalogoRecursos.MenorPlanoQueLibera(r.Chave, lista)?.Nome)).ToList();
    }

    public static PlanoDto ParaDto(this Plano p, IEnumerable<Plano>? todos = null) => new(
        p.Id, p.Codigo, p.Nome, p.Descricao, p.PrecoMensalUsd, p.PrecoAnualUsd,
        p.UsuariosIncluidos, p.LimiteUsuarios, p.Ordem, p.IsCustom,
        p.ChavesDeRecurso(),
        CatalogoPara(p, todos ?? new[] { p }),
        p.PaddlePriceIdMensal, p.PaddlePriceIdAnual, p.PlayProductId,
        p.PlayBasePlanIdMensal, p.PlayBasePlanIdAnual);

    public static AssinaturaDto ParaDto(
        this Assinatura a, int assentosEmUso, IEnumerable<Plano>? planos = null) => new(
        a.Id, a.TenantId, a.Plano!.ParaDto(planos), a.Ciclo, a.Gateway, a.Status,
        a.AssentosContratados, assentosEmUso,
        Math.Max(0, a.AssentosContratados - assentosEmUso),
        a.InicioCicloAtual, a.FimCicloAtual, a.ProximaCobranca, a.ValorUltimaCobrancaUsd,
        a.CancelamentoAgendado, a.Produtos.Where(p => p.Ativo).Select(p => p.ProdutoChave).ToList(),
        a.GerenciamentoUrl, a.LiberaAcesso);

    public static CotacaoDto ParaDto(this DetalhePreco d) => new(
        "USD", d.Ciclo, d.AssentosTotais, d.AssentosIncluidos, d.AssentosAdicionais,
        d.PrecoBase, d.PrecoUnitarioAssentoAdicional, d.TotalAssentosAdicionais,
        d.Total, d.EquivalenteMensal, d.EconomiaAnual, d.SobConsulta);
}

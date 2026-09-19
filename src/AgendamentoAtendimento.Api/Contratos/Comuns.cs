using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Assinaturas;
using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Usuarios;
using AgendamentoAtendimento.Domain.Vendas;

namespace AgendamentoAtendimento.Api.Contratos;

// ------------------------------------------------------------------ autenticação
public sealed record LoginRequest(string Login, string Senha, string? TenantSlug, string? Produto);

public sealed record RefreshRequest(string RefreshToken, string? Produto);

public sealed record UsuarioDto(
    long UsuarioId, string Nome, string Email, string? Perfil, long PerfilId,
    IReadOnlyList<string> Permissoes, IReadOnlyList<string> TelasVisiveis,
    string? FotoUrl, bool Administrador, bool Atendente);

public sealed record TenantDto(
    long TenantId, string Slug, string NomeEmpresa, string Moeda, string FusoHorario, string IdiomaPadrao);

public sealed record LoginResponse(
    string AccessToken, string? RefreshToken, long ExpiresInSeconds,
    UsuarioDto Usuario, TenantDto Tenant);

// ---------------------------------------------------------------------- clientes
public sealed record ClienteDto(
    long ClienteId, TipoCliente TipoCliente, string? Nome, string? Sobrenome,
    string? RazaoSocial, string? NomeFantasia, string? InscricaoEstadual, string? Responsavel,
    string? Documento, string? Email, string? Telefone, string? Celular, string? WhatsApp,
    string? Logradouro, string? Numero, string? Complemento, string? Bairro, string? Municipio,
    string? Estado, string? Pais, string? Cep, double? Latitude, double? Longitude,
    bool IsAtivo, bool IsVip, string? Observacoes, string? FotoUrl, string NomeExibicao);

public sealed record ClienteRequest(
    TipoCliente TipoCliente, string? Nome, string? Sobrenome, string? RazaoSocial,
    string? NomeFantasia, string? InscricaoEstadual, string? Responsavel, string? Documento,
    string? Email, string? Telefone, string? Celular, string? WhatsApp, string? Logradouro,
    string? Numero, string? Complemento, string? Bairro, string? Municipio, string? Estado,
    string? Pais, string? Cep, double? Latitude, double? Longitude, bool IsAtivo = true,
    bool IsVip = false, string? Observacoes = null, string? FotoUrl = null);

// ---------------------------------------------------------------------- catálogo
public sealed record ItemCatalogoDto(
    long ItemId, TipoItem Tipo, string Nome, string? Descricao, string? Categoria,
    decimal Preco, decimal Custo, int? DuracaoMinutos, int? Estoque, string? CodigoDeBarras,
    string? ImagemUrl, bool IsAtivo, decimal ComissaoPercentual, decimal TaxaPercentual);

public sealed record ItemCatalogoRequest(
    TipoItem Tipo, string Nome, string? Descricao, string? Categoria, decimal Preco,
    decimal Custo = 0, int? DuracaoMinutos = null, int? Estoque = null,
    string? CodigoDeBarras = null, string? ImagemUrl = null, bool IsAtivo = true,
    decimal ComissaoPercentual = 0, decimal TaxaPercentual = 0);

// ------------------------------------------------------------------ agendamentos
public sealed record ItemAgendadoDto(long ItemId, string Nome, int DuracaoMinutos, int Quantidade, decimal PrecoUnitario);

public sealed record AgendamentoDto(
    long AgendamentoId, long ClienteId, string ClienteNome, TipoCliente TipoCliente,
    DateTimeOffset Inicio, DateTimeOffset Fim, StatusAgendamento Status,
    long? ResponsavelId, string? ResponsavelNome, IReadOnlyList<ItemAgendadoDto> Itens,
    string? Observacoes, string? LocalAtendimento, long? VendaId, decimal ValorEstimado);

public sealed record NovoAgendamentoRequest(
    long ClienteId, DateTimeOffset Inicio, IReadOnlyList<long> ItensIds,
    long? ResponsavelId, string? Observacoes, string? LocalAtendimento);

public sealed record AlterarStatusRequest(StatusAgendamento Status, string? Motivo);

public sealed record SlotDto(DateTimeOffset Inicio, DateTimeOffset Fim, long ResponsavelId, string ResponsavelNome);

public sealed record DiaDaAgendaDto(
    DateOnly Data, bool Aberto, TimeOnly? Abertura, TimeOnly? Fechamento,
    TimeOnly? PausaInicio, TimeOnly? PausaFim, string? MotivoFechado,
    int IntervaloSlotMinutos, int TotalAgendamentos, IReadOnlyList<SlotDto> Livres);

// ------------------------------------------------------------------------ vendas
public sealed record VendaItemDto(
    long VendaItemId, long ItemId, TipoItem Tipo, string Nome, decimal Quantidade,
    decimal PrecoUnitario, decimal DescontoValor, decimal TotalLiquido,
    /// <summary>Congelado na venda: mudar o catálogo depois não mexe no que já foi vendido.</summary>
    decimal ComissaoPercentual, decimal ComissaoValor);

public sealed record PagamentoDto(
    long PagamentoId, long FormaPagamentoId, string FormaPagamentoNome, StatusPagamento Status,
    decimal Valor, decimal ValorTaxa, decimal ValorLiquido, int Parcela, int TotalParcelas,
    DateTimeOffset? ConfirmadoEm, DateOnly? PrevisaoLiquidacao, string? Autorizacao,
    MeioDeCaptura Meio,
    /// <summary>O que a alíquota configurada previa, congelado no lançamento.</summary>
    decimal ValorTaxaEstimada,
    /// <summary>false = o líquido é previsão; a adquirente ainda não confirmou a taxa.</summary>
    bool TaxaConferida,
    decimal DivergenciaDaTaxa,
    string? Nsu, string? Bandeira, string? UltimosDigitos, string? AdquirenteChave);

public sealed record VendaDto(
    long VendaId, long ClienteId, string ClienteNome, StatusVenda Status,
    DateTimeOffset CriadaEm, long? AgendamentoId, decimal TotalBruto, decimal TotalDescontos,
    decimal DescontoGeral, decimal TotalLiquido, decimal TotalPago, decimal SaldoAberto,
    string? Observacao, IReadOnlyList<VendaItemDto> Itens, IReadOnlyList<PagamentoDto> Pagamentos,
    /// <summary>Quem leva a comissão desta venda.</summary>
    long? VendedorId, string? VendedorNome, decimal TotalComissao);

public sealed record VendaItemRequest(long ItemId, decimal Quantidade, decimal? PrecoUnitario, decimal DescontoValor = 0);

public sealed record VendaRequest(
    long ClienteId, IReadOnlyList<VendaItemRequest> Itens, long? AgendamentoId,
    decimal DescontoGeral = 0, string? Observacao = null,
    /// <summary>Quem leva a comissão. Nulo herda o atendente do agendamento.</summary>
    long? VendedorId = null);

public sealed record PagamentoRequest(long FormaPagamentoId, decimal Valor, int Parcelas = 1, string? Autorizacao = null);

// ---------------------------------------------------------------------- cobrança
public sealed record CobrancaDto(
    long CobrancaId, long VendaId, StatusCobranca Status, MeioDeCaptura Meio,
    long FormaPagamentoId, string FormaPagamentoNome, decimal Valor, int Parcelas,
    string ChaveIdempotencia, string? AdquirenteChave, string? TerminalSerie,
    string? Nsu, string? CodigoAutorizacao, string? Bandeira, string? UltimosDigitos,
    string? TransacaoExternaId, string? PixCopiaECola, decimal? ValorTaxaReal,
    string? MotivoRecusa, DateTimeOffset? EnviadaEm, DateTimeOffset? RespondidaEm,
    DateTimeOffset ExpiraEm, bool EstaAberta, long? PagamentoId,
    /// <summary>true quando a chave repetida devolveu uma cobrança que já existia.</summary>
    bool JaExistia);

public sealed record AbrirCobrancaRequest(
    long FormaPagamentoId,
    decimal Valor,
    MeioDeCaptura Meio,
    /// <summary>Gerada pelo app. A mesma chave nunca abre duas cobranças.</summary>
    string ChaveIdempotencia,
    int Parcelas = 1,
    string? AdquirenteChave = null,
    string? TerminalSerie = null);

public sealed record ConcluirCobrancaRequest(
    bool Aprovada,
    string? Nsu = null,
    string? CodigoAutorizacao = null,
    string? Bandeira = null,
    string? UltimosDigitos = null,
    string? TransacaoExternaId = null,
    decimal? ValorTaxaReal = null,
    string? MotivoRecusa = null);

public sealed record EnviarCobrancaRequest(string? PixCopiaECola = null);

public sealed record CancelarCobrancaRequest(string? Motivo = null);

public sealed record ConciliarPagamentoRequest(decimal ValorTaxaReal);

public sealed record FormaPagamentoDto(
    long FormaPagamentoId, string Nome, string Codigo, bool Ativa, bool PermiteParcelamento,
    int MaximoParcelas, decimal TaxaPercentual, decimal TaxaFixa, int DiasParaLiquidacao);

public sealed record FormaPagamentoRequest(
    string Nome, string Codigo, bool Ativa = true, bool PermiteParcelamento = false,
    int MaximoParcelas = 1, decimal TaxaPercentual = 0, decimal TaxaFixa = 0, int DiasParaLiquidacao = 0);

// ---------------------------------------------------------------------- horários
public sealed record HorarioFuncionamentoDto(
    long HorarioId, DayOfWeek DiaDaSemana, bool Aberto, TimeOnly? Abertura, TimeOnly? Fechamento,
    TimeOnly? PausaInicio, TimeOnly? PausaFim, string TipoDia, int IntervaloSlotMinutos);

public sealed record HorarioFuncionamentoRequest(
    DayOfWeek DiaDaSemana, bool Aberto, TimeOnly? Abertura, TimeOnly? Fechamento,
    TimeOnly? PausaInicio, TimeOnly? PausaFim, string TipoDia = "Normal", int IntervaloSlotMinutos = 30);

public sealed record ExcecaoFuncionamentoDto(
    long ExcecaoId, DateOnly Data, bool Fechado, TimeOnly? Abertura, TimeOnly? Fechamento,
    TimeOnly? PausaInicio, TimeOnly? PausaFim, string? Motivo);

public sealed record ExcecaoFuncionamentoRequest(
    DateOnly Data, bool Fechado = true, TimeOnly? Abertura = null, TimeOnly? Fechamento = null,
    TimeOnly? PausaInicio = null, TimeOnly? PausaFim = null, string? Motivo = null);

public sealed record HorarioStaffDto(
    long HorarioId, long UsuarioId, string UsuarioNome, DayOfWeek DiaDaSemana,
    TimeOnly Inicio, TimeOnly Fim, TimeOnly? PausaInicio, TimeOnly? PausaFim, bool Trabalha);

public sealed record HorarioStaffRequest(
    long UsuarioId, DayOfWeek DiaDaSemana, TimeOnly Inicio, TimeOnly Fim,
    TimeOnly? PausaInicio, TimeOnly? PausaFim, bool Trabalha = true);

public sealed record AusenciaStaffDto(
    long AusenciaId, long UsuarioId, string UsuarioNome, DateOnly Data,
    bool DiaInteiro, TimeOnly? Inicio, TimeOnly? Fim, string? Motivo);

public sealed record AusenciaStaffRequest(
    long UsuarioId, DateOnly Data, bool DiaInteiro = true,
    TimeOnly? Inicio = null, TimeOnly? Fim = null, string? Motivo = null);

// -------------------------------------------------------------- time e permissões
public sealed record MembroTimeDto(
    long UsuarioId, string Nome, string Email, long PerfilId, string PerfilNome,
    bool IsAtivo, bool OcupaAssento, bool ConvitePendente, bool Atendente,
    string? FotoUrl, DateTimeOffset? UltimoLoginEm);

public sealed record ConvidarMembroRequest(string Nome, string Email, long PerfilId, bool Atendente = true);

public sealed record AtualizarMembroRequest(string? Nome, long? PerfilId, bool? IsAtivo, bool? Atendente);

public sealed record AcaoPermissaoDto(string Chave, string ChaveCompleta, string Nome, bool Destrutiva);

public sealed record ModuloPermissaoDto(
    string Chave, string Nome, string Rota, IReadOnlyList<AcaoPermissaoDto> Acoes);

public sealed record PerfilDto(
    long PerfilId, string Nome, string? Descricao, bool DeSistema, bool Administrador,
    IReadOnlyList<string> Permissoes, IReadOnlyList<string> TelasVisiveis, int Usuarios);

public sealed record PerfilRequest(string Nome, string? Descricao, IReadOnlyList<string> Permissoes);

// ------------------------------------------------------------------- assinatura
/// <summary>Um recurso do catálogo, já resolvido para o plano que está sendo mostrado.</summary>
public sealed record RecursoDto(
    string Chave, string Nome, string Grupo, string? Descricao,
    bool Incluso, int NivelMinimo, string? PlanoMinimo,
    /// <summary>false = o plano promete, mas o sistema ainda não entrega.</summary>
    bool Disponivel);

public sealed record GrupoRecursosDto(string Grupo, IReadOnlyList<RecursoDto> Recursos);

public sealed record PlanoDto(
    long PlanoId, string Codigo, string Nome, string? Descricao,
    decimal? PrecoMensalUsd, decimal? PrecoAnualUsd, int UsuariosIncluidos, int? LimiteUsuarios,
    int Ordem, bool IsCustom, IReadOnlyList<string> Recursos,
    IReadOnlyList<RecursoDto> Catalogo,
    string? PaddlePriceIdMensal, string? PaddlePriceIdAnual,
    string? PlayProductId, string? PlayBasePlanIdMensal, string? PlayBasePlanIdAnual);

public sealed record AssinaturaDto(
    long AssinaturaId, long TenantId, PlanoDto Plano, CicloCobranca Ciclo, GatewayPagamento Gateway,
    StatusAssinatura Status, int AssentosContratados, int AssentosEmUso, int AssentosDisponiveis,
    DateTimeOffset? InicioCicloAtual, DateTimeOffset? FimCicloAtual, DateTimeOffset? ProximaCobranca,
    decimal? ValorUltimaCobrancaUsd, bool CancelamentoAgendado, IReadOnlyList<string> Produtos,
    string? GerenciamentoUrl, bool LiberaAcesso);

public sealed record CotacaoRequest(long PlanoId, CicloCobranca Ciclo, int Assentos);

public sealed record CotacaoDto(
    string Moeda, CicloCobranca Ciclo, int AssentosTotais, int AssentosIncluidos,
    int AssentosAdicionais, decimal PrecoBase, decimal PrecoUnitarioAssento,
    decimal TotalAssentosAdicionais, decimal Total, decimal EquivalenteMensal,
    decimal EconomiaAnual, bool SobConsulta);

public sealed record PaddleCheckoutRequest(long PlanoId, CicloCobranca Ciclo, int Assentos, string ReturnUrl);

public sealed record PaddleCheckoutDto(string CheckoutUrl, string? TransactionId);

public sealed record PlayPurchaseRequest(
    string PurchaseToken, string ProductId, string? BasePlanId, long? PlanoId,
    CicloCobranca? Ciclo, int Quantidade, string TipoCompra);

public sealed record AlterarAssentosRequest(int Assentos);

// -------------------------------------------------------------------- dashboard
public sealed record ResumoDashboardDto(
    DateOnly Data, int AgendamentosHoje, int AgendamentosConfirmados, int AtendimentosConcluidos,
    int ClientesAtivos, int ClientesEmpresa, decimal FaturamentoHoje, decimal FaturamentoMes,
    decimal TicketMedio, int VendasEmAberto, string Moeda);

// -------------------------------------------------------------------- bootstrap
public sealed record OpcaoDto(string Valor, string Rotulo);

/// <summary>
/// Tudo que a interface precisa para se desenhar em uma chamada só: quem é o usuário, o que
/// ele pode ver e fazer, os rótulos dos enums e as listas curtas de apoio.
/// </summary>
public sealed record BootstrapDto(
    UsuarioDto Usuario,
    TenantDto Tenant,
    AssinaturaDto? Assinatura,
    IReadOnlyList<ModuloPermissaoDto> CatalogoPermissoes,
    IReadOnlyList<RecursoDto> CatalogoRecursos,
    IReadOnlyList<string> RecursosLiberados,
    IReadOnlyList<MembroTimeDto> Time,
    IReadOnlyList<FormaPagamentoDto> FormasPagamento,
    IReadOnlyList<HorarioFuncionamentoDto> HorarioFuncionamento,
    IReadOnlyDictionary<string, IReadOnlyList<OpcaoDto>> Opcoes);

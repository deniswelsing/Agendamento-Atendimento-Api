using AgendamentoAtendimento.Domain.Pacotes;
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
    string? ImagemUrl, bool IsAtivo, decimal ComissaoPercentual, decimal TaxaPercentual,
    bool VisivelOnline,
    /// <summary>Quantas pessoas cabem na mesma sessão. 1 é atendimento individual.</summary>
    int CapacidadeTurma = 1,
    bool EhTurma = false);

public sealed record ItemCatalogoRequest(
    TipoItem Tipo, string Nome, string? Descricao, string? Categoria, decimal Preco,
    decimal Custo = 0, int? DuracaoMinutos = null, int? Estoque = null,
    string? CodigoDeBarras = null, string? ImagemUrl = null, bool IsAtivo = true,
    decimal ComissaoPercentual = 0, decimal TaxaPercentual = 0, bool VisivelOnline = true,
    int CapacidadeTurma = 1);

// ------------------------------------------------------------------ agendamentos
public sealed record ItemAgendadoDto(
    long ItemId, string Nome, int DuracaoMinutos, int Quantidade, decimal PrecoUnitario,
    /// <summary>Id da linha — é por ele que se troca quem presta este serviço.</summary>
    long AgendamentoItemId,
    int Ordem,
    /// <summary>Quem presta ESTE serviço. A comissão da venda segue esta pessoa.</summary>
    long? ResponsavelId, string? ResponsavelNome,
    /// <summary>A janela deste serviço dentro do atendimento.</summary>
    DateTimeOffset Inicio, DateTimeOffset Fim);

public sealed record AgendamentoDto(
    long AgendamentoId, long ClienteId, string ClienteNome, TipoCliente TipoCliente,
    DateTimeOffset Inicio, DateTimeOffset Fim, StatusAgendamento Status,
    long? ResponsavelId, string? ResponsavelNome, IReadOnlyList<ItemAgendadoDto> Itens,
    string? Observacoes, string? LocalAtendimento, long? VendaId, decimal ValorEstimado,
    OrigemAgendamento Origem,
    /// <summary>
    /// Quando o cliente confirmou presença. Nulo é "ainda não respondeu" — diferente de
    /// "não vem", e diferente do status, que o time também muda.
    /// </summary>
    DateTimeOffset? ConfirmadoPeloCliente = null,
    /// <summary>
    /// O próximo passo de cobrança, decidido aqui: depende do status do atendimento, de
    /// já ter virado venda e de em que pé essa venda está — três coisas que só o
    /// servidor tem juntas.
    /// </summary>
    AcaoDeCobranca AcaoDeCobranca = AcaoDeCobranca.Nenhuma,
    /// <summary>O texto do botão, para as telas dizerem todas a mesma coisa.</summary>
    string AcaoDeCobrancaRotulo = "");

// ------------------------------------------------------- página pública (admin)
public sealed record PaginaPublicaDto(
    bool Ativa, string Slug, string? TituloPublico, string? Mensagem, string? Endereco,
    string? TelefoneContato, int AntecedenciaMinimaHoras, int JanelaMaximaDias,
    bool ExigeAprovacao, bool PermiteEscolherProfissional, bool ExigeTelefone,
    int LimiteDiarioPorCliente,
    /// <summary>Quantos serviços do catálogo a página está oferecendo agora.</summary>
    int ServicosPublicados,
    /// <summary>Pedidos esperando aprovação. É o que a tela mostra como pendência.</summary>
    int PedidosPendentes,
    /// <summary>Endereço pronto para compartilhar, montado pelo servidor.</summary>
    string Url);

public sealed record PaginaPublicaRequest(
    bool Ativa, string Slug, string? TituloPublico, string? Mensagem, string? Endereco,
    string? TelefoneContato, int AntecedenciaMinimaHoras = 2, int JanelaMaximaDias = 60,
    bool ExigeAprovacao = false, bool PermiteEscolherProfissional = true,
    bool ExigeTelefone = true, int LimiteDiarioPorCliente = 5);

public sealed record SlugDisponivelDto(string Slug, bool Disponivel, string? Motivo);

// ------------------------------------------------------ página pública (cliente)
public sealed record ServicoPublicoDto(
    long ItemId, string Nome, string? Descricao, string? Categoria,
    int DuracaoMinutos, decimal Preco);

public sealed record ProfissionalPublicoDto(long UsuarioId, string Nome);

/// <summary>
/// O que a página mostra antes de o cliente escolher qualquer coisa. Só o que quem
/// configurou marcou como público — nada de time, preço de custo ou dado interno.
/// </summary>
public sealed record PaginaPublicaInfoDto(
    string Slug, string Titulo, string? Mensagem, string? Endereco, string? Telefone,
    string Moeda, string FusoHorario, bool ExigeTelefone, bool PermiteEscolherProfissional,
    bool ExigeAprovacao, int AntecedenciaMinimaHoras,
    DateOnly PrimeiraData, DateOnly UltimaData,
    IReadOnlyList<ServicoPublicoDto> Servicos,
    IReadOnlyList<ProfissionalPublicoDto> Profissionais);

public sealed record NovoAgendamentoPublicoRequest(
    string Nome, string? Email, string? Telefone, IReadOnlyList<long> ItensIds,
    DateTimeOffset Inicio, long? ResponsavelId, string? Observacoes);

/// <summary>O comprovante do cliente. O código é o que ele guarda para voltar.</summary>
public sealed record AgendamentoPublicoDto(
    string Codigo, StatusAgendamento Status, DateTimeOffset Inicio, DateTimeOffset Fim,
    string? ProfissionalNome, IReadOnlyList<string> Servicos, decimal ValorEstimado,
    string Empresa, bool AguardandoAprovacao);

public sealed record NovoAgendamentoRequest(
    long ClienteId, DateTimeOffset Inicio, IReadOnlyList<long> ItensIds,
    long? ResponsavelId, string? Observacoes, string? LocalAtendimento,
    /// <summary>
    /// Quem presta cada serviço, na mesma ordem de <c>ItensIds</c>. Pode vir vazio — aí
    /// o servidor escolhe quem está livre e sabe fazer. Posição nula segue a mesma regra.
    /// </summary>
    IReadOnlyList<long?>? ResponsaveisPorItem = null,
    /// <summary>
    /// A etapa de cada serviço, na mesma ordem de <c>ItensIds</c>. Serviços na MESMA etapa
    /// acontecem ao mesmo tempo, cada um com a sua pessoa — o treinamento com uma e a
    /// revisão de contrato com a outra, na mesma hora. Vazio é o de sempre: cada serviço
    /// na sua etapa, um depois do outro.
    /// </summary>
    IReadOnlyList<int>? EtapasPorItem = null);

/// <summary>Troca quem presta um serviço já marcado.</summary>
public sealed record TrocarResponsavelRequest(long? ResponsavelId);

public sealed record AlterarStatusRequest(StatusAgendamento Status, string? Motivo);

public sealed record PessoaResumoDto(long UsuarioId, string Nome);

public sealed record AtribuicaoDto(
    long ItemId, string Nome, DateTimeOffset Inicio, DateTimeOffset Fim,
    long ResponsavelId, string ResponsavelNome,
    /// <summary>
    /// Quem mais poderia prestar este serviço neste horário. Um só quer dizer que não
    /// há escolha a fazer — a tela marca e segue.
    /// </summary>
    IReadOnlyList<PessoaResumoDto> Candidatos,
    /// <summary>Capacidade da sessão. 1 é atendimento individual.</summary>
    int Capacidade = 1,
    int Inscritos = 0,
    bool EhTurma = false,
    int VagasRestantes = 0);

public sealed record SlotDto(
    DateTimeOffset Inicio, DateTimeOffset Fim, long ResponsavelId, string ResponsavelNome,
    /// <summary>Um por serviço, na ordem em que foram pedidos.</summary>
    IReadOnlyList<AtribuicaoDto> Atribuicoes);

/// <summary>O próximo dia com encaixe, quando o pedido não cabe no dia perguntado.</summary>
public sealed record ProximaOportunidadeDto(DateOnly Data, SlotDto Slot);

public sealed record DiaDaAgendaDto(
    DateOnly Data, bool Aberto, TimeOnly? Abertura, TimeOnly? Fechamento,
    TimeOnly? PausaInicio, TimeOnly? PausaFim, string? MotivoFechado,
    int IntervaloSlotMinutos, int TotalAgendamentos, IReadOnlyList<SlotDto> Livres,
    /// <summary>Por que o dia não tem encaixe, quando a empresa está aberta.</summary>
    string? MotivoSemEncaixe = null,
    /// <summary>O primeiro dia à frente que tem encaixe, quando este não tem.</summary>
    ProximaOportunidadeDto? Proxima = null);

// ------------------------------------------------------------------------ vendas
public sealed record ComissaoPorVendedorDto(long VendedorId, string VendedorNome, decimal Valor);

public sealed record VendaItemDto(
    long VendaItemId, long ItemId, TipoItem Tipo, string Nome, decimal Quantidade,
    decimal PrecoUnitario, decimal DescontoValor, decimal TotalLiquido,
    /// <summary>Congelado na venda: mudar o catálogo depois não mexe no que já foi vendido.</summary>
    decimal ComissaoPercentual, decimal ComissaoValor,
    /// <summary>
    /// Quem leva a comissão DESTE item. Nulo cai no vendedor da venda — é assim que um
    /// atendimento com dois funcionários paga cada um pelo que prestou.
    /// </summary>
    long? VendedorId, string? VendedorNome);

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
    /// <summary>Quem leva a comissão desta venda, quando não há um por item.</summary>
    long? VendedorId, string? VendedorNome, decimal TotalComissao,
    /// <summary>Quanto cada pessoa leva. Somar num nome só pagaria a pessoa errada.</summary>
    IReadOnlyList<ComissaoPorVendedorDto> ComissoesPorVendedor);

public sealed record VendaItemRequest(
    long ItemId, decimal Quantidade, decimal? PrecoUnitario, decimal DescontoValor = 0,
    /// <summary>Quem leva a comissão deste item. Nulo herda o vendedor da venda.</summary>
    long? VendedorId = null);

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

/// <summary>Quem pode prestar um serviço. Lista vazia = qualquer atendente.</summary>
public sealed record ExecutoresDoServicoDto(
    long ItemId, string Nome, IReadOnlyList<MembroTimeDto> Executores, bool AbertoATodos);

public sealed record DefinirExecutoresRequest(IReadOnlyList<long> UsuariosIds);

// --------------------------------------------------------------- lista de espera
public sealed record EsperaDto(
    long EsperaId, long ClienteId, string ClienteNome, long ItemCatalogoId, string ServicoNome,
    DateOnly? DataDesejada, long? ResponsavelId, string? ResponsavelNome,
    StatusNaEspera Status, string StatusRotulo,
    DateTimeOffset CriadoEm, DateTimeOffset? AvisadoEm, long? AgendamentoId,
    string? Observacao,
    /// <summary>`Corte · qualquer dia` ou `Corte · 28/09 com Bruna`, pronto para a tela.</summary>
    string Resumo);

public sealed record NovaEsperaRequest(
    long ClienteId, long ItemCatalogoId, DateOnly? DataDesejada = null,
    long? ResponsavelId = null, string? Observacao = null);

/// <summary>Quem a fila devolveu quando uma vaga abriu.</summary>
public sealed record OportunidadeDto(
    DateOnly Data, long AgendamentoLiberadoId, IReadOnlyList<EsperaDto> Esperando);

// ---------------------------------------------------------- lembretes e confirmação
public sealed record ConfiguracaoDeLembreteDto(
    bool Ativo,
    int HorasDeAntecedencia,
    bool AvisarAoMarcar,
    bool PedirConfirmacao,
    CanalDeLembrete Canal,
    int ToleranciaDeAtrasoMinutos,
    /// <summary>
    /// Falso quando não há canal de envio configurado nesta instalação. A tela precisa
    /// dizer isso: ligar o lembrete achando que um e-mail sai seria pior que não ligar.
    /// </summary>
    bool CanalConfigurado,
    string AvisoDoCanal);

public sealed record ConfiguracaoDeLembreteRequest(
    bool Ativo,
    int HorasDeAntecedencia = 24,
    bool AvisarAoMarcar = true,
    bool PedirConfirmacao = true,
    int ToleranciaDeAtrasoMinutos = 120);

public sealed record LembreteDto(
    long LembreteId,
    long AgendamentoId,
    string ClienteNome,
    TipoDeLembrete Tipo,
    string TipoRotulo,
    CanalDeLembrete Canal,
    StatusDeLembrete Status,
    string StatusRotulo,
    DateTimeOffset QuandoEnviar,
    DateTimeOffset? EnviadoEm,
    DateTimeOffset InicioDoAtendimento,
    string Destino,
    string? Erro,
    int Tentativas);

public sealed record DespachoDto(int Enviados, int Falharam, int Expirados, string Resumo);

// ---------------------------------------------------------------------- horários
/// <summary>
/// O modo de ocupação da empresa, já com o texto que a tela mostra: quem decide isso é
/// dono de empresa, não programador, e "PorServico" sozinho não explica a escolha.
/// </summary>
public sealed record ModoDeOcupacaoDto(ModoDeOcupacao Modo, string Rotulo, string Explicacao)
{
    public static ModoDeOcupacaoDto De(ModoDeOcupacao modo) => modo switch
    {
        ModoDeOcupacao.PorFuncionario => new(
            modo,
            "Por funcionário",
            "Quem entra no atendimento fica ocupado do começo ao fim dele, mesmo nos "
            + "serviços que não presta."),
        _ => new(
            ModoDeOcupacao.PorServico,
            "Por serviço",
            "Cada pessoa fica ocupada só na janela do serviço que presta. Abre mais "
            + "encaixe quando o atendimento passa por mais de uma pessoa."),
    };
}

public sealed record ModoDeOcupacaoRequest(ModoDeOcupacao Modo);

/// <summary>
/// Os tetos diários da empresa. Zero é sem limite — e é o padrão, porque uma empresa que
/// nunca pediu teto não pode ganhar um.
/// </summary>
public sealed record LimiteDiarioDto(
    int LimiteDoDia, int LimitePorPessoa,
    /// <summary>Quantos atendimentos o dia consultado já tem.</summary>
    int UsadosHoje,
    bool TemLimiteDoDia, bool TemLimitePorPessoa,
    /// <summary>`12 de 20 atendimentos hoje` ou `sem teto`, pronto para a tela.</summary>
    string Resumo);

public sealed record LimiteDiarioRequest(int LimiteDoDia = 0, int LimitePorPessoa = 0);

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

/// <summary>Um turno nomeado da escala. `Janela` já vem pronta para a tela.</summary>
public sealed record TurnoDto(
    long TurnoId, string Nome, TimeOnly Inicio, TimeOnly Fim,
    TimeOnly? PausaInicio, TimeOnly? PausaFim, string? Cor, bool Ativo,
    int MinutosUteis, string Janela,
    /// <summary>Quantas linhas de escala usam este turno hoje.</summary>
    int EmUso);

public sealed record TurnoRequest(
    string Nome, TimeOnly Inicio, TimeOnly Fim,
    TimeOnly? PausaInicio = null, TimeOnly? PausaFim = null,
    string? Cor = null, bool Ativo = true);

public sealed record HorarioStaffDto(
    long HorarioId, long UsuarioId, string UsuarioNome, DayOfWeek DiaDaSemana,
    TimeOnly Inicio, TimeOnly Fim, TimeOnly? PausaInicio, TimeOnly? PausaFim, bool Trabalha,
    /// <summary>Nulo = horário aberto; a janela é a de `Inicio`/`Fim` desta linha.</summary>
    long? TurnoId = null,
    string? TurnoNome = null,
    /// <summary>A janela que vale de verdade: a do turno, ou a própria.</summary>
    TimeOnly InicioEfetivo = default,
    TimeOnly FimEfetivo = default);

/// <summary>
/// Atrela alguém a um turno. `Dias` vazio vale a semana que a empresa abre — é o que
/// "atrelar ao turno" quer dizer quando ninguém detalha.
/// </summary>
public sealed record AtrelarAoTurnoRequest(long TurnoId, IReadOnlyList<DayOfWeek>? Dias = null);

/// <summary>Horário próprio de um dia, soltando a pessoa de qualquer turno.</summary>
public sealed record HorarioProprioRequest(
    DayOfWeek DiaDaSemana, TimeOnly Inicio, TimeOnly Fim,
    TimeOnly? PausaInicio = null, TimeOnly? PausaFim = null, bool Trabalha = true);

public sealed record HorarioStaffRequest(
    long UsuarioId, DayOfWeek DiaDaSemana, TimeOnly Inicio, TimeOnly Fim,
    TimeOnly? PausaInicio, TimeOnly? PausaFim, bool Trabalha = true,
    /// <summary>Informe para seguir um turno; deixe nulo para horário aberto.</summary>
    long? TurnoId = null);

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
    DateOnly Data,
    /// <summary>
    /// Falso quando os contadores de agendamento são só os desta pessoa. A tela precisa
    /// dizer isso: "3 atendimentos hoje" sem dizer de quem é um número enganoso.
    /// </summary>
    bool AgendaDeTodoOTime,
    int AgendamentosHoje, int AgendamentosConfirmados, int AtendimentosConcluidos,
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

// --------------------------------------------------------------------- pacotes
public sealed record PacoteModeloDto(
    long PacoteModeloId, string Nome, string? Descricao, int Quantidade, decimal Preco,
    RecorrenciaDePacote Recorrencia, bool Ativo,
    IReadOnlyList<ItemDoPacoteDto> Itens,
    /// <summary>O que uma sessão vale. É a conta do estorno, pronta de cá.</summary>
    decimal ValorPorAtendimento,
    /// <summary>"4 atendimentos · mensal · R$ 200,00" — montado aqui para não divergir.</summary>
    string Resumo);

public sealed record ItemDoPacoteDto(long ItemId, string Nome, int DuracaoMinutos, decimal Preco);

public sealed record PacoteModeloRequest(
    string Nome, string? Descricao, int Quantidade, decimal Preco,
    RecorrenciaDePacote Recorrencia, IReadOnlyList<long> ItensIds, bool Ativo = true);

public sealed record CicloDoClienteDto(
    int Ciclo, DateOnly Inicio, DateOnly Fim, int QuantidadeContratada, int CreditoRecebido,
    int QuantidadeUsada, int Total, int Disponivel, bool Encerrado,
    int CreditoCedido, int EstornoQuantidade, decimal EstornoValor);

public sealed record PacoteClienteDto(
    long PacoteClienteId, long PacoteId, long ClienteId, string ClienteNome,
    DayOfWeek? DiaDaSemana, TimeOnly? Hora, long? ResponsavelPreferidoId,
    string? ResponsavelPreferidoNome, bool Ativo,
    CicloDoClienteDto? CicloAtual,
    /// <summary>"Toda quarta às 14:00" — ou vazio quando não há dia fixo.</summary>
    string Preferencia,
    /// <summary>Quantas sessões ainda faltam marcar neste ciclo.</summary>
    int FaltamMarcar);

public sealed record PacoteDto(
    long PacoteId, long? PacoteModeloId, string Nome, int QuantidadePorCliente,
    decimal PrecoPorCliente, RecorrenciaDePacote Recorrencia, StatusDePacote Status,
    int CicloAtual, DateOnly InicioDoCicloAtual, DateOnly FimDoCicloAtual,
    bool EhRecorrente, decimal ValorPorAtendimento, int DiasAteVencer,
    IReadOnlyList<ItemDoPacoteDto> Itens,
    IReadOnlyList<PacoteClienteDto> Clientes,
    string Resumo);

public sealed record PacoteRequest(
    string Nome, int QuantidadePorCliente, decimal PrecoPorCliente,
    RecorrenciaDePacote Recorrencia, IReadOnlyList<long> ItensIds,
    DateOnly? InicioDoCicloAtual = null,
    /// <summary>Quando vem de um modelo, o resto pode vir dele — e vem.</summary>
    long? PacoteModeloId = null);

public sealed record EntrarNoPacoteRequest(
    long ClienteId, DayOfWeek? DiaDaSemana, TimeOnly? Hora, long? ResponsavelPreferidoId);

public sealed record PropostaDePacoteDto(
    DateOnly Data, DateTimeOffset? Inicio, DateTimeOffset? Fim,
    long? ResponsavelId, string? ResponsavelNome,
    IReadOnlyList<PessoaResumoDto> Candidatos, bool TemEncaixe, string? Observacao);

/// <summary>Uma proposta aceita, do jeito que a tela a devolve para marcar.</summary>
public sealed record MarcarDoPacoteRequest(
    DateTimeOffset Inicio, long? ResponsavelId);

public sealed record AvisoDeRenovacaoDto(
    long PacoteId, string Nome, int Ciclo, DateOnly Vence, int DiasAteVencer,
    int Clientes, RecorrenciaDePacote Recorrencia, string Texto);

public sealed record VarreduraDePacotesDto(
    DateOnly Data, IReadOnlyList<AvisoDeRenovacaoDto> Avisos, int CiclosEncerrados,
    int CiclosAbertos, int PacotesEncerrados, int EstornosGerados, decimal ValorEstornado,
    string Resumo);

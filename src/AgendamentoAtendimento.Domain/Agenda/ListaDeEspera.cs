using AgendamentoAtendimento.Domain.Catalogo;
using AgendamentoAtendimento.Domain.Clientes;
using AgendamentoAtendimento.Domain.Common;
using AgendamentoAtendimento.Domain.Usuarios;

namespace AgendamentoAtendimento.Domain.Agenda;

public enum StatusNaEspera
{
    /// <summary>Na fila, sem vaga ainda.</summary>
    Aguardando = 1,

    /// <summary>Apareceu vaga e o cliente foi avisado. Ainda não é agendamento.</summary>
    Avisado = 2,

    /// <summary>Virou agendamento. É o fim feliz da fila.</summary>
    Convertido = 3,

    /// <summary>Desistiu, ou o time tirou da fila.</summary>
    Cancelado = 4,

    /// <summary>A data desejada passou sem vaga. Não some: é o que mostra demanda perdida.</summary>
    Expirado = 5,
}

/// <summary>
/// Alguém esperando vaga. A fila existe porque "não tem horário" não pode ser o fim da
/// conversa: o cliente que ficou de fora hoje é o primeiro a chamar quando alguém
/// desmarca — e sem registro ninguém lembra quem era.
/// </summary>
public class EntradaListaDeEspera : EntidadeDeTenant
{
    public long ClienteId { get; set; }
    public Cliente? Cliente { get; set; }

    /// <summary>O serviço que a pessoa quer. É por ele que a vaga é reconhecida.</summary>
    public long ItemCatalogoId { get; set; }
    public ItemCatalogo? ItemCatalogo { get; set; }

    /// <summary>
    /// O dia que a pessoa quer. Nulo é "qualquer dia" — quem só quer o serviço, e não
    /// uma data, continua na fila enquanto não for chamado.
    /// </summary>
    public DateOnly? DataDesejada { get; set; }

    /// <summary>Com quem, quando a pessoa faz questão. Nulo aceita qualquer um.</summary>
    public long? ResponsavelId { get; set; }
    public Usuario? Responsavel { get; set; }

    public StatusNaEspera Status { get; set; } = StatusNaEspera.Aguardando;

    /// <summary>Quando a vaga apareceu e o cliente foi avisado.</summary>
    public DateTimeOffset? AvisadoEm { get; set; }

    /// <summary>O agendamento que nasceu desta espera, quando nasce.</summary>
    public long? AgendamentoId { get; set; }
    public Agendamento? Agendamento { get; set; }

    public string? Observacao { get; set; }

    /// <summary>Só quem ainda pode ser chamado ocupa lugar na fila.</summary>
    public bool NaFila => Status is StatusNaEspera.Aguardando or StatusNaEspera.Avisado;

    /// <summary>
    /// Esta espera casa com a vaga que abriu? Data nula aceita qualquer dia; responsável
    /// nulo aceita qualquer pessoa. O serviço, não: quem espera por um corte não quer
    /// ser chamado para uma consultoria.
    /// </summary>
    public bool CasaCom(long itemCatalogoId, DateOnly data, long? responsavelId) =>
        NaFila
        && ItemCatalogoId == itemCatalogoId
        && (DataDesejada is null || DataDesejada == data)
        && (ResponsavelId is null || responsavelId is null || ResponsavelId == responsavelId);
}

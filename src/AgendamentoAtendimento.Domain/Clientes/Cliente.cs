using AgendamentoAtendimento.Domain.Common;

namespace AgendamentoAtendimento.Domain.Clientes;

/// <summary>
/// Diferença central em relação ao PetShop.Route: o alvo do agendamento é o próprio
/// cliente, que pode ser pessoa física ou empresa — não há Pet no meio.
/// </summary>
public enum TipoCliente
{
    Pessoa = 1,
    Empresa = 2,
}

public class Cliente : EntidadeDeTenant
{
    public TipoCliente Tipo { get; set; } = TipoCliente.Pessoa;

    // --- pessoa física
    public string? Nome { get; set; }
    public string? Sobrenome { get; set; }

    // --- empresa
    public string? RazaoSocial { get; set; }
    public string? NomeFantasia { get; set; }
    public string? InscricaoEstadual { get; set; }

    /// <summary>Pessoa de contato dentro da empresa.</summary>
    public string? Responsavel { get; set; }

    /// <summary>CPF (pessoa) ou CNPJ (empresa), somente dígitos.</summary>
    public string? Documento { get; set; }

    // --- contato
    public string? Email { get; set; }
    public string? Telefone { get; set; }
    public string? Celular { get; set; }
    public string? WhatsApp { get; set; }

    // --- endereço
    public string? Logradouro { get; set; }
    public string? Numero { get; set; }
    public string? Complemento { get; set; }
    public string? Bairro { get; set; }
    public string? Municipio { get; set; }
    public string? Estado { get; set; }
    public string? Pais { get; set; } = "BR";
    public string? Cep { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public bool Ativo { get; set; } = true;
    public bool Vip { get; set; }
    public string? Observacoes { get; set; }
    public string? FotoUrl { get; set; }

    public bool AceitaEmail { get; set; } = true;
    public bool AceitaWhatsApp { get; set; } = true;
    public bool AceitaMarketing { get; set; }

    /// <summary>Nome usado em listagens e no agendamento.</summary>
    public string NomeExibicao => Tipo == TipoCliente.Empresa
        ? (string.IsNullOrWhiteSpace(NomeFantasia) ? RazaoSocial ?? string.Empty : NomeFantasia)
        : string.Join(' ', new[] { Nome, Sobrenome }.Where(p => !string.IsNullOrWhiteSpace(p)));
}

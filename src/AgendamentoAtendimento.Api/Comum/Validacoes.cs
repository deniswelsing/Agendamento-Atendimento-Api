using System.Net.Mail;

namespace AgendamentoAtendimento.Api.Comum;

/// <summary>
/// Conferências de formulário que várias rotas repetem. Ficam aqui para que a mensagem e o
/// código sejam os mesmos em toda a Api — e para que um texto grande demais volte como 400
/// com o nome do campo, e não como o 500 que o banco devolvia.
/// </summary>
public static class Validacoes
{
    /// <summary>Recusa texto maior que a coluna, dizendo qual campo encurtar.</summary>
    public static void Cabe(string? valor, int maximo, string campo)
    {
        if (valor is not null && valor.Trim().Length > maximo)
        {
            throw new RegraDeNegocioException(
                $"{campo} pode ter no máximo {maximo} caracteres.", "CAMPO_LONGO");
        }
    }

    /// <summary>
    /// E-mail com cara de e-mail: é por ele que chegam confirmação e lembrete, e "abc" ou
    /// "isso-nao-e-email" eram aceitos em silêncio.
    /// </summary>
    public static bool EmailValido(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var texto = email.Trim();
        if (texto.Contains(' ') || !MailAddress.TryCreate(texto, out var endereco))
        {
            return false;
        }

        // MailAddress aceita "a@b"; um domínio de verdade tem ponto.
        return endereco.Address == texto && endereco.Host.Contains('.') && !endereco.Host.EndsWith('.');
    }

    /// <summary>
    /// Telefone é feito de dígitos (e da pontuação de sempre). "abc" ou "x" passavam e
    /// viravam um contato que ninguém consegue chamar.
    /// </summary>
    public static bool TelefoneValido(string? telefone)
    {
        if (string.IsNullOrWhiteSpace(telefone))
        {
            return false;
        }

        var texto = telefone.Trim();
        var digitos = texto.Count(char.IsDigit);
        return digitos >= 8 && texto.All(c => char.IsDigit(c) || c is ' ' or '(' or ')' or '-' or '+' or '.');
    }
}

namespace AgendamentoAtendimento.Api.Comum;

/// <summary>
/// Como um texto digitado na busca vira filtro. Fica num lugar só porque as listagens
/// (clientes, catálogo) precisam da mesma regra — e porque as duas armadilhas daqui
/// devolvem resultado errado sem erro nenhum, o que ninguém percebe na tela.
/// </summary>
public static class BuscaTextual
{
    /// <summary>Caractere de escape usado no ILIKE (o mesmo padrão do PostgreSQL).</summary>
    public const string Escape = "\\";

    /// <summary>Quantos dígitos, no mínimo, fazem a busca ir ao documento.</summary>
    public const int MinimoDeDigitosDoDocumento = 3;

    /// <summary>
    /// O padrão de "contém" para o ILIKE, com `%`, `_` e a barra do texto escapados:
    /// sem isso, buscar "50%" ou "a_b" virava curinga e trazia o que não foi pedido.
    /// </summary>
    public static string PadraoContem(string busca)
    {
        var termo = busca.Trim()
            .Replace(Escape, Escape + Escape)
            .Replace("%", Escape + "%")
            .Replace("_", Escape + "_");
        return $"%{termo}%";
    }

    /// <summary>
    /// Os dígitos a procurar no documento (CPF/CNPJ), ou null quando o texto não é um
    /// documento.
    ///
    /// Só vale texto que parece documento — dígitos e a pontuação de CPF/CNPJ — com um
    /// mínimo de dígitos. Tirar os dígitos de qualquer busca fazia "Ana 9" (ou um nome com
    /// um número no meio) trazer todo cliente cujo documento tem um 9.
    /// </summary>
    public static string? DigitosDeDocumento(string busca)
    {
        var texto = busca.Trim();
        if (texto.Length == 0 || !texto.All(c => char.IsDigit(c) || c is '.' or '-' or '/' or ' '))
        {
            return null;
        }

        var digitos = new string(texto.Where(char.IsDigit).ToArray());
        return digitos.Length >= MinimoDeDigitosDoDocumento ? digitos : null;
    }
}

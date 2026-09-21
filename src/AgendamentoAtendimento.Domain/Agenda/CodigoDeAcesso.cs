using System.Security.Cryptography;

namespace AgendamentoAtendimento.Domain.Agenda;

/// <summary>
/// O código opaco que deixa o cliente consultar, confirmar e desmarcar sem ter conta.
/// Fica aqui — e não dentro de um serviço — porque dois lugares o criam: a página pública
/// e o lembrete. Dois alfabetos diferentes dariam dois níveis de proteção.
/// </summary>
public static class CodigoDeAcesso
{
    /// <summary>Sem caracteres que se confundem lidos em voz alta (O/0, I/1).</summary>
    private const string Alfabeto = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Sorteado por gerador criptográfico: é ele que protege o agendamento.</summary>
    public static string Gerar()
    {
        var letras = new char[10];
        for (var i = 0; i < letras.Length; i++)
        {
            letras[i] = Alfabeto[RandomNumberGenerator.GetInt32(Alfabeto.Length)];
        }

        return new string(letras);
    }
}

namespace AgendamentoAtendimento.Api.Autenticacao;

public class OpcoesJwt
{
    public const string Secao = "Jwt";

    public string Emissor { get; set; } = "agendamento-atendimento-api";
    public string Audiencia { get; set; } = "agendamento-atendimento";

    /// <summary>Chave HMAC. Em produção vem de variável de ambiente ou cofre.</summary>
    public string Chave { get; set; } = string.Empty;

    public int MinutosDoAccessToken { get; set; } = 60;
    public int DiasDoRefreshToken { get; set; } = 30;
}

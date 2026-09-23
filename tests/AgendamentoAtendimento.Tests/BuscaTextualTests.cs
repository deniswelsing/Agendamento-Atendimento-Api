using AgendamentoAtendimento.Api.Comum;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// A busca das listagens. As duas armadilhas daqui devolvem resultado errado sem erro:
/// um nome com número trazendo quem tem aquele dígito no CPF, e `%`/`_` virando curinga.
/// </summary>
public class BuscaTextualTests
{
    [Theory]
    [InlineData("Testemudcj9hr")]
    [InlineData("Ana 9")]
    [InlineData("Studio 2")]
    [InlineData("12")]
    [InlineData("")]
    public void Texto_que_nao_e_documento_nao_vai_ao_documento(string busca)
    {
        Assert.Null(BuscaTextual.DigitosDeDocumento(busca));
    }

    [Theory]
    [InlineData("204.551.880-11", "20455188011")]
    [InlineData("12.345.678/0001-90", "12345678000190")]
    [InlineData(" 2045 ", "2045")]
    [InlineData("123", "123")]
    public void Documento_digitado_com_ou_sem_pontuacao_vira_os_digitos(string busca, string esperado)
    {
        Assert.Equal(esperado, BuscaTextual.DigitosDeDocumento(busca));
    }

    [Fact]
    public void Curingas_do_ilike_sao_escapados()
    {
        Assert.Equal(@"%50\%%", BuscaTextual.PadraoContem("50%"));
        Assert.Equal(@"%a\_b%", BuscaTextual.PadraoContem(" a_b "));
        Assert.Equal(@"%c:\\x%", BuscaTextual.PadraoContem(@"c:\x"));
    }
}

using AgendamentoAtendimento.Domain.Assinaturas;
using Xunit;

namespace AgendamentoAtendimento.Tests;

public class PrecificacaoAssinaturaTests
{
    private static Plano Platinum() => new()
    {
        Id = 2, Codigo = "PLATINUM", Nome = "Platinum",
        PrecoMensalUsd = 99m, PrecoAnualUsd = 948m,
        UsuariosIncluidos = 5, LimiteUsuarios = 25,
    };

    private static Plano Custom() => new()
    {
        Id = 4, Codigo = "CUSTOM", Nome = "Custom",
        PrecoMensalUsd = null, PrecoAnualUsd = null,
        UsuariosIncluidos = 10, LimiteUsuarios = null, IsCustom = true,
    };

    [Fact]
    public void Time_dentro_do_plano_nao_cobra_assento_adicional()
    {
        var detalhe = PrecificacaoAssinatura.Calcular(Platinum(), CicloCobranca.Mensal, 5);

        Assert.Equal(0, detalhe.AssentosAdicionais);
        Assert.Equal(0.00m, detalhe.TotalAssentosAdicionais);
        Assert.Equal(99.00m, detalhe.Total);
    }

    [Fact]
    public void Time_menor_que_o_incluso_nunca_gera_assento_negativo()
    {
        var detalhe = PrecificacaoAssinatura.Calcular(Platinum(), CicloCobranca.Mensal, 2);

        Assert.Equal(0, detalhe.AssentosAdicionais);
        Assert.Equal(99.00m, detalhe.Total);
    }

    [Theory]
    [InlineData(6, 1, 109.00)]
    [InlineData(8, 3, 129.00)]
    [InlineData(25, 20, 299.00)]
    public void Cada_usuario_alem_do_plano_custa_dez_dolares_por_mes(
        int assentos, int esperadosAdicionais, decimal total)
    {
        var detalhe = PrecificacaoAssinatura.Calcular(Platinum(), CicloCobranca.Mensal, assentos);

        Assert.Equal(esperadosAdicionais, detalhe.AssentosAdicionais);
        Assert.Equal(10.00m, detalhe.PrecoUnitarioAssentoAdicional);
        Assert.Equal(total, detalhe.Total);
    }

    [Fact]
    public void No_ciclo_anual_o_assento_custa_cento_e_vinte()
    {
        var detalhe = PrecificacaoAssinatura.Calcular(Platinum(), CicloCobranca.Anual, 8);

        Assert.Equal(120.00m, detalhe.PrecoUnitarioAssentoAdicional);
        Assert.Equal(360.00m, detalhe.TotalAssentosAdicionais);
        Assert.Equal(1308.00m, detalhe.Total);
        Assert.Equal(109.00m, detalhe.EquivalenteMensal);
    }

    [Fact]
    public void Economia_anual_vem_so_do_desconto_do_plano()
    {
        // 12 x 99 = 1188 contra 948; os assentos custam o mesmo nos dois ciclos.
        Assert.Equal(240.00m, PrecificacaoAssinatura.EconomiaAnual(Platinum(), 5));
        Assert.Equal(240.00m, PrecificacaoAssinatura.EconomiaAnual(Platinum(), 9));
    }

    [Fact]
    public void Plano_sob_consulta_cobra_apenas_os_assentos_e_e_sinalizado()
    {
        var detalhe = PrecificacaoAssinatura.Calcular(Custom(), CicloCobranca.Mensal, 12);

        Assert.True(detalhe.SobConsulta);
        Assert.Equal(2, detalhe.AssentosAdicionais);
        Assert.Equal(20.00m, detalhe.Total);
        Assert.Equal(0.00m, PrecificacaoAssinatura.EconomiaAnual(Custom(), 12));
    }

    [Fact]
    public void Assentos_totais_nunca_sao_menores_que_um()
    {
        Assert.Equal(1, PrecificacaoAssinatura.Calcular(Platinum(), CicloCobranca.Mensal, 0).AssentosTotais);
    }

    [Fact]
    public void Limite_do_plano_e_respeitado()
    {
        var plano = Platinum();

        Assert.True(plano.Suporta(25));
        Assert.False(plano.Suporta(26));
        Assert.True(Custom().Suporta(1000));
    }

    [Fact]
    public void Proporcional_cobra_so_os_dias_restantes()
    {
        Assert.Equal(10.00m, PrecificacaoAssinatura.ProporcionalNovosAssentos(
            CicloCobranca.Mensal, novosAssentos: 2, diasRestantes: 15, diasDoCiclo: 30));

        Assert.Equal(0.00m, PrecificacaoAssinatura.ProporcionalNovosAssentos(
            CicloCobranca.Anual, novosAssentos: 3, diasRestantes: 0, diasDoCiclo: 365));
    }
}

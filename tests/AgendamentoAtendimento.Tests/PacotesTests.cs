using AgendamentoAtendimento.Domain.Pacotes;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// A conta de um pacote pré-pago. O cliente pagou N atendimentos adiantado; o que ele
/// não usou não pode evaporar — desce como crédito para a recorrência seguinte, ou volta
/// como estorno quando não há seguinte.
/// </summary>
public class PacotesTests
{
    private static readonly DateTimeOffset Agora =
        new(new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero);

    private static CicloDoCliente Ciclo(int contratada, int usada, int credito = 0) => new()
    {
        TenantId = 1, Ciclo = 1, QuantidadeContratada = contratada,
        QuantidadeUsada = usada, CreditoRecebido = credito,
        Inicio = new DateOnly(2026, 9, 1), Fim = new DateOnly(2026, 9, 30),
    };

    [Fact]
    public void O_saldo_soma_o_credito_que_veio_de_tras()
    {
        var ciclo = Ciclo(contratada: 4, usada: 1, credito: 2);

        Assert.Equal(6, ciclo.Total);
        Assert.Equal(5, ciclo.Disponivel);
    }

    /// <summary>
    /// O caso do pedido: o cliente faltou duas das quatro e o pacote se renova. As duas
    /// descem para o próximo ciclo em vez de sumirem.
    /// </summary>
    [Fact]
    public void O_que_sobra_desce_para_a_proxima_recorrencia()
    {
        var ciclo = Ciclo(contratada: 4, usada: 2);

        ciclo.Encerrar(haProximoCiclo: true, valorPorAtendimento: 50m, Agora);

        Assert.Equal(2, ciclo.CreditoCedido);
        Assert.Equal(0, ciclo.EstornoQuantidade);
        Assert.Equal(0m, ciclo.EstornoValor);
        Assert.True(ciclo.Encerrado);
    }

    /// <summary>
    /// Sem próxima recorrência, o que sobrou volta como dinheiro. Duas sessões de R$ 50
    /// são R$ 100 de estorno — e a quantidade fica registrada ao lado do valor porque o
    /// preço da sessão muda de um pacote para outro.
    /// </summary>
    [Fact]
    public void Sem_proxima_recorrencia_o_que_sobra_e_estornado()
    {
        var ciclo = Ciclo(contratada: 4, usada: 2);

        ciclo.Encerrar(haProximoCiclo: false, valorPorAtendimento: 50m, Agora);

        Assert.Equal(0, ciclo.CreditoCedido);
        Assert.Equal(2, ciclo.EstornoQuantidade);
        Assert.Equal(100m, ciclo.EstornoValor);
    }

    [Fact]
    public void Ciclo_todo_usado_nao_gera_credito_nem_estorno()
    {
        var ciclo = Ciclo(contratada: 4, usada: 4);

        ciclo.Encerrar(haProximoCiclo: false, valorPorAtendimento: 50m, Agora);

        Assert.Equal(0, ciclo.CreditoCedido);
        Assert.Equal(0, ciclo.EstornoQuantidade);
    }

    /// <summary>
    /// Usar mais do que o saldo é outro problema — e não vira estorno negativo, que
    /// seria a empresa cobrando do cliente por engano.
    /// </summary>
    [Fact]
    public void Usar_alem_do_saldo_nao_vira_estorno_negativo()
    {
        var ciclo = Ciclo(contratada: 4, usada: 6);

        ciclo.Encerrar(haProximoCiclo: false, valorPorAtendimento: 50m, Agora);

        Assert.Equal(0, ciclo.EstornoQuantidade);
        Assert.Equal(0m, ciclo.EstornoValor);
    }

    /// <summary>Fechar de novo não dobra o crédito nem o estorno.</summary>
    [Fact]
    public void Fechar_duas_vezes_nao_dobra_nada()
    {
        var ciclo = Ciclo(contratada: 4, usada: 1);

        ciclo.Encerrar(haProximoCiclo: true, valorPorAtendimento: 50m, Agora);
        ciclo.Encerrar(haProximoCiclo: true, valorPorAtendimento: 50m, Agora);

        Assert.Equal(3, ciclo.CreditoCedido);
    }

    /// <summary>
    /// O crédito recebido entra no estorno quando o pacote acaba: ele foi pago num ciclo
    /// anterior e continua sendo dinheiro do cliente.
    /// </summary>
    [Fact]
    public void O_credito_herdado_tambem_e_estornado_no_fim()
    {
        var ciclo = Ciclo(contratada: 4, usada: 3, credito: 2);

        ciclo.Encerrar(haProximoCiclo: false, valorPorAtendimento: 50m, Agora);

        Assert.Equal(3, ciclo.EstornoQuantidade);
        Assert.Equal(150m, ciclo.EstornoValor);
    }

    [Theory]
    [InlineData(RecorrenciaDePacote.Semanal, 7)]
    [InlineData(RecorrenciaDePacote.Quinzenal, 14)]
    [InlineData(RecorrenciaDePacote.Mensal, 30)]
    [InlineData(RecorrenciaDePacote.Trimestral, 90)]
    [InlineData(RecorrenciaDePacote.Anual, 365)]
    public void O_ciclo_dura_o_que_a_recorrencia_diz(RecorrenciaDePacote r, int dias)
    {
        Assert.Equal(dias, Pacote.DiasDoCiclo(r));
    }

    [Fact]
    public void O_valor_da_sessao_sai_do_preco_dividido_pela_quantidade()
    {
        var pacote = new Pacote
        {
            TenantId = 1, Nome = "Mensal 4", QuantidadePorCliente = 4, PrecoPorCliente = 320m,
        };

        Assert.Equal(80m, pacote.ValorPorAtendimento);
    }

    /// <summary>Pacote sem sessão nenhuma não divide por zero.</summary>
    [Fact]
    public void Pacote_sem_quantidade_nao_divide_por_zero()
    {
        var pacote = new Pacote { TenantId = 1, Nome = "Vazio", PrecoPorCliente = 100m };

        Assert.Equal(0m, pacote.ValorPorAtendimento);
    }

    [Fact]
    public void Dias_ate_vencer_fica_negativo_depois_do_fim()
    {
        var pacote = new Pacote
        {
            TenantId = 1, Nome = "Mensal", FimDoCicloAtual = new DateOnly(2026, 9, 20),
        };

        Assert.Equal(-1, pacote.DiasAteVencer(new DateOnly(2026, 9, 21)));
        Assert.Equal(6, pacote.DiasAteVencer(new DateOnly(2026, 9, 14)));
    }
}

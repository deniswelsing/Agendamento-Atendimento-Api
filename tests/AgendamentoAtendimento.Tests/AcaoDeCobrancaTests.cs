using AgendamentoAtendimento.Domain.Agenda;
using AgendamentoAtendimento.Domain.Vendas;
using Xunit;

namespace AgendamentoAtendimento.Tests;

/// <summary>
/// O próximo passo de cobrança de um atendimento. A resposta depende de três coisas que
/// só o servidor tem juntas — o status do atendimento, se já virou venda e em que pé
/// está essa venda —, então é ele que decide, e as três telas mostram o mesmo botão.
/// </summary>
public class AcaoDeCobrancaTests
{
    private static Agendamento Com(
        StatusAgendamento status, long? vendaId = null, bool comItem = true)
    {
        var a = new Agendamento
        {
            TenantId = 1, ClienteId = 1, Status = status, VendaId = vendaId,
            Inicio = DateTimeOffset.UtcNow, Fim = DateTimeOffset.UtcNow.AddHours(1),
        };
        if (comItem)
        {
            a.Itens.Add(new AgendamentoItem
            {
                TenantId = 1, ItemCatalogoId = 1, Nome = "Corte",
                DuracaoMinutos = 30, Quantidade = 1, PrecoUnitario = 60m,
            });
        }
        return a;
    }

    [Fact]
    public void Concluido_sem_venda_oferece_gerar()
    {
        var acao = Com(StatusAgendamento.Concluido).CobrancaDisponivel(null);

        Assert.Equal(AcaoDeCobranca.GerarVenda, acao);
        Assert.Equal("Receber pagamento", Agendamento.RotuloDaCobranca(acao));
    }

    /// <summary>Faturar conclui o que está em andamento — então ele também já pode.</summary>
    [Fact]
    public void Em_atendimento_tambem_ja_pode_virar_venda()
    {
        Assert.Equal(
            AcaoDeCobranca.GerarVenda,
            Com(StatusAgendamento.EmAtendimento).CobrancaDisponivel(null));
    }

    [Theory]
    [InlineData(StatusAgendamento.Agendado)]
    [InlineData(StatusAgendamento.Confirmado)]
    [InlineData(StatusAgendamento.Cancelado)]
    [InlineData(StatusAgendamento.NaoCompareceu)]
    public void O_que_nao_foi_entregue_nao_se_cobra(StatusAgendamento status)
    {
        Assert.Equal(AcaoDeCobranca.Nenhuma, Com(status).CobrancaDisponivel(null));
    }

    [Fact]
    public void Sem_servico_nao_ha_o_que_cobrar()
    {
        Assert.Equal(
            AcaoDeCobranca.Nenhuma,
            Com(StatusAgendamento.Concluido, comItem: false).CobrancaDisponivel(null));
    }

    [Theory]
    [InlineData(StatusVenda.Aberta)]
    [InlineData(StatusVenda.AguardandoPagamento)]
    public void Venda_com_saldo_leva_ao_recebimento(StatusVenda status)
    {
        var acao = Com(StatusAgendamento.Concluido, vendaId: 7).CobrancaDisponivel(status);

        Assert.Equal(AcaoDeCobranca.ReceberPagamento, acao);
        Assert.Equal("Receber pagamento", Agendamento.RotuloDaCobranca(acao));
    }

    /// <summary>Quitada não se recebe de novo: o botão passa a ser de conferência.</summary>
    [Fact]
    public void Venda_paga_so_tem_o_que_conferir()
    {
        var acao = Com(StatusAgendamento.Concluido, vendaId: 7)
            .CobrancaDisponivel(StatusVenda.Paga);

        Assert.Equal(AcaoDeCobranca.VerVenda, acao);
        Assert.Equal("Ver venda", Agendamento.RotuloDaCobranca(acao));
    }

    /// <summary>
    /// Venda cancelada devolve o atendimento à fila. O serviço foi entregue e continua a
    /// receber — tratá-la como venda existente deixaria o trabalho sem cobrança para
    /// sempre por causa de um cancelamento.
    /// </summary>
    [Fact]
    public void Venda_cancelada_devolve_o_atendimento_para_a_fila()
    {
        Assert.Equal(
            AcaoDeCobranca.GerarVenda,
            Com(StatusAgendamento.Concluido, vendaId: 7)
                .CobrancaDisponivel(StatusVenda.Cancelada));
    }

    /// <summary>
    /// Já tem venda e não se sabe o status: só dá para olhar. Oferecer gerar prometeria
    /// uma venda que a Api recusa com ATENDIMENTO_JA_FATURADO, e quem tocasse no botão
    /// só descobriria no erro.
    /// </summary>
    [Fact]
    public void Sem_o_status_da_venda_so_da_para_olhar()
    {
        Assert.Equal(
            AcaoDeCobranca.VerVenda,
            Com(StatusAgendamento.Concluido, vendaId: 7).CobrancaDisponivel(null));
    }
}

using AgendamentoAtendimento.Domain.Assinaturas;
using Xunit;

namespace AgendamentoAtendimento.Tests;

public class CatalogoRecursosTests
{
    private static Plano Plano(string codigo, int ordem, string? recursos) => new()
    {
        Id = ordem, Codigo = codigo, Nome = codigo, Ordem = ordem, Recursos = recursos,
    };

    private static List<Plano> Tabela() => new()
    {
        Plano("BASIC", 1, CatalogoRecursos.ListaAteNivel(1)),
        Plano("PLATINUM", 2, CatalogoRecursos.ListaAteNivel(2)),
        Plano("ULTIMATE", 3, CatalogoRecursos.ListaAteNivel(3)),
        Plano("CUSTOM", 4, CatalogoRecursos.ListaAteNivel(4)),
    };

    [Fact]
    public void Cada_degrau_acumula_o_anterior()
    {
        var basic = CatalogoRecursos.AteNivel(1);
        var platinum = CatalogoRecursos.AteNivel(2);
        var ultimate = CatalogoRecursos.AteNivel(3);
        var custom = CatalogoRecursos.AteNivel(4);

        Assert.Subset(platinum.ToHashSet(), basic.ToHashSet());
        Assert.Subset(ultimate.ToHashSet(), platinum.ToHashSet());
        Assert.Subset(custom.ToHashSet(), ultimate.ToHashSet());

        Assert.True(basic.Count < platinum.Count);
        Assert.True(platinum.Count < ultimate.Count);
        Assert.True(ultimate.Count < custom.Count);
        Assert.Equal(CatalogoRecursos.Todos.Count, custom.Count);
    }

    [Fact]
    public void O_plano_mais_alto_libera_o_catalogo_inteiro()
    {
        var custom = Plano("CUSTOM", 4, CatalogoRecursos.ListaAteNivel(4));

        Assert.All(CatalogoRecursos.Todos, r => Assert.True(custom.Libera(r.Chave)));
    }

    [Fact]
    public void Tudo_continua_liberando_o_catalogo_inteiro()
    {
        // Valor histórico, gravado antes do catálogo existir.
        var antigo = Plano("CUSTOM", 4, CatalogoRecursos.Tudo);

        Assert.Equal(CatalogoRecursos.Todos.Count, antigo.ChavesDeRecurso().Count);
        Assert.True(antigo.Libera(CatalogoRecursos.ApiPublica));
    }

    [Fact]
    public void Chave_desconhecida_no_plano_e_descartada()
    {
        var plano = Plano("BASIC", 1, "agendamentos,recurso-que-nao-existe");

        Assert.Equal(new[] { CatalogoRecursos.Agendamentos }, plano.ChavesDeRecurso());
        Assert.False(plano.Libera("recurso-que-nao-existe"));
    }

    [Fact]
    public void Plano_do_basic_nao_libera_recurso_de_degrau_maior()
    {
        var basic = Plano("BASIC", 1, CatalogoRecursos.ListaAteNivel(1));

        Assert.True(basic.Libera(CatalogoRecursos.Agendamentos));
        Assert.False(basic.Libera(CatalogoRecursos.MultiUsuario));
        Assert.False(basic.Libera(CatalogoRecursos.RelatoriosAvancados));
    }

    [Theory]
    [InlineData(CatalogoRecursos.Agendamentos, "BASIC")]
    [InlineData(CatalogoRecursos.MultiUsuario, "PLATINUM")]
    [InlineData(CatalogoRecursos.Comissoes, "ULTIMATE")]
    [InlineData(CatalogoRecursos.ApiPublica, "CUSTOM")]
    public void O_cadeado_aponta_o_menor_plano_que_libera(string chave, string esperado)
    {
        var minimo = CatalogoRecursos.MenorPlanoQueLibera(chave, Tabela());

        Assert.NotNull(minimo);
        Assert.Equal(esperado, minimo!.Codigo);
    }

    [Fact]
    public void Plano_inativo_nao_conta_como_cadeado()
    {
        var tabela = Tabela();
        tabela[1].Ativo = false; // Platinum fora do ar

        var minimo = CatalogoRecursos.MenorPlanoQueLibera(CatalogoRecursos.MultiUsuario, tabela);

        Assert.Equal("ULTIMATE", minimo!.Codigo);
    }

    [Fact]
    public void Sanitizar_normaliza_e_descarta()
    {
        var chaves = CatalogoRecursos.Sanitizar(
            new[] { " Multi-Usuario ", "api-publica", "inventado", "", "api-publica" });

        Assert.Equal(new[] { CatalogoRecursos.MultiUsuario, CatalogoRecursos.ApiPublica }, chaves);
    }

    /// <summary>
    /// A migração RecursosPorPlano grava as chaves em SQL literal. Se o catálogo mudar sem
    /// que uma nova migração acompanhe, o banco fica para trás — este teste denuncia.
    /// </summary>
    [Fact]
    public void A_migracao_grava_exatamente_o_que_o_catalogo_diz()
    {
        const string nivel1 = "agendamentos,clientes,catalogo,vendas,pagina-online,lembrete-email";

        const string nivel2 = nivel1 +
            ",multi-usuario,jornada-por-pessoa,politica-cancelamento,cartao-em-arquivo" +
            ",lembrete-sms-whatsapp,google-calendar,agendamento-recorrente,sem-marca";

        const string nivel3 = nivel2 +
            ",relatorios-avancados,comissoes,multiunidade,permissoes-avancadas" +
            ",recursos-reservaveis";

        const string nivel4 = nivel3 + ",onboarding-dedicado,api-publica,gerente-de-conta";

        Assert.Equal(nivel1, CatalogoRecursos.ListaAteNivel(1));
        Assert.Equal(nivel2, CatalogoRecursos.ListaAteNivel(2));
        Assert.Equal(nivel3, CatalogoRecursos.ListaAteNivel(3));
        Assert.Equal(nivel4, CatalogoRecursos.ListaAteNivel(4));
    }
}

using AgendamentoAtendimento.Domain.Usuarios;
using Xunit;

namespace AgendamentoAtendimento.Tests;

public class PermissoesTests
{
    [Fact]
    public void Catalogo_expoe_uma_tela_por_modulo_com_acao_ver()
    {
        Assert.NotEmpty(Permissoes.Modulos);
        Assert.All(Permissoes.Modulos, m =>
            Assert.Contains(m.Acoes, a => a.Chave == Permissoes.AcaoVer));
    }

    [Fact]
    public void Sanitizar_descarta_chave_desconhecida()
    {
        var validas = Permissoes.Sanitizar(new[] { "clientes.editar", "inventada.total", "  " });

        Assert.Equal(new[] { "clientes.editar" }, validas);
    }

    [Fact]
    public void Acao_implica_a_tela_dela()
    {
        // Sem isto o perfil ficaria "cego que edita": pode criar, mas não vê a tela.
        var completas = Permissoes.ComTelasImplicadas(new[] { "agenda.criar" });

        Assert.Contains("agenda.ver", completas);
        Assert.Contains("agenda.criar", completas);
    }

    [Fact]
    public void Telas_visiveis_seguem_a_acao_ver()
    {
        var telas = Permissoes.TelasVisiveis(new[] { "agenda.ver", "vendas.ver" });

        Assert.Equal(new[] { "agenda", "vendas" }, telas);
    }

    [Fact]
    public void Administrador_ve_todas_as_telas_pelo_coringa()
    {
        var telas = Permissoes.TelasVisiveis(new[] { Permissoes.Coringa });

        Assert.Equal(Permissoes.Modulos.Count, telas.Count);
        Assert.True(Permissoes.Permite(new[] { Permissoes.Coringa }, "qualquer.coisa"));
    }

    [Fact]
    public void Permite_exige_a_chave_exata_quando_nao_e_admin()
    {
        var permissoes = new[] { "clientes.ver" };

        Assert.True(Permissoes.Permite(permissoes, "clientes.ver"));
        Assert.False(Permissoes.Permite(permissoes, "clientes.editar"));
    }
}

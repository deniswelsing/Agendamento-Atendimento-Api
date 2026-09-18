using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AgendamentoAtendimento.Domain.MultiTenancy;
using AgendamentoAtendimento.Domain.Usuarios;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AgendamentoAtendimento.Api.Autenticacao;

public sealed record TokenEmitido(string AccessToken, DateTimeOffset ExpiraEm, long ExpiraEmSegundos);

/// <summary>
/// Emite o access token. As permissões viajam dentro dele para que a autorização não
/// precise ir ao banco a cada requisição — e é o mesmo conjunto que o app usa para decidir
/// o que desenhar.
/// </summary>
public class ServicoDeToken
{
    private readonly OpcoesJwt _opcoes;

    public ServicoDeToken(IOptions<OpcoesJwt> opcoes) => _opcoes = opcoes.Value;

    public TokenEmitido Emitir(
        Usuario usuario, Tenant tenant, IEnumerable<string> permissoes, string produto)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        ArgumentNullException.ThrowIfNull(tenant);

        var agora = DateTimeOffset.UtcNow;
        var expiraEm = agora.AddMinutes(_opcoes.MinutosDoAccessToken);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, usuario.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, usuario.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.Name, usuario.Nome),
            new(ClaimsApp.UsuarioId, usuario.Id.ToString()),
            new(ClaimsApp.TenantId, tenant.Id.ToString()),
            new(ClaimsApp.TenantSlug, tenant.Slug),
            new(ClaimsApp.Produto, produto),
        };

        if (usuario.Perfil is not null)
        {
            claims.Add(new Claim(ClaimsApp.Perfil, usuario.Perfil.Nome));
        }

        claims.AddRange(permissoes.Distinct().Select(p => new Claim(ClaimsApp.Permissao, p)));

        var credenciais = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opcoes.Chave)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _opcoes.Emissor,
            audience: _opcoes.Audiencia,
            claims: claims,
            notBefore: agora.UtcDateTime,
            expires: expiraEm.UtcDateTime,
            signingCredentials: credenciais);

        return new TokenEmitido(
            new JwtSecurityTokenHandler().WriteToken(token),
            expiraEm,
            (long)(expiraEm - agora).TotalSeconds);
    }
}

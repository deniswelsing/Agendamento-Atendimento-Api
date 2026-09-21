using AgendamentoAtendimento.Api.Jobs;
using System.Text;
using System.Text.Json.Serialization;
using AgendamentoAtendimento.Api.Autenticacao;
using AgendamentoAtendimento.Api.Comum;
using AgendamentoAtendimento.Infrastructure.Persistencia;
using AgendamentoAtendimento.Infrastructure.Persistencia.Seed;
using AgendamentoAtendimento.Infrastructure.Servicos;
using AgendamentoAtendimento.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------------ persistência
var conexao = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres não configurada.");

builder.Services.AddDbContext<AppDbContext>(opcoes =>
    opcoes.UseNpgsql(conexao, npgsql => npgsql.MigrationsAssembly(
            typeof(AppDbContext).Assembly.FullName))
        // Tabelas e colunas em snake_case, como manda o costume em PostgreSQL.
        .UseSnakeCaseNamingConvention());

// O contexto é por requisição: quem é o tenant, o usuário e o produto que está chamando.
builder.Services.AddScoped<ContextoAtual>();
builder.Services.AddScoped<IContextoAtual>(sp => sp.GetRequiredService<ContextoAtual>());

builder.Services.AddScoped<DisponibilidadeService>();
builder.Services.AddScoped<AssinaturaService>();
builder.Services.AddScoped<VendaService>();
builder.Services.AddScoped<CobrancaService>();
builder.Services.AddScoped<PaginaPublicaService>();
builder.Services.AddScoped<LembreteService>();
builder.Services.AddScoped<ListaDeEsperaService>();
builder.Services.AddScoped<PacoteAgendaService>();
builder.Services.AddScoped<RecorrenciaDePacotesService>();
// A varredura diária dos pacotes: avisa o que vence e vira o ciclo do que venceu.
builder.Services.AddHostedService<JobDiarioDePacotes>();
// O canal real (SMTP, provedor) é escolha de quem hospeda. Sem um configurado, o aviso
// vai para o log — e a fila diz isso, em vez de a tela garantir um e-mail que não saiu.
builder.Services.AddScoped<IEnviadorDeLembrete, EnviadorDeLembreteEmLog>();
builder.Services.AddScoped<ServicoDeToken>();

// ------------------------------------------------------------------ autenticação
builder.Services.Configure<OpcoesJwt>(builder.Configuration.GetSection(OpcoesJwt.Secao));
var opcoesJwt = builder.Configuration.GetSection(OpcoesJwt.Secao).Get<OpcoesJwt>() ?? new OpcoesJwt();

if (string.IsNullOrWhiteSpace(opcoesJwt.Chave) || opcoesJwt.Chave.Length < 32)
{
    if (builder.Environment.IsDevelopment())
    {
        opcoesJwt.Chave = "chave-de-desenvolvimento-nao-usar-em-producao-0001";
        builder.Services.PostConfigure<OpcoesJwt>(o => o.Chave = opcoesJwt.Chave);
    }
    else
    {
        throw new InvalidOperationException(
            "Jwt:Chave precisa ter ao menos 32 caracteres fora de desenvolvimento.");
    }
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opcoes =>
    {
        opcoes.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = opcoesJwt.Emissor,
            ValidAudience = opcoesJwt.Audiencia,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opcoesJwt.Chave)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissaoPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissaoHandler>();
builder.Services.AddAuthorization();

// ------------------------------------------------------------------------- mvc
builder.Services
    .AddControllers()
    .AddJsonOptions(opcoes =>
    {
        opcoes.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        // Enums viajam como texto: o app lê "EMPRESA", não 2.
        opcoes.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        // Hora com ou sem segundos: "09:00" é o que um campo de hora produz.
        opcoes.JsonSerializerOptions.Converters.Add(new HoraFlexivelConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opcoes =>
{
    opcoes.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Agendamento & Atendimento API",
        Version = "v1",
        Description = "Backend do aplicativo Android. Todo dado e toda permissão saem daqui.",
    });
    opcoes.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
    });
    opcoes.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
        }] = Array.Empty<string>(),
    });
});

builder.Services.AddCors(opcoes => opcoes.AddDefaultPolicy(politica =>
    politica.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()));

builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

var app = builder.Build();

app.UseMiddleware<TratamentoDeErroMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseMiddleware<ContextoMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();

// ---------------------------------------------------------- migrations e seed
await using (var escopo = app.Services.CreateAsyncScope())
{
    var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
    var contexto = escopo.ServiceProvider.GetRequiredService<ContextoAtual>();

    if (app.Configuration.GetValue("Banco:AplicarMigrationsNoStartup", true))
    {
        await db.Database.MigrateAsync();
    }

    contexto.IgnorarFiltroDeTenant = true;
    await DadosIniciais.GarantirPlanosAsync(db);
    contexto.IgnorarFiltroDeTenant = false;

    if (app.Configuration.GetValue("Banco:SemearDemo", false))
    {
        var senha = app.Configuration["Banco:SenhaDemo"] ?? "Agendamento@2026";
        await DadosIniciais.GarantirTenantDemoAsync(db, contexto, senha);
    }
}

await app.RunAsync();

/// <summary>Exposto para os testes de integração.</summary>
public partial class Program;

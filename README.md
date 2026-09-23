# Agendamento & Atendimento — API

Backend em **.NET 8 + PostgreSQL** do aplicativo Android
[Agendamento-Atendimento-Mobile](https://github.com/deniswelsing/Agendamento-Atendimento-Mobile).

O princípio da arquitetura é simples: **todo dado e toda permissão saem daqui**. O
aplicativo é camada de apresentação — ele desenha o que a API devolve e habilita o que a
API permite. Nenhuma regra de preço, disponibilidade ou visibilidade de tela vive no
cliente.

## Estrutura

```
src/
├── AgendamentoAtendimento.Domain/          entidades e regras puras (sem EF, sem ASP.NET)
│   ├── Assinaturas/PrecificacaoAssinatura  a regra dos US$ 10 por usuário extra
│   └── Usuarios/CatalogoPermissoes         catálogo de telas × ações
├── AgendamentoAtendimento.Infrastructure/  EF Core, PostgreSQL, migrations, serviços
│   ├── Persistencia/AppDbContext           multi-tenant, soft delete e auditoria
│   └── Servicos/DisponibilidadeService     motor de encaixes da agenda
└── AgendamentoAtendimento.Api/             controllers, JWT, autorização por permissão
tests/
└── AgendamentoAtendimento.Tests/           xUnit
```

## Como rodar

```bash
# banco
createdb agendamento   # ou docker run -e POSTGRES_PASSWORD=... postgres:16

# a connection string fica em appsettings.json (ConnectionStrings:Postgres)
dotnet run --project src/AgendamentoAtendimento.Api
```

Em `Development` a API aplica as migrations no start e semeia um tenant de demonstração
(`Banco:SemearDemo`). Swagger em `/swagger`, health check em `/health`.

`PaginaPublica:BaseUrl` é o endereço do painel web: o link da página pública que o dono
compartilha sai como `{BaseUrl}/p/{slug}`. Vazio, a Api usa o próprio host — o que só
serve quando o painel é servido pela mesma origem.

```bash
dotnet test           # 26 testes
```

## Migrations

```bash
dotnet tool install --global dotnet-ef --version 8.0.11

dotnet ef migrations add <Nome> \
  --project src/AgendamentoAtendimento.Infrastructure \
  --startup-project src/AgendamentoAtendimento.Api \
  --output-dir Persistencia/Migrations

dotnet ef database update \
  --project src/AgendamentoAtendimento.Infrastructure \
  --startup-project src/AgendamentoAtendimento.Api
```

Migrations existentes:

| Migration | O que faz |
|---|---|
| `Inicial` | 22 tabelas, índices e chaves do modelo inteiro |
| `IndicesUnicosIgnoramExcluidos` | torna os 8 índices únicos parciais (`WHERE excluido = false`), para que um registro excluído logicamente não continue segurando a chave |

Em produção, deixe `Banco:AplicarMigrationsNoStartup: false` e rode
`dotnet ef database update` (ou o script gerado por `dotnet ef migrations script`) no
pipeline de deploy.

## Decisões que valem saber

**Multi-tenant por filtro global.** Toda entidade de tenant tem `TenantId` preenchido e
filtrado automaticamente pelo `AppDbContext` a partir do token. Nenhum controller escreve
`Where(x => x.TenantId == ...)` — não dá para esquecer.

**Exclusão lógica.** Entidades de tenant têm `Excluido`; o `DELETE` vira `UPDATE`. Por isso
os índices únicos são parciais: sem isso, um e-mail removido bloquearia o recadastro.

**Auditoria automática.** Toda inserção, alteração e exclusão de entidade de tenant grava
uma linha em `audit_logs` com o que mudou, quem mudou e por qual produto. Campos com
`senha` ou `token` no nome nunca entram na trilha.

**Permissões como catálogo.** `GET /api/perfis/catalogo` devolve as telas e as ações de
cada uma. O admin monta os perfis a partir dessa lista e o app renderiza a tela de
permissões sem conhecer nenhuma chave por conta própria. Uma ação sempre implica a tela
dela, então não existe perfil "cego que edita".

**Assinatura compartilhada com o PetShop.Route.** O cabeçalho `X-Produto` diz qual produto
está chamando; a tabela `assinatura_produtos` diz quais produtos o plano cobre. Os assentos
são contados uma vez só para a suíte, e o mesmo refresh token troca de produto em
`POST /api/auth/refresh`.

**402 é um código de negócio.** Assinatura inativa, produto não coberto ou time sem assento
livre respondem `402 Payment Required` com o motivo. É por esse status que o app abre a
tela de compra de assento.

## Endpoints

Documentação completa em [`docs/ENDPOINTS.md`](docs/ENDPOINTS.md).

| Área | Rotas |
|---|---|
| Autenticação | `POST /api/auth/{login,refresh,logout}` |
| Bootstrap | `GET /api/bootstrap` — usuário, permissões, telas, time, formas, horários, rótulos |
| Clientes | CRUD em `/api/clientes` |
| Catálogo | CRUD em `/api/catalogo/itens` |
| Agenda | CRUD em `/api/agendamentos` + `/disponibilidade` e `/disponibilidade/periodo` |
| Vendas | CRUD em `/api/vendas` + `/pagamentos`, `/finalizar` |
| Financeiro | CRUD em `/api/formas-pagamento` |
| Horários | `/api/horarios/funcionamento`, `/staff` e as exceções de cada um |
| Time | CRUD em `/api/time/membros` |
| Perfis | CRUD em `/api/perfis` + `/catalogo` |
| Assinatura | `/api/assinatura/{planos,atual,cotacao,assentos}`, Paddle e Google Play |
| Painel | `GET /api/dashboard/resumo` |

## Pendências conhecidas

As chamadas externas dos gateways estão isoladas e **ainda não implementadas**:
`CriarTransacaoPaddleAsync` e `ValidarCompraPlayAsync` em `AssinaturaController` lançam
erro explicando o que falta configurar. Todo o resto do fluxo de assinatura (planos,
cotação, assentos, entitlement por produto) está pronto e coberto por testes.

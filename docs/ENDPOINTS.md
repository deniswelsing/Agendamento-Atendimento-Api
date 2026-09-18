# Endpoints

Toda rota autenticada espera:

| Cabeçalho | Conteúdo |
|---|---|
| `Authorization` | `Bearer <accessToken>` |
| `X-Produto` | `agendamento-atendimento` ou `petshop-route` |

Erros voltam como `{ "message": "...", "code": "..." }`.

| Status | Significado |
|---|---|
| 400 | regra de negócio; `code` identifica qual |
| 401 | token ausente, inválido ou expirado |
| 403 | falta a permissão exigida pela rota |
| **402** | assinatura inativa, produto não coberto ou **time sem assento livre** |
| 404 | registro não encontrado |

## Autenticação

```
POST /api/auth/login    { login, senha, tenantSlug?, produto? }  -> LoginResponse
POST /api/auth/refresh  { refreshToken, produto? }               -> LoginResponse
POST /api/auth/logout
```

`LoginResponse.usuario` já traz `permissoes` e `telasVisiveis` — é o que o app usa para
montar a navegação.

## Bootstrap

```
GET /api/bootstrap -> usuario, tenant, assinatura, catalogoPermissoes,
                      catalogoRecursos, recursosLiberados,
                      time, formasPagamento, horarioFuncionamento, opcoes
```

`opcoes` traz os rótulos dos enums (`tipoCliente`, `statusAgendamento`, `statusVenda`,
`cicloCobranca`, `diaDaSemana`…) para que o app não traduza nada por conta própria.

Cada lista só vem se o usuário tiver a permissão correspondente (`time.ver`,
`financeiro.ver`, `horarios.ver`).

`catalogoRecursos` traz o catálogo inteiro já resolvido contra o plano assinado — cada
recurso com `incluso` e o `planoMinimo` que o libera — e `recursosLiberados` é só a lista
de chaves inclusas. É com isso que o app decide o cadeado; ele não conhece a tabela de
planos.

## Permissões exigidas por rota

| Rota | Permissão |
|---|---|
| `GET /api/dashboard/resumo` | `dashboard.ver` |
| `GET /api/clientes` | `clientes.ver` |
| `POST /api/clientes` | `clientes.criar` |
| `PUT /api/clientes/{id}` | `clientes.editar` |
| `DELETE /api/clientes/{id}` | `clientes.excluir` |
| `GET /api/catalogo/itens` | `catalogo.ver` |
| `POST/PUT/DELETE /api/catalogo/itens` | `catalogo.criar` / `.editar` / `.excluir` |
| `GET /api/agendamentos`, `/disponibilidade` | `agenda.ver` |
| `POST /api/agendamentos` | `agenda.criar` |
| `PUT /api/agendamentos/{id}` | `agenda.editar` |
| `PATCH /api/agendamentos/{id}/status` | `agenda.concluir` (cancelar exige `agenda.cancelar`) |
| `DELETE /api/agendamentos/{id}` | `agenda.cancelar` |
| `GET /api/vendas` | `vendas.ver` |
| `POST /api/vendas` | `vendas.criar` |
| `POST /api/vendas/{id}/pagamentos` | `financeiro.receber` |
| `POST /api/vendas/{id}/finalizar` | `vendas.finalizar` |
| `DELETE /api/vendas/{id}` | `vendas.cancelar` |
| `GET /api/formas-pagamento` | `financeiro.ver` |
| `POST/PUT/DELETE /api/formas-pagamento` | `financeiro.formas` |
| `GET /api/horarios/**` | `horarios.ver` |
| `PUT/POST/DELETE /api/horarios/**` | `horarios.editar` |
| `GET /api/time/membros` | `time.ver` |
| `POST /api/time/membros` | `time.convidar` |
| `PUT /api/time/membros/{id}` | `time.editar` |
| `DELETE /api/time/membros/{id}` | `time.remover` |
| `GET /api/perfis` | `perfis.ver` |
| `POST/PUT/DELETE /api/perfis` | `perfis.criar` / `.editar` / `.excluir` |
| `GET /api/assinatura/**` | `assinatura.ver` |
| `PUT /api/assinatura/assentos`, checkouts | `assinatura.alterar` |

`GET /api/perfis/catalogo`, `GET /api/bootstrap` e `GET /api/assinatura/recursos` exigem
só estar autenticado.

### Recursos exigidos por rota

Permissão é o que o admin deu ao perfil; recurso é o que a empresa comprou. Um admin pode
ter a permissão e ainda assim esbarrar no plano — aí a resposta é **402** com
`code: RecursoForaDoPlano` e a mensagem já diz a partir de qual plano o recurso entra.

| Rota | Recurso |
|---|---|
| `POST /api/time/membros` | `multi-usuario` |
| `POST/PUT/DELETE /api/perfis` | `permissoes-avancadas` |
| `PUT /api/horarios/staff/{usuarioId}` | `jornada-por-pessoa` |
| `POST/DELETE /api/horarios/staff/ausencias` | `jornada-por-pessoa` |

## Agenda: disponibilidade

```
GET /api/agendamentos/disponibilidade?data=2026-09-21&itensIds=3&itensIds=5&responsavelId=
GET /api/agendamentos/disponibilidade/periodo?de=2026-09-01&ate=2026-09-30&itensIds=3
```

Devolve, por dia: se a empresa abre, a janela, a pausa, o motivo de estar fechado, o
intervalo de encaixe, quantos atendimentos já existem e a lista de horários livres **com a
pessoa do time que atende cada um**.

O cálculo é a interseção da janela da empresa com a jornada de cada atendente, menos as
pausas dos dois, menos as ausências e menos o que já está agendado. `POST /api/agendamentos`
revalida isso antes de gravar: uma agenda desatualizada no app não cria conflito.

## Assinatura

```
GET  /api/assinatura/planos
GET  /api/assinatura/recursos
GET  /api/assinatura/atual
POST /api/assinatura/cotacao        { planoId, ciclo, assentos }
PUT  /api/assinatura/assentos       { assentos }
POST /api/assinatura/paddle/checkout
POST /api/assinatura/google-play/confirmar
```

Preços em **USD**. A cotação é a fonte da verdade:

```
total mensal = preço do plano + max(0, assentos - usuáriosIncluídos) × US$ 10
total anual  = preço do plano + max(0, assentos - usuáriosIncluídos) × US$ 120
```

### Recursos por plano

`GET /api/assinatura/planos` devolve, em cada plano, `recursos` (as chaves inclusas) e
`catalogo` (o catálogo inteiro marcado item a item). `GET /api/assinatura/recursos` faz o
mesmo para o plano atual, já agrupado, que é o formato da tela de assinatura.

Os degraus acumulam — cada plano tem tudo do anterior:

| Degrau | Plano | Recursos |
|---|---|---|
| 1 | Basic | agendamentos, clientes, catálogo, vendas, página de agendamento online, lembrete por e-mail |
| 2 | Platinum | + vários usuários no time, jornada por pessoa, política de cancelamento, cartão em arquivo, lembretes por SMS e WhatsApp, Google Calendar, agendamento recorrente, sem a marca da plataforma |
| 3 | Ultimate | + relatórios avançados, comissões, várias unidades, permissões avançadas por perfil, salas e equipamentos |
| 4 | Custom | + onboarding dedicado, API pública, gerente de conta |

A fonte é `CatalogoRecursos` no domínio; `planos.recursos` guarda as chaves e a migração
`RecursosPorPlano` traz os planos já gravados para elas.

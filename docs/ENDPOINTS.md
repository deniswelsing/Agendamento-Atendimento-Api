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
                      time, formasPagamento, horarioFuncionamento, opcoes
```

`opcoes` traz os rótulos dos enums (`tipoCliente`, `statusAgendamento`, `statusVenda`,
`cicloCobranca`, `diaDaSemana`…) para que o app não traduza nada por conta própria.

Cada lista só vem se o usuário tiver a permissão correspondente (`time.ver`,
`financeiro.ver`, `horarios.ver`).

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

`GET /api/perfis/catalogo` e `GET /api/bootstrap` exigem só estar autenticado.

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

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

## Do atendimento ao dinheiro

`POST /api/vendas` com `agendamentoId` fatura um atendimento: cria a venda e **grava a
volta** em `agendamentos.venda_id`. Sem essa volta o app não sabe que o atendimento já
virou venda e oferece faturar de novo — o mesmo serviço cobrado duas vezes. Um segundo
pedido para o mesmo atendimento é recusado com **400** e `code:
ATENDIMENTO_JA_FATURADO`.

É o caminho que o botão **Finalizar e cobrar** da agenda usa: conclui o atendimento, cria
a venda com os serviços agendados e abre o recebimento.

## Cobrança: maquininha, Pix e gateway

```
POST /api/vendas/{id}/cobrancas     { formaPagamentoId, valor, meio, chaveIdempotencia, parcelas?, adquirenteChave?, terminalSerie? }
GET  /api/vendas/{id}/cobrancas
GET  /api/cobrancas/{id}
POST /api/cobrancas/{id}/enviar     { pixCopiaECola? }
POST /api/cobrancas/{id}/concluir   { aprovada, nsu?, codigoAutorizacao?, bandeira?, ultimosDigitos?, transacaoExternaId?, valorTaxaReal?, motivoRecusa? }
POST /api/cobrancas/{id}/cancelar   { motivo? }
POST /api/pagamentos/{id}/conciliar { valorTaxaReal }
GET  /api/pagamentos/divergencias
```

`meio` é `Manual`, `TerminalPresente`, `PixQr` ou `GatewayOnline`. O desenho é o mesmo
para os três últimos: só muda quem responde o `concluir`.

**A ordem existe por um motivo.** `abrir` grava a intenção **antes** de o cliente ser
cobrado. Entre mandar cobrar e saber o resultado o app pode cair, a rede pode sumir e a
pessoa pode tocar de novo — sem um registro anterior, uma cobrança aprovada que não voltou
vira dinheiro cobrado e não lançado.

- Repetir a mesma `chaveIdempotencia` devolve **200** com a cobrança que já existe e
  `jaExistia: true`; a criação de verdade devolve **201**. O cliente nunca é cobrado duas vezes.
- Uma venda só tem **uma cobrança aberta por vez**. A segunda é recusada com 400.
- `concluir` é idempotente: a mesma resposta chegando duas vezes não duplica o lançamento.
- Uma cobrança aberta expira em 10 minutos e **nunca** vira pagamento depois disso. Se o
  dinheiro entrou mesmo assim, ele aparece na conciliação como transação sem lançamento —
  um problema visível, que é melhor que um lançamento inventado.

### Taxa estimada e taxa real

O pagamento guarda as duas. `valorTaxaEstimada` é o que a alíquota configurada previa,
congelado no lançamento; `valorTaxa` é a que vale hoje e entra no líquido. Enquanto
`taxaConferida` for `false`, **o líquido é previsão, não fato** — foi calculado pela
configuração, não pelo que a adquirente cobrou.

`concluir` com `valorTaxaReal` já grava a taxa verdadeira. Para o que foi digitado à mão,
`POST /api/pagamentos/{id}/conciliar` corrige depois, contra o extrato.
`GET /api/pagamentos/divergencias` é a fila de conferência: o que ainda não foi conferido,
mais o que veio diferente do previsto.

O pagamento também guarda `meio`, `nsu`, `bandeira`, `ultimosDigitos` e `adquirenteChave`.
`Manual` quer dizer que o sistema não viu a transação: alguém passou o cartão numa
maquininha de fora e digitou o valor.

**O que ainda não existe:** nenhum SDK de adquirente está integrado. Hoje quem chama
`concluir` é o app, com o que a maquininha respondeu. Esta é a base que serve igual para
Stone, Cielo, PagBank ou Mercado Pago — a integração com cada uma entra por cima dela.

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

| Degrau | Plano | Recursos | Total |
|---|---|---|---|
| 1 | Basic | agendamentos, clientes, catálogo, vendas, página de agendamento online, lembrete por e-mail, contratos e sinal | 7 |
| 2 | Platinum | + vários usuários no time, jornada por pessoa, política de cancelamento, cartão em arquivo, lembretes por SMS e WhatsApp, Google Calendar, agendamento recorrente, sem a marca, turmas e aulas, lista de espera, limite diário | 18 |
| 3 | Ultimate | + relatórios avançados, comissões, ponto do time, várias unidades, permissões avançadas por perfil, salas e equipamentos | 24 |
| 4 | Custom | + onboarding dedicado, API pública, gerente de conta | 27 |

Os degraus seguem a tabela do Square Appointments (Free, Plus e Premium), com o degrau 4
somando o que costuma ser acordo comercial. A colocação de cada recurso está fixada em
teste: mudar um de degrau muda o que a empresa paga.

### O que o plano promete e o que o sistema entrega

Cada recurso carrega `disponivel`. **`false` significa que o plano promete mas o sistema
ainda não implementa** — a tela de planos mostra "em breve" em vez de um visto. Hoje são
9 prontos de 27, então os planos entregam 4, 6, 7 e 9 dos 7, 18, 24 e 27 que anunciam.

Prontos: agendamentos, clientes, catálogo, vendas, vários usuários no time, jornada por
pessoa, permissões avançadas, onboarding e gerente de conta (os dois últimos são serviço
humano, não software).

Dois testes seguram isso: um fixa a lista do que está pronto, e outro garante que nenhuma
rota recusa com 402 um recurso ainda não implementado — cobrar por uma porta que não existe
seria pior que não ter a porta. Ao terminar um recurso, tire o `Disponivel: false` dele e
atualize os dois testes.

A fonte é `CatalogoRecursos` no domínio; `planos.recursos` guarda as chaves. As migrações
`RecursosPorPlano` e `RecursosRevisadosComSquare` trazem os planos já gravados para elas —
ao acrescentar um recurso, crie uma nova migração e atualize as constantes do teste de
deriva, nunca edite uma migração que já rodou.

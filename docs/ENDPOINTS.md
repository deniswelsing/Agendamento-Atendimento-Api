# Endpoints

Toda rota autenticada espera:

| Cabeçalho | Conteúdo |
|---|---|
| `Authorization` | `Bearer <accessToken>` |
| `X-Produto` | `agendamento-atendimento` ou `petshop-route` |

Datas e horas: todo `DateTimeOffset` (ex.: `inicio`, `fim`) é um instante real em UTC, e
é assim que deve ser enviado de volta (qualquer deslocamento é aceito e normalizado).
Horário de funcionamento, `horaDe`/`horaAte` e parâmetros `DateOnly` (`data`, `de`,
`ate`) são interpretados no fuso da empresa (`Tenant.FusoHorario`).

Erros voltam como `{ "message": "...", "code": "..." }`. Texto maior que a coluna ou número
fora da faixa do banco é **400** (`CAMPO_LONGO`, `VALOR_FORA_DA_FAIXA`), e não 500. Corpo
que nem vira objeto (número vazio, `"45.5"` num campo inteiro, data inválida) também volta
nesse formato — **400** `DADOS_INVALIDOS`, com `campos` dizendo quais falharam — e não no
`ProblemDetails` padrão, que não tem `message`.

| Status | Significado |
|---|---|
| 400 | regra de negócio; `code` identifica qual |
| 401 | token ausente, inválido ou expirado |
| 403 | falta a permissão exigida pela rota |
| **402** | assinatura inativa, produto não coberto ou **time sem assento livre** |
| 404 | registro não encontrado |

### Assinatura em dia, em toda rota

Toda rota autenticada confere a assinatura da empresa — não só o bootstrap. Com ela
**parada** (pendente, suspensa, cancelada, expirada, ou em período de graça que já
acabou), a resposta é **402** com `code: ASSINATURA_INATIVA`; com o produto do
`X-Produto` fora da assinatura, **402** `PRODUTO_NAO_COBERTO`. Período de graça ainda
vigente libera normalmente. Empresa sem assinatura nenhuma passa (vale o plano de entrada,
como no bootstrap).

Continuam abertas com a assinatura parada, para dar para entrar e pagar:
`/api/auth/**`, `/api/bootstrap` (que tem a checagem dela), `/api/assinatura/**`,
`/api/publico/**` (regra própria, abaixo), `/health` e `/swagger`.

A leitura da assinatura fica num cache por empresa de 30 s, esquecido na hora quando a
assinatura muda pela Api (assentos, compra confirmada, webhook). Mudança feita direto no
banco ou por outra instância leva até 30 s para valer.

### Acesso de agora, em toda rota

As permissões viajam no access token, mas quem decide é o acesso **de agora**: toda rota
autenticada (menos `/api/auth/**` e `/api/publico/**`) confere se o usuário continua ativo
e troca as permissões do token pelas do perfil atual. Rebaixar alguém, mudar as
permissões de um perfil, desativar ou remover vale na próxima requisição da pessoa — e não
quando o token dela vencer. Usuário desativado ou removido responde **401**
`ACESSO_REVOGADO` (e o refresh também é recusado).

O mesmo desenho da assinatura: cache de 30 s por usuário, esquecido na hora quando o time
ou os perfis mudam pela Api.

## Autenticação

```
POST /api/auth/login             { login, senha, tenantSlug?, produto? }  -> LoginResponse
POST /api/auth/refresh           { refreshToken, produto? }               -> LoginResponse
POST /api/auth/sessao-irma       { refreshToken, produto? }               -> LoginResponse
GET  /api/auth/convite/{token}                                            -> { nome, email, empresa, expiraEm }
POST /api/auth/convite/aceitar   { token, senha, produto? }               -> LoginResponse
POST /api/auth/logout
```

`LoginResponse.usuario` já traz `permissoes` e `telasVisiveis` — é o que o app usa para
montar a navegação.

### Sessão do app irmão (Android ↔ PetShop.Route)

`POST /api/auth/refresh` **roda** o token: o usado é revogado e sai outro. Com os dois apps
dividindo o mesmo refresh token (o ContentProvider do Android), quem renovava primeiro
derrubava o outro.

`POST /api/auth/sessao-irma` resolve isso: recebe o refresh token compartilhado, **só
confere** (não revoga nem roda) e devolve um par **novo e independente** — access token e
refresh token próprios — para o produto que pediu (`produto` no corpo, senão o cabeçalho
`X-Produto`, senão `agendamento-atendimento`; o mesmo critério do login). O app que
recebeu a sessão chama isto **uma vez**, guarda o par dele e dali em diante renova só o
seu com `/refresh`. Token revogado, vencido ou desconhecido: **401** `REFRESH_INVALIDO`.

A rotação é atômica: dois `/refresh` ao mesmo tempo com o mesmo token (duas abas, um retry)
não saem os dois com par novo — só o primeiro gira; o outro recebe **401**
`REFRESH_INVALIDO`. Quem divide o token entre abas deve adotar o par que a outra já gravou
(é o que o painel web faz) em vez de renovar de novo.

O slug da empresa no login não diferencia maiúsculas.

### Convite

`POST /api/time/membros` cria o membro com o convite pendente e devolve, **só nessa
resposta**, `urlConvite` (`{Web:BaseUrl | PaginaPublica:BaseUrl | host da requisição}/convite/{token}`)
e `conviteExpiraEm` (7 dias). Não há envio de e-mail: quem convidou copia o link e o
entrega à pessoa. O banco guarda só o SHA-256 do token — perdeu o link, gere outro:

```
POST /api/time/membros/{id}/convite   -> MembroTimeDto com urlConvite novo (time.convidar)
```

Renovar invalida o link anterior e recomeça a validade. Só vale para convite pendente de
alguém ativo (`CONVITE_NAO_PENDENTE`); o de um administrador só um administrador renova
(`SO_ADMINISTRADOR`).

A tela `/convite/{token}` do painel usa as duas rotas anônimas:

- `GET /api/auth/convite/{token}` mostra quem foi convidado e para qual empresa.
- `POST /api/auth/convite/aceitar` grava a senha (mínimo **8** caracteres, senão **400**
  `SENHA_FRACA`), gasta o token, tira o convite de pendente e já entra — a resposta é a do
  login.

Token desconhecido, vencido, já usado (uso único) ou de usuário desativado: **404**
`CONVITE_INVALIDO`. O token é comparado exato (maiúsculas contam).

## Bootstrap

```
GET /api/bootstrap -> usuario, tenant, assinatura, catalogoPermissoes,
                      catalogoRecursos, recursosLiberados,
                      time, formasPagamento, horarioFuncionamento, opcoes
```

`opcoes` traz os rótulos dos enums (`tipoCliente`, `statusAgendamento`, `statusVenda`,
`statusPagamento`, `meioDeCaptura`, `statusCobranca`, `cicloCobranca`, `diaDaSemana`…) para
que o app não traduza nada por conta própria.

Cada lista só vem se o usuário tiver a permissão correspondente (`time.ver`,
`financeiro.ver`, `horarios.ver`). `vendedores` (`{ usuarioId, nome }` de quem está ativo no
time) vai para quem tem `vendas.criar` ou `vendas.editar`: é o seletor de quem leva a
comissão, e sem ele a recepção e o financeiro viam só "Sem vendedor".

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
| `POST /api/vendas/{id}/pagamentos/{pagamentoId}/estorno` | `financeiro.estornar` |
| `POST /api/vendas/{id}/finalizar` | `vendas.finalizar` |
| `DELETE /api/vendas/{id}` | `vendas.cancelar` |
| `GET /api/formas-pagamento` | `financeiro.ver` |
| `POST/PUT/DELETE /api/formas-pagamento` | `financeiro.formas` |
| `GET /api/horarios/**` | `horarios.ver` |
| `PUT/POST/DELETE /api/horarios/**` | `horarios.editar` |
| `GET /api/pagina-online/**` | `pagina-online.ver` |
| `PUT /api/pagina-online`, `PUT .../servicos/{id}` | `pagina-online.editar` |
| `POST /api/pagina-online/pendentes/{id}/**` | `pagina-online.aprovar` |
| `GET /api/time/membros` | `time.ver` |
| `POST /api/time/membros`, `POST /api/time/membros/{id}/convite` | `time.convidar` |
| `PUT /api/time/membros/{id}` | `time.editar` |
| `DELETE /api/time/membros/{id}` | `time.remover` |
| `GET /api/perfis` | `perfis.ver` |
| `POST/PUT/DELETE /api/perfis` | `perfis.criar` / `.editar` / `.excluir` |
| `GET /api/assinatura/**` | `assinatura.ver` |
| `PUT /api/assinatura/assentos`, checkouts | `assinatura.alterar` |

`GET /api/perfis/catalogo`, `GET /api/bootstrap` e `GET /api/assinatura/recursos` exigem
só estar autenticado. `GET/POST/DELETE /api/publico/{slug}/**` é a família de rotas que
responde **sem token** — quem a protege é o slug, o plano do tenant e as regras da
página, não a autenticação. Também são anônimas as de entrar (`/api/auth/login`,
`/refresh`, `/sessao-irma`, `/convite/**`) e o webhook do Paddle
(`POST /api/assinatura/paddle/webhook`), protegido pela assinatura HMAC.

### Recursos exigidos por rota

Permissão é o que o admin deu ao perfil; recurso é o que a empresa comprou. Um admin pode
ter a permissão e ainda assim esbarrar no plano — aí a resposta é **402** com
`code: RecursoForaDoPlano` e a mensagem já diz a partir de qual plano o recurso entra.

| Rota | Recurso |
|---|---|
| `POST /api/time/membros` | `multi-usuario` |
| `POST/PUT/DELETE /api/perfis` | `permissoes-avancadas` |
| `PUT /api/pagina-online` | `pagina-online` |
| `PUT /api/horarios/staff/{usuarioId}` | `jornada-por-pessoa` |
| `POST/DELETE /api/horarios/staff/ausencias` | `jornada-por-pessoa` |

## Do atendimento ao dinheiro

`POST /api/vendas` com `agendamentoId` fatura um atendimento: cria a venda e **grava a
volta** em `agendamentos.venda_id`. Sem essa volta o app não sabe que o atendimento já
virou venda e oferece faturar de novo — o mesmo serviço cobrado duas vezes. Um segundo
pedido para o mesmo atendimento é recusado com **400** e `code:
ATENDIMENTO_JA_FATURADO`.

É o caminho que o botão **Finalizar e cobrar** da agenda usa: conclui o atendimento, cria
a venda com os serviços agendados e abre o recebimento. Só atendimento **em andamento ou
concluído**, com serviço, vira venda (senão **400** `ATENDIMENTO_NAO_ENTREGUE`), e o
faturamento passa pela trava da agenda: dois pedidos ao mesmo tempo para o mesmo
atendimento criam uma venda só.

### Fechar a venda

A venda nasce **aberta**: os itens ainda mudam (`PUT /api/vendas/{id}`). Ela **fecha** no
primeiro destes, o que vier antes:

- `POST /api/vendas/{id}/finalizar`;
- o primeiro recebimento (`POST /api/vendas/{id}/pagamentos`);
- a primeira cobrança (`POST /api/vendas/{id}/cobrancas`) — antes de o cliente ser cobrado.

Fechar baixa o estoque dos produtos, uma vez só; sem estoque suficiente, **400** `ESTOQUE`.
Fechada, a venda não muda mais: `PUT` responde **400** `VENDA_FECHADA`. Antes, receber
com a venda aberta só trocava o status — a venda saía de "aberta" sem passar pelo
fechamento e o estoque nunca baixava.

Receber, cobrar, fechar, editar e cancelar a mesma venda passam por uma trava da venda: dois
cliques ou duas abas não recebem o mesmo saldo duas vezes.

Recebimento manual (`/pagamentos`): o valor é arredondado a centavos antes da conferência
(R$ 0,004 é **400** `VALOR_INVALIDO`); a forma precisa estar ativa (`FORMA_INATIVA`) e as
parcelas ficam entre 1 e o máximo dela — 1 quando ela não parcela (`PARCELAS_INVALIDAS`);
com uma cobrança em andamento na venda, **400** `VENDA_COM_COBRANCA` — o cliente pagaria
duas vezes quando a cobrança fosse aprovada.

Itens: quantidade, preço e desconto são arredondados a centavos; a quantidade de uma linha
vai de 0,01 a 100.000 (`QUANTIDADE`); o desconto de uma linha não passa do valor dela, nem
o desconto geral do valor dos itens (`DESCONTO_INVALIDO`). Venda cancelada ou estornada tem
`saldoAberto` zero.

Prender um atendimento a uma venda já existente (`PUT` com outro `agendamentoId`) é faturá-lo:
as mesmas regras e a mesma trava do `POST` — só atendimento em andamento ou concluído, com
serviço (`ATENDIMENTO_NAO_ENTREGUE`), e nunca um que já tem venda.

### Comissão

`VendaItem` guarda `comissaoPercentual` **congelado no momento da venda**, copiado do item
do catálogo. Congelar importa: mexer na comissão do catálogo amanhã não pode mudar o que
já foi vendido e prometido a quem atendeu.

`comissaoValor` sai do **líquido** do item, então desconto dado reduz a comissão de quem
deu. `Venda.totalComissao` soma os itens, e é **zero sem vendedor** — comissão sem alguém
para receber é número solto.

`Venda.vendedorId` é quem leva. Numa venda que nasce de atendimento ele já vem preenchido
com o responsável do agendamento; `POST /api/vendas` aceita `vendedorId` para mandar outro.

Em cada item, `vendedorId` é só o vendedor **próprio** da linha — nulo quando ela herda o da
venda — e `vendedorNome` é o de quem leva de fato. Reenvie o `vendedorId` do item como veio:
trocar o vendedor da venda muda a comissão das linhas que herdam.

As vendas anteriores a esta mudança ficaram com comissão zero e sem vendedor, de
propósito: copiar o percentual atual do catálogo inventaria uma comissão que ninguém
acordou na época.

### Estorno e cancelamento

```
POST /api/vendas/{vendaId}/pagamentos/{pagamentoId}/estorno   { motivo? }  -> VendaDto
```

Cancelar (`DELETE /api/vendas/{id}`) continua exigindo que nenhum recebimento esteja
confirmado (`VENDA_COM_PAGAMENTO`) — e agora há como chegar lá. O estorno marca o
pagamento como `Estornado` (ele continua na lista, com `estornado: true`, `estornadoEm` e
`motivoEstorno`), refaz `totalPago` e `saldoAberto`, e a venda **paga** volta a
`AguardandoPagamento` (a aberta continua aberta). Estornado todo, a venda pode ser
cancelada.

Recusas: estornar de novo o mesmo recebimento é **400** `PAGAMENTO_JA_ESTORNADO`; um
recebimento que não está confirmado, **400** `PAGAMENTO_NAO_CONFIRMADO`; motivo com mais
de 500 caracteres, **400** `MOTIVO_LONGO`; pagamento de outra venda, **404**.

O que entrou por cobrança (maquininha, Pix, gateway) se estorna igual, **só no registro**:
não há integração de estorno com adquirente ou PSP — a devolução do dinheiro é feita lá.

Cancelar uma venda **finalizada** devolve ao estoque o que a finalização baixou. Vendas
finalizadas antes desta versão não sabem se baixaram (a coluna `estoque_baixado` nasceu
`false`) e não devolvem nada — melhor que devolver estoque que nunca saiu. Cancelar de novo
uma venda cancelada responde 204 sem mexer em nada.

### Painel

`GET /api/dashboard/resumo` conta o faturamento pelo **dinheiro que entrou**: os
recebimentos confirmados no dia (e no mês) menos o que foi estornado no mesmo período. O
ticket médio é o faturamento do mês dividido pelas vendas que receberam no mês, e "vendas
em aberto" são todas as que ainda têm saldo a receber, de qualquer mês. Antes o painel
contava a venda pela data em que foi criada e só quando já estava quitada: um recebimento
parcial não aparecia, e a venda de ontem paga hoje entrava no dia de ontem. Sem `data`, o
dia é o de hoje no fuso da empresa.

### Cadastros com história

`DELETE /api/clientes/{id}` é para cadastro feito por engano: cliente que já tem venda,
atendimento, pacote ou lugar na fila de espera responde **400** `CLIENTE_COM_HISTORICO` —
desative-o (`isAtivo: false`). Excluído, as vendas dele sumiam das listas (inclusive uma
aberta, com saldo a receber) e continuavam contando no painel.

`DELETE /api/formas-pagamento/{id}` responde **204** quando excluiu e **200** com a forma
(`ativa: false`) quando ela já tinha recebimentos e por isso só foi desativada.

### Catálogo

`POST`/`PUT /api/catalogo/itens` recusam o que antes era gravado sem pergunta: comissão
fora de 0–100% (`COMISSAO`), taxa fora de 0–100% (`TAXA`), custo negativo (`CUSTO`),
estoque negativo (`ESTOQUE`) e texto maior que o campo (`CAMPO_LONGO`: nome 200, descrição
1000, categoria 120, código de barras 60, endereço da imagem 500). Preço, custo, comissão e
taxa são guardados com duas casas — `10.555` vira `10.56`, e não um valor que a tela mostra
de um jeito e a venda cobra de outro. A listagem desempata pelo id: com nomes iguais, cada
página ordenava do seu jeito e um item podia aparecer em duas páginas (e sumir de outra).

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
  um problema visível, que é melhor que um lançamento inventado. A que passou do prazo já
  sai como `Expirada` (e `estaAberta: false`) nas leituras, e `cancelar` nela só registra a
  expiração: ela não prende mais o caixa.
- Abrir, enviar, concluir e cancelar passam pela trava da venda: a mesma resposta do
  terminal chegando duas vezes ao mesmo tempo lança um pagamento só.
- `valorTaxaReal` (no `concluir` ou no `conciliar`) fica entre 0 e o valor recebido; fora
  disso, **400** `TAXA_INVALIDA`.

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

`responsaveisPorItem` pede o encaixe para pessoas escolhidas, uma por serviço, casando
com `itensIds` **por posição** — a mesma forma do `POST`. Na query string **zero é "quem
estiver livre"**: a posição precisa sobreviver, e uma lista de query não carrega nulo.

```
GET /api/agendamentos/disponibilidade?data=2026-09-21&itensIds=4&itensIds=3
    &responsaveisPorItem=7&responsaveisPorItem=9
```

lê-se "a pessoa 7 faz o serviço 4 e, em seguida, a 9 faz o 3" — e a resposta só traz os
horários em que **as duas** cabem.

`etapasPorItem` diz o que acontece **ao mesmo tempo**. É a etapa de cada serviço, também
por posição: etapas diferentes são um depois do outro, e serviços na MESMA etapa começam
juntos, cada um com a sua pessoa.

```
GET /api/agendamentos/disponibilidade?data=2026-09-21&itensIds=4&itensIds=3
    &etapasPorItem=0&etapasPorItem=0
```

O atendimento passa a durar o serviço **mais longo** da etapa, e não a soma: treinamento
de 60 com revisão de contrato de 30, ao mesmo tempo, é uma hora — e prende duas pessoas
nessa hora. A mesma pessoa nos dois é recusada (`SIMULTANEOS_MESMA_PESSOA`): ela estaria
em dois lugares na mesma hora. Sem a lista, cada serviço é a sua própria etapa — o
atendimento em sequência, que é o de sempre.

No banco, quem guarda isso é `Ordem` do item, que passou a ser a etapa. Atendimento
sequencial continua sendo ordens distintas, que é como tudo o que já existe foi gravado.

Devolve, por dia: se a empresa abre, a janela, a pausa, o motivo de estar fechado, o
intervalo de encaixe, quantos atendimentos já existem e a lista de horários livres **com a
pessoa do time que atende cada um**.

`horaDe` e `horaAte` são a faixa que o cliente pediu, e o atendimento **inteiro** tem de
caber nela: quem pede "entre 14h e 18h" não quer um encaixe que termina 19h45. Um lado só
também vale. Quando a faixa é o que esvaziou o dia, o motivo diz isso — culpar a agenda
mandaria procurar outro dia quando bastava abrir a faixa.

`incluirSugestoes=true` traz os dias próximos mesmo quando este dia tem horário: é o
botão "ver outros dias". O dia ter encaixe não quer dizer que o encaixe sirva ao cliente.

`clienteId` diz para quem é o atendimento: o horário em que esse cliente (pessoa) já tem
atendimento sai da grade e das sugestões — o `POST` o recusaria com `CLIENTE_JA_AGENDADO`.
Sem ele, a grade é a do time, como sempre.

Dia sem encaixe devolve `sugestoes`: os próximos dias que têm, com os primeiros horários
de cada um. É lista, e não um "próximo dia" solto, porque quem está com o cliente no
telefone precisa de duas ou três opções na mesma resposta — e apontar um dia só obriga a
tela a perguntar de novo para mostrar o seguinte. As sugestões respeitam tudo o que foi
pedido: serviços, quem presta, etapas e a faixa.

O cálculo é a interseção da janela da empresa com a jornada de cada atendente, menos as
pausas dos dois, menos as ausências, menos o que já está agendado — e só entre quem presta
o serviço pedido. `POST /api/agendamentos` revalida isso antes de gravar: uma agenda
desatualizada no app não cria conflito.

**Pedidos ao mesmo tempo.** A revalidação e a gravação acontecem sob uma trava da agenda
da empresa (`pg_advisory_xact_lock`, uma por tenant, dentro de uma transação): dois pedidos
simultâneos para o mesmo horário não passam os dois pela conferência — o segundo espera o
primeiro gravar e aí recebe `HORARIO_INDISPONIVEL` (ou `RESPONSAVEL_INDISPONIVEL`). Vale
para todo caminho que grava ou move um agendamento: `POST` e `PUT /api/agendamentos`,
`PATCH .../itens/{itemId}/responsavel`, `POST /api/publico/{slug}/agendamentos`, marcar
pelo pacote e aprovar/recusar pedido da página. Medido com 10 `POST` paralelos no mesmo
horário e pessoa: antes entravam 4–5 (10 de 10 pela página pública); agora entra 1.

### Horários, exceções e escala

- Exceção que **abre** o dia com horário próprio traz a pausa dela; sem pausa na exceção, o
  dia não tem pausa. Antes o "horário especial das 12 às 16" herdava a pausa das 12 às 13
  do dia da semana e só abria às 13.
- Dia que a empresa só abre por exceção (um domingo de mutirão): ninguém tem jornada nesse
  dia da semana — nem pode ter, a Api recusa com `EMPRESA_FECHADA` —, então o expediente da
  exceção vale para quem não tem jornada. Quem não vem é marcado como ausência. Antes o dia
  aberto não tinha horário nenhum.
- `PUT /api/horarios/staff/{usuarioId}`: turno desativado não entra em escala nova
  (`TURNO_INVALIDO`), mas o dia que **já** estava nele continua salvando — excluir um turno
  em uso só o desativa, e a semana inteira da pessoa ficava travada por causa de um dia que
  ninguém mexeu. A pausa da jornada livre precisa ter início e fim, nessa ordem, dentro da
  jornada (`PAUSA_INVALIDA`).

### Marcar, remarcar e cancelar

- `responsavelId` pede **uma pessoa para tudo**. Com `responsaveisPorItem` preenchido ele é
  ignorado e vale a escolha por serviço — o painel mandava a pessoa da primeira linha junto,
  e um atendimento com um serviço da Bruna e outro do Caio era sempre recusado.
- O mesmo cliente (pessoa) não fica em dois atendimentos no mesmo horário, nem em duas vagas
  da mesma turma: **400** `CLIENTE_JA_AGENDADO`. Empresa pode — ela manda gente diferente.
  A regra vale em toda porta que marca: a agenda, a sessão de pacote e a página pública.
- Remarcar (`PUT`) move, não reprecifica: o mesmo serviço mantém o preço e o nome que já
  tinha. Mudar o horário de um atendimento confirmado (ou de quem faltou) o devolve a
  `Agendado` — a confirmação era do horário antigo. Sessão de pacote continua pré-paga
  (preço zero), com os serviços do pacote (`SESSAO_DE_PACOTE`) e dentro do ciclo dela
  (`FORA_DO_CICLO`).
- Atendimento que já virou venda (não cancelada) não se remarca nem se cancela: **400**
  `ATENDIMENTO_FATURADO` — cancele a venda antes. Vale para o `PUT`, o `DELETE` e o `PATCH`
  de status para `Cancelado`, conferido sob a trava da agenda (a mesma do faturamento).
- Aprovar um pedido da página pelo `PATCH` de status tem o efeito do "Aprovar" da página
  online: programa os lembretes e tira o cliente da lista de espera. Enquanto o pedido está
  pendente, a espera continua valendo — recusado, o cliente segue na fila.
- `DELETE` segue as mesmas transições do `PATCH` de status: quem já faltou não é cancelado
  (`TRANSICAO_INVALIDA`).
- Trocar quem presta um serviço (`PATCH .../itens/{itemId}/responsavel`) com `null` devolve
  o serviço para quem responde pelo atendimento — e essa pessoa também precisa estar livre
  na janela dele. Serviços da mesma etapa não ficam com a mesma pessoa, nem na troca nem
  na lista de candidatos.
- `GET /api/agendamentos?responsavelId=` traz o atendimento em que a pessoa presta
  **qualquer** serviço, como a visibilidade "só o seu".
- Marcar (pela agenda, pelo pacote ou pela página) tira o cliente da lista de espera
  daquele serviço — a entrada vira `Convertido`. A varredura diária expira as esperas cuja
  data passou.
- Observações (1000), local (250) e motivo do cancelamento (500) maiores que a coluna
  voltam **400** `CAMPO_LONGO` antes de gravar.
- `POST /api/lista-de-espera` com "com quem" (`responsavelId`) exige alguém que atende
  (`NAO_ATENDENTE`) e presta o serviço pedido (`NAO_PRESTA`) — a fila aceitava o
  financeiro, e a espera nunca casava com vaga nenhuma.

## Página de agendamento online

O cliente marca sozinho, num endereço público, sem conta e sem token.

```
GET    /api/pagina-online                       -> configuração + url para compartilhar
PUT    /api/pagina-online                       { ativa, slug, ... }
GET    /api/pagina-online/slug-disponivel?slug=
GET    /api/pagina-online/pendentes             -> AgendamentoDto[]
POST   /api/pagina-online/pendentes/{id}/aprovar
POST   /api/pagina-online/pendentes/{id}/recusar
PUT    /api/pagina-online/servicos/{itemId}?visivel=
```

A porta aberta, sem `Authorization`:

```
GET    /api/publico/{slug}                          -> serviços, profissionais e a janela
GET    /api/publico/{slug}/disponibilidade?data=&itensIds=
POST   /api/publico/{slug}/agendamentos             -> { codigo, status, ... }
GET    /api/publico/{slug}/agendamentos/{codigo}
DELETE /api/publico/{slug}/agendamentos/{codigo}?motivo=
```

O tenant sai do **slug**, nunca do chamador: enquanto a página não é encontrada, nenhuma
consulta enxerga dado de ninguém. Página desligada, inexistente ou fora do plano respondem
**404 igual**, de propósito — quem desliga não quer que o endereço antigo continue
confirmando que a empresa existe. O plano que vale é o da empresa dona da página.

Serviço só aparece com `visivelOnline`; adivinhar o id não ajuda, porque o agendamento
recusa o mesmo conjunto. A disponibilidade pública é a mesma do app, **inclusive quem
presta o serviço**, menos o que a antecedência mínima já comeu.

Não existe "meus agendamentos": o cliente recebe um código de 10 caracteres e é com ele —
e só com ele — que consulta e desmarca.

Com `exigeAprovacao`, o pedido nasce `PendenteAprovacao` e **já segura o horário**. Soltá-lo
deixaria dois clientes pedirem o mesmo encaixe. Recusar é o que devolve o horário.

Recusas: `AntecedenciaInsuficiente`, `ForaDaJanela`, `ServicoIndisponivel`,
`HorarioIndisponivel`, `DadosIncompletos`, `ClienteJaAgendado` (400) e `LimiteDiario`
(**429**). `ClienteJaAgendado` é o cliente do e-mail que já tem atendimento nesse horário,
com qualquer pessoa: a página o punha em dois lugares ao mesmo tempo. E-mail e
telefone com formato inválido são `EMAIL_INVALIDO` / `TELEFONE_INVALIDO`; nome acima de 150
caracteres, observação acima de 1000 e motivo de desmarcar acima de 500, `CAMPO_LONGO`.

O combo de serviços é marcado com **cada serviço na pessoa que o presta**, como na grade:
uma pessoa só para tudo recusava o combo que a própria página oferecia. Sem "o cliente
escolhe o profissional", os horários não levam quem atende (`responsavelId`,
`responsavelNome` e os candidatos saem da resposta) e `totalAgendamentos` vai zerado — o
time e o movimento da empresa são dado interno.

O cadastro não muda pela porta aberta: o cliente é achado pelo e-mail (sem diferenciar
maiúsculas) e só ganha o telefone se não tinha nenhum. Um telefone diferente fica anotado
no agendamento — antes, qualquer um que soubesse o e-mail de um cliente trocava o celular
dele.

Marcar pela página programa os lembretes como marcar pela agenda — o pedido pendente só
quando for aprovado — e desmarcar cancela os que estavam na fila.

**Assinatura da empresa parada.** `GET /api/publico/{slug}`, `/disponibilidade` e
`POST .../agendamentos` respondem **503** `PAGINA_INDISPONIVEL` ("a agenda online desta
empresa está temporariamente indisponível"). É 503, e não 402, de propósito: quem abre a
página é o cliente da empresa, e não é ele quem tem de pagar. Consultar, confirmar e
desmarcar pelo código continuam funcionando — quem já marcou não perde o acesso ao que
marcou. Período de graça vigente mantém a página aberta.

### Quem presta cada serviço

```
GET /api/catalogo/itens/{id}/executores  -> { itemId, nome, executores[], abertoATodos }
PUT /api/catalogo/itens/{id}/executores  { usuariosIds: [2, 3] }
GET /api/catalogo/executores?itensIds=1&itensIds=2  -> [ { itemId, nome, executores[], abertoATodos } ]
```

O `GET` em lote responde por vários serviços de uma vez, e sem `itensIds` por todos os
ativos. É o que a tela de novo agendamento usa para oferecer, ao lado de cada serviço
marcado, só quem sabe prestá-lo — e para marcar de uma vez tudo o que uma pessoa presta.
Perguntar item a item seria uma requisição por linha do catálogo. Id que não existe
simplesmente não volta: a resposta vira opção de tela, e um erro por causa de um item
apagado enquanto ela estava aberta não ajudaria ninguém.

Um serviço **sem executores cadastrados é aberto a qualquer atendente** — é o padrão, e é
o que mantém agendável tudo que existia antes desta regra. Assim que alguém é marcado, a
lista fecha: a agenda deixa de oferecer encaixe com quem não sabe fazer aquilo. Mandar
lista vazia reabre para o time inteiro.

Só quem tem `atendente` recebe serviço; aceitar outro seria prometer um encaixe que a
agenda nunca vai oferecer.

A disponibilidade filtra por `itensIds`, serviço a serviço: cada atribuição do encaixe
traz os candidatos **daquele** serviço. Os serviços são sequenciais, então **pessoas
diferentes podem pegar serviços diferentes do mesmo atendimento** — a coloração com quem
faz coloração, a massagem em seguida com quem faz massagem. Quem já está no atendimento
continua nele quando pode; trocar só acontece quando não dá para continuar.

Um serviço sem ninguém livre derruba o encaixe inteiro: um atendimento pela metade não é
um horário que se possa oferecer. E `responsavelId` na consulta é o pedido de que **uma
pessoa só** faça tudo — é assim que a tela procura encaixe para quem quer um nome fixo.

Quando a tela já escolheu quem faz o quê, a escolha vai na pergunta, em
`responsaveisPorItem`, e não num recorte da resposta. É o que mantém a contagem do dia, o
motivo de não haver encaixe e o `proxima` falando da mesma coisa que a lista mostra: com
as escolhas de fora, o dia vazio culpava o time (`"Ninguém que presta esse serviço está
livre neste dia"`) quando o que faltava era a agenda de uma pessoa, e o `proxima`
apontava um dia que a tela abriria vazio. Escolha impossível é dita pelo nome — `"Bruno
não presta Manicure."`

`/periodo` aplica o mesmo filtro que `/disponibilidade`, desde que receba os mesmos
`itensIds`: sem eles a semana contaria encaixes com quem não presta o serviço, e o dia
mostraria menos do que a semana prometeu.

Estar apto não basta: quem sabe fazer mas já tem compromisso naquele horário continua fora
da lista, como sempre esteve.

## Pacotes

- Preço negativo é recusado também no pacote montado na hora (`PRECO_INVALIDO`) — ele virava
  estorno negativo. Um ciclo vende no máximo 1000 atendimentos (`PACOTE_GRANDE_DEMAIS`).
- O estorno de quem sai do pacote é calculado sobre o valor exato da sessão e arredondado só
  no total: R$ 100 em 3 sessões devolve R$ 100,00 das três e R$ 66,67 de duas (antes, R$ 99,99
  e R$ 66,66 — o centavo sempre contra o cliente). `valorPorAtendimento` continua o número
  com duas casas que a tela mostra.
- Na lista de clientes do pacote, o ciclo aberto mostra em `quantidadeUsada` o que já foi
  **atendido** (concluído) e em `disponivel` o que falta — o campo gravado só é preenchido
  no fechamento, e a tela passava o ciclo inteiro em "0/4". Quem saiu do pacote aparece com
  o último ciclo, que é onde está o estorno dele.
- `POST /api/pacotes/varredura?data=` não roda para uma data futura (`DATA_FUTURA`): varrer
  encerra ciclos e gera estorno de verdade. O aviso de renovação continua saindo uma vez por
  ciclo, mas a resposta traz também `jaAvisados` — os pacotes que vencem nos próximos dias
  e já foram avisados (pela rotina da madrugada, por exemplo). Antes a rotina consumia o
  aviso e a tela, rodando depois, dizia que nada vencia.
- Serviço excluído do catálogo depois de entrar no pacote sai das sessões marcadas daí em
  diante (antes, marcar dava 500); sem nenhum serviço restante, **400** `SERVICO_INVALIDO`.
- A sessão não põe o cliente em dois lugares: com outro atendimento dele no mesmo horário,
  **400** `CLIENTE_JA_AGENDADO`, como na agenda. As propostas já pulam esses horários.

## Lembretes e rotinas

Marcar, remarcar e aprovar programam os avisos do atendimento (o "está marcado" e o
lembrete N horas antes). Uma rotina de minuto em minuto, por empresa, **despacha** o que
venceu — antes só saía quem apertasse "Despachar a fila agora" (`POST
/api/lembretes/despachar`, que continua existindo para testar) — e expira as cobranças que
passaram do prazo. Com os lembretes desligados, a rotina não manda nada.

O despacho é um por empresa de cada vez (uma trava por tenant): a rotina e o "Despachar a
fila agora" ao mesmo tempo não mandam o mesmo lembrete duas vezes. A cobrança vencida é
expirada sob a trava da venda — uma conclusão que chegou antes do prazo não é sobrescrita.

O link de confirmação do aviso é absoluto e abre a página pública com o código:
`{PaginaPublica:BaseUrl}/p/{slug}?codigo={CODIGO}&confirmar=1` (ou `Web:BaseUrl`). Sem
nenhum dos dois configurado, ou com a página pública desligada, o aviso sai sem link — um
endereço relativo num e-mail não abre nada, e a página desligada responderia "não
encontrada".

## Assinatura

```
GET  /api/assinatura/planos
GET  /api/assinatura/recursos
GET  /api/assinatura/atual
POST /api/assinatura/cotacao        { planoId, ciclo, assentos }
PUT  /api/assinatura/assentos       { assentos }              -> só REDUZ
POST /api/assinatura/paddle/checkout      { planoId, ciclo, assentos, returnUrl }
POST /api/assinatura/paddle/webhook       (Paddle → Api, anônimo, assinado)
POST /api/assinatura/google-play/confirmar
```

### Assentos: reduzir é direto, aumentar é compra

`PUT /api/assinatura/assentos` **só reduz**. Pedir mais do que o contratado responde
**402** `ASSENTOS_EXIGEM_PAGAMENTO`, com a mensagem mandando comprar pelo checkout — antes,
qualquer um com `assinatura.alterar` subia os assentos até o teto do plano sem pagar.
Reduzir nunca fica abaixo dos assentos em uso nem dos inclusos no plano: pedir menos
reduz até esse piso (o mesmo "ajusta ao mínimo" que a rota sempre fez), e o corpo da
resposta traz o número que ficou.

Assento só aumenta pelos caminhos de pagamento:

- **Paddle**: `POST /api/assinatura/paddle/checkout` já leva `assentos` (o total
  desejado). A transação criada leva no `custom_data` `tenant_id`, `plano_id`, `ciclo` e
  `assentos`, e é o webhook que aplica. O webhook confere `Paddle-Signature`
  (`ts=...;h1=...`, HMAC-SHA256 de `"{ts}:{corpo}"` com `Paddle:WebhookSecret`, até 5 min
  de diferença — sem segredo configurado, recusa tudo com **401** `WEBHOOK_INVALIDO`), é
  idempotente pelo `event_id` e aplica plano, ciclo, assentos e status `Ativa` em
  `transaction.completed`/`transaction.paid` e em `subscription.created|activated|updated`
  com status `active`. Os outros eventos ficam gravados em `eventos_gateway`, sem
  processamento automático por enquanto.
- **Google Play**: `POST /api/assinatura/google-play/confirmar` com `tipoCompra: "ASSENTO"`
  contrata os inclusos do plano mais a `quantidade` comprada.

Nos dois, passar do limite do plano é recusado sem gravar nada — e a cotação
(`POST /api/assinatura/cotacao`) recusa do mesmo jeito (`ACIMA_DO_LIMITE`), em vez de mostrar
um preço que o checkout depois não aceita. A criação da transação no
Paddle e a validação no Google Play continuam por implementar nesta instalação
(`PADDLE_NAO_IMPLEMENTADO`, `PLAY_NAO_IMPLEMENTADO`).

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

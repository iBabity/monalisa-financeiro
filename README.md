# Monalisa Financeiro

Versão atual: **1.1.38**.

A conciliação OFX prepara automaticamente vínculos com baixas existentes (inclusive data próxima e nomes equivalentes), parcelas em aberto e novos lançamentos. Um PIX pode aproveitar várias parcelas já pagas e registrar somente a diferença, sem presumir a finalidade dessa diferença. Valores ambíguos exigem escolha ou confirmação explícita de que é outro pagamento. Aplicações e resgates identificados como COFRINHOS são movimentos de transferência, sem receita/despesa, e não criam títulos. FITIDs e rateios impedem reaproveitar a mesma baixa ou repetir movimentações em arquivos sobrepostos. Estornos parciais de rateio reabrem somente o valor correspondente e permitem reaproveitar movimentos preservados. O saldo do banco vem marcado para conciliação quando há movimentos novos e data válida; reimportar um arquivo todo conciliado não remarca esse ajuste. Antes de gravar, o usuário confere e confirma o extrato. Contas a receber liquidadas usam o status RECEBIDO.

O botão **CONCILIAR EXTRATO OFX** fica centralizado nas telas de contas a pagar e a receber. O botão de Open Finance foi retirado dessas telas.

O modo pessoal pode exigir login diário com `MONALISA_REQUIRE_DAILY_LOGIN=1` (arquivo local `daily-login.txt` com valor `1`). O acesso automático fica bloqueado. Se só houver o usuário automático LOCAL, o primeiro acesso permite cadastrar o responsável sem modificar o banco financeiro. A senha numérica mantém a proteção PBKDF2 e o bloqueio de tentativas do sistema. O navegador guarda somente o token de sessão; o servidor o valida e o expira à meia-noite, sem renovação por atividade. Sair revoga a sessão; reiniciar o serviço exige novo login. O selo de demonstração foi retirado da interface.

O acesso pessoal por endereço fixo usa uma compilação web com `WEB_API_URL=http://127.0.0.1:5187`. Somente a interface fica hospedada no Site privado: o banco e a API continuam neste computador. O serviço aceita o endereço HTTPS exato definido em `MONALISA_WEB_ORIGIN` apenas por conexões de loopback; outras origens continuam bloqueadas. Na instalação local, a configuração fica em `trusted-web-origin.txt`. O navegador pode solicitar permissão de acesso à rede local. Este modo não permite acessar os dados deste computador pelo celular ou por outra máquina.

Ao estornar uma baixa, escolha se o saldo da conta deve ser atualizado. **NÃO — DESFAZER A BAIXA E MANTER O SALDO** reabre a parcela e preserva o movimento bancário, registrando a escolha e o motivo no histórico. Para baixas conciliadas por OFX, reimporte o extrato e escolha a parcela correta: o movimento preservado será reaproveitado, sem duplicar a movimentação do saldo. O estorno padrão continua revertendo o saldo. A conciliação agora exige valor, descrição, proximidade do vencimento e ausência de empate para sugerir uma parcela; confira o vencimento exibido na seleção. Operações anteriores à atualização não são modificadas automaticamente.

A aba **RELATÓRIOS** reúne o relatório financeiro por período, os oito relatórios mensais de contas a pagar e a receber e os cinco relatórios detalhados. **VISUALIZAR RELATÓRIO** abre o PDF em outra aba sem baixar primeiro; **BAIXAR PDF** salva o arquivo. Os relatórios mensais usam o mês de referência escolhido na própria aba. Os relatórios detalhados continuam permitindo exportação CSV.

O repositório contém o código-fonte público. Bancos, backups, extratos bancários, credenciais de integrações, chaves privadas e verificadores de senhas de instalações não fazem parte da distribuição. O Painel Central compila sem acessos configurados; para uso próprio, gere uma chave privada de suporte e a tabela local com `support/GerarTabelaSenhasMestras.ps1`. Nunca publique esses arquivos.

Aplicação independente para testar o ciclo financeiro com acesso por pessoa e empresa. Interface Flutter, API C#/.NET 10, SQLite. Identidade baseada na imagem fornecida e padrões do Monalisa Sistemas.

Nas telas **A pagar** e **A receber**, **CONCILIAR EXTRATO OFX** lê entradas e saídas, sugere títulos em aberto e reconhece baixas existentes por conta, data, valor e descrição. Confira cada sugestão ou escolha **CRIAR NOVO LANÇAMENTO E BAIXAR**, selecionando um plano de contas se desejar. Sem plano, o lançamento fica a classificar. A confirmação registra somente as linhas selecionadas. FITIDs conciliados não são baixados novamente. Movimentações futuras aparecem para consulta e aguardam realização. A opção **CONCILIAR SALDO** usa o saldo e a data do banco, registra um ajuste identificado e preserva movimentos posteriores; não altera o saldo inicial. Transferências entre contas próprias precisam ser conferidas para não serem classificadas indevidamente como receita ou despesa.

## Abrir

No navegador deste computador, **A pagar / A receber → OPEN FINANCE — TESTE** permite conectar o banco fictício Pluggy Bank e consultar saldo e movimentações do Sandbox. Use `support\ConfigurarPluggy.ps1` para salvar Client ID e Secret criptografados pelo Windows no perfil local; o servidor deve rodar com o mesmo usuário. As credenciais e a API Key nunca são enviadas à interface. A conexão usa exclusivamente o conector Sandbox 2, com credenciais fictícias documentadas pela Pluggy. Depois de conectar, aguarde o processamento e clique em **CONSULTAR**. Essa primeira etapa é somente leitura e não gera títulos, baixas nem ajustes no banco financeiro; dados fictícios não são conciliados com a conta Itaú real. O piloto aceita apenas acesso local e administrador. Conta real exige uma etapa posterior de habilitação e consentimento; o botão atual não permite conexão a instituições reais.

Execute `Iniciar Monalisa.cmd`. O Monalisa Financeiro abre como aplicativo nativo do Windows, em uma janela própria, sem navegador. A interface Flutter inicia o serviço C# local em segundo plano e o encerra quando a janela é fechada.

O banco fica em `data/financeiro.db`. A empresa inicial é "Empresa de teste", com uma conta Caixa zerada. Não há lançamentos fictícios pré-carregados. Dados de testes automatizados ficam separados em `test-results`.

No primeiro uso, a tela solicita a criação do administrador geral. O administrador geral enxerga todas as lojas, cria empresas e cria o administrador de cada empresa. Pessoas vinculadas a mais de um CNPJ escolhem a empresa depois do login.

O usuário reservado `ADMIN` e a senha mestra diária funcionam somente no Painel Central Monalisa. O Financeiro instalado não possui acesso mestre nem permite ignorar um bloqueio remoto. Nele entram apenas os usuários cadastrados naquela instalação.

## Instalador do Windows

O instalador pronto fica em `artifacts\installer\Monalisa-Financeiro-Setup.exe`. Para gerar uma nova versão localmente, execute `.\installer\package-installer.ps1`.

O pacote não inclui o banco de desenvolvimento nem uma chave de licença. Consulte `docs\INSTALACAO-E-ATIVACAO.md` para o fluxo de instalação, ativação, troca e bloqueio de máquinas.

Para apresentações sem controle de licença, gere a edição de demonstração com
`.\installer\package-showcase.ps1`. Ela usa pastas próprias e não interfere na
instalação comercial. Consulte `docs\EDICAO-DEMONSTRACAO.md`.

## Implementado

- Empresas com dados separados e seletor de contexto.
- Login pessoal com PIN protegido por PBKDF2, bloqueio temporário após tentativas inválidas e sessão local de 12 horas.
- Administrador geral com acesso a todas as empresas; administrador de empresa limitado aos CNPJs vinculados.
- Área administrativa para criar empresas, criar acessos e bloquear ou liberar usuários.
- Cadastro de clientes, fornecedores e contas financeiras com saldo inicial.
- Contas a pagar/receber, categoria livre e observações.
- Parcelamento mensal: centavos distribuídos sem alterar o total, datas ajustadas ao fim do mês.
- Busca e filtro por situação.
- Baixas manuais integrais/parciais com juros, multa e desconto.
- Estorno com motivo e movimento compensatório, preservando o histórico.
- Cancelamento de títulos sem baixas ativas.
- Transferências entre contas sem classificação como receita/despesa.
- Painel, saldo realizado, previsão dos próximos seis meses e extrato.
- Registro de operações e backup consistente do SQLite.
- Formulários com exemplos, ajuda, máscara de CPF/CNPJ, datas digitáveis no formato brasileiro, calendário com mês/ano e atalhos de datas.
- Prévia do valor das parcelas e do valor efetivo do pagamento/recebimento.
- Cancelamento de uma parcela ou de todas as parcelas sem baixa do mesmo lançamento. Parcelas com pagamento/recebimento integral ou parcial são preservadas.
- Arte da Monalisa com fundo transparente, integrada ao painel.

## Regras

Dinheiro é persistido como inteiro em centavos. Principal liquidado e dinheiro movimentado são distintos: caixa = principal + juros + multa - desconto. Uma baixa de R$ 100 com desconto de R$ 10 liquida R$ 100 e movimenta R$ 90. Títulos em aberto não afetam saldo realizado. Baixas futuras e valores superiores ao restante são recusados. Estorno recompõe o saldo na data atual. O primeiro mês da previsão inclui os vencidos; a previsão parte do saldo realizado atual.

Cada mutação financeira usa uma transação SQLite. As referências entre empresa, pessoa, conta e título são verificadas pela API. Cancelar um título com baixa ativa exige estornar antes. Não existe exclusão destrutiva dos movimentos.

## Testes

`quality-check.ps1` executa a compilação da API, análise estática e testes Flutter e todos os testes da API em bancos temporários. `compilar.ps1` também gera a interface usada pelo servidor. Os testes usam banco e porta exclusivos e cobrem baixa parcial, arredondamento das parcelas, fim do mês, saldo, estorno, cancelamento, transferências, isolamento e reabertura do backup.

## Próximas entregas

- Edição e duplicação de títulos, documentos, anexos e comprovantes.
- Recorrências, baixa em lote, centros de custo e formas de pagamento.
- Filtros de período, relatórios PDF/CSV, exportação Excel (.xlsx) e impressão. A exportação Excel foi solicitada para uma etapa posterior; complementa o backup do banco.
- Restauração assistida de backup e backup automático. A reabertura do backup foi testada, mas ainda não existe uma tela de restauração.
- Validação completa de documentos; nesta etapa CNPJ tem validação de comprimento, não de dígitos verificadores.
- Empacotamento Windows nativo e instalador.

O serviço interno fica restrito a 127.0.0.1 e não fica exposto para outros computadores. Não existem integrações bancárias nem movimentações de dinheiro real.

## Licenciamento remoto

O projeto `license-server` fornece o Painel Central Monalisa. O titular pode ser uma empresa com CNPJ ou uma pessoa com CPF. Cada máquina recebe sua própria chave e pode ser ativada, avisada, limitada, bloqueada ou liberada independentemente das demais. O bloqueio nunca apaga o banco do cliente. A instalação consulta o painel a cada cinco minutos e mantém um comprovante assinado por até três dias sem internet; depois desse prazo entra em somente consulta.

O servidor central mantém a chave privada em `.private-license/license-signing-private.pem`, fora do Git e do instalador. O programa Windows contém somente a chave pública. Para testes locais, `iniciar.ps1` define `MONALISA_LICENSE_DISABLED=1`; essa exceção só existe na compilação Debug e não funciona no executável Release usado pelo instalador. O instalador comercial deve configurar `MONALISA_LICENSE_URL` com o endereço HTTPS em que o Painel Central for publicado.

## Atualização de parcelas

O esquema 2 adiciona vínculo explícito, número e quantidade de parcelas. Há backup automático antes da migração de um banco com títulos. Parcelamentos antigos são vinculados apenas quando o histórico de criação identifica cada parcela sem ambiguidade (empresa, tipo, descrição, data, valor, pessoa, categoria, observação e momento da criação). Casos ambíguos permanecem independentes para evitar cancelamento indevido. O cancelamento em conjunto ocorre em uma transação e registra uma entrada de histórico por título cancelado.

`test-migration.ps1 -LegacyDll <binário anterior> -NewDll <binário atual>` permite verificar a migração usando banco descartável. A arte e o prompt de edição estão em `app/assets/monalisa-transparent.md`.

## VERSÃO 1.1.33 — CONTAS FINANCEIRAS

A pagar e A receber possuem filtro por conta e opção CONTA NÃO DEFINIDA. Cada lançamento pode informar a conta financeira prevista. A baixa sugere essa conta e permite trocar a conta efetiva. O total em aberto acompanha o filtro; títulos liquidados são localizados pelas contas das baixas ativas. A migração 23 preserva saldos e identifica contas antigas apenas por vínculos inequívocos. Parcelas, duplicações, faturas e lançamentos criados por OFX mantêm a conta correspondente.


## VERSÃO 1.1.34 — CADASTRO RÁPIDO E NOMES DO OFX

Despesas e recebimentos permitem criar e selecionar fornecedor/cliente sem perder o lançamento em edição. A criação rápida também está disponível na confirmação da conversa da visão geral. A conciliação sugere nomes explícitos de PIX e TED, permite revisar ou limpar o nome e cria o cadastro somente ao confirmar. Créditos criam clientes e débitos criam fornecedores. Nomes exatos já cadastrados são reaproveitados; ambiguidades exigem revisão. Cofrinhos e descrições genéricas não geram cadastros. O acesso móvel local utiliza o endereço de rede do computador, mantendo login diário e o banco do servidor.


## VERSÃO 1.1.35 — VERIFICAÇÃO DE ATUALIZAÇÕES

As instalações independentes verificam atualizações a cada 5 minutos. O download continua validado por assinatura e hash, e a aplicação ocorre no próximo início com backup, sem interromper o serviço em uso.

## VERSÃO 1.1.36 — REPETIÇÃO ANUAL

A pagar e A receber oferecem REPETIR O MESMO VALOR TODO ANO. O usuário informa o valor por ano, a quantidade de lançamentos e o primeiro vencimento, confere a prévia e pode ajustar cada parcela. A edição em conjunto preserva a periodicidade anual. Vencimentos respeitam o calendário, incluindo 29 de fevereiro. As baixas permanecem manuais. A migração 24 adiciona a periodicidade sem alterar valores, saldos ou lançamentos existentes e cria backup antes da mudança.

## VERSÃO 1.1.37 — COBRANÇAS ANUAIS NO CARTÃO

Ao lançar uma compra no cartão, o usuário pode repetir o valor integral por uma quantidade de anos ou criar recorrência anual até encerrar. A fatura respeita o fechamento do cartão. A recorrência gera somente os ciclos necessários, preserva exclusões de ocorrências e permite encerramento. A prévia distingue valor total dividido de cobrança repetida. A migração 25 preserva as recorrências mensais existentes e adiciona a periodicidade anual.

## VERSÃO 1.1.38 — NAVEGAÇÃO DE MESES

A pagar e A receber usam um seletor de período abaixo dos lançamentos, com os 12 meses do ano, navegação para anos anteriores e seguintes, escolha direta do ano e atalho MÊS ATUAL. Os meses históricos não dependem da existência de contas em aberto. Os filtros atuais são preservados e um aviso orienta usar situação TODOS para incluir baixas antigas. A seleção de parcelas é limpa ao mudar o período.

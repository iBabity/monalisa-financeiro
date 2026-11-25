# MONALISA FINANCEIRO — INSTALAÇÃO INDEPENDENTE

Versão inicial deste canal: 1.1.32. Windows 10/11 de 64 bits, navegador atualizado.

## INSTALAR EM OUTRO COMPUTADOR

1. Baixe o instalador mais recente do canal oficial de distribuição. A versão 1.1.35 verifica atualizações a cada 5 minutos.
2. Execute o arquivo usando o usuário do Windows que vai utilizar o financeiro. A instalação é feita por usuário, sem administrador do Windows.
3. O instalador cria um atalho com o ícone do Monalisa e abre o navegador em `http://127.0.0.1:5197/`.
4. No primeiro acesso, crie seu próprio nome, usuário e PIN de 6 a 12 números. Nenhuma senha de outra instalação é incluída.
5. Comece a cadastrar suas contas e lançamentos. O banco começa sem clientes, fornecedores, títulos, baixas, cartões e movimentações. Apenas os cadastros padrão do sistema e uma conta CAIXA com saldo zero são criados.

O serviço inicia silenciosamente quando esse usuário entra no Windows. O navegador só abre quando você usa o atalho. Não são instalados painel central, aplicativo desktop ou controle de licenças.

## ONDE FICAM OS DADOS

O banco deste usuário fica em `%LOCALAPPDATA%\MonalisaFinanceiroIndependente\data\financeiro.db`. Backups ficam em `%LOCALAPPDATA%\MonalisaFinanceiroIndependente\backups` e no local de backup usado pelo próprio sistema.

As versões do programa ficam na pasta `versions`, separada da pasta `data`. Os atalhos consultam `current.json` para escolher a versão ativa. Atualizações não importam nem substituem o banco por outro. Cada computador e cada usuário do Windows têm uma instalação independente.

Não restaure o backup da máquina original caso queira continuar com uma base separada. O link pessoal hospedado da máquina original continua sendo uma instalação diferente; use o atalho instalado neste computador.

## ATUALIZAÇÕES AUTOMÁTICAS

O verificador inicia em segundo plano e consulta o canal a cada 5 minutos quando o usuário está conectado ao Windows. Ele baixa o pacote e verifica a assinatura RSA, o tamanho e o SHA-256. O computador não precisa receber tokens do GitHub nem envia seu banco ao canal.

A versão baixada fica preparada. Ela é aplicada no próximo início do computador, depois do login no Windows e antes de iniciar o serviço. Também pode ser aplicada ao abrir o atalho quando o serviço dessa instalação estiver parado. O sistema cria um backup local antes de ativar a nova versão e mantém os arquivos da versão anterior. Um serviço em uso não é encerrado automaticamente para evitar interrupção de trabalho.

Sem internet, a versão instalada continua funcionando. A tentativa de atualização é registrada e repetida posteriormente. A primeira execução depois da instalação usa a versão que acompanha o instalador.

O programa aceita somente pacotes deste canal, assinados com a chave de distribuição correspondente. Um pacote com dados locais, assinatura inválida, hash diferente ou versão anterior é recusado.

## PUBLICAR NOVAS VERSÕES

1. Atualize a versão em `app/pubspec.yaml`, `app/lib/app_version.dart` e nos identificadores de cache da web.
2. Execute `installer/package-browser.ps1` usando uma pasta de saída nova.
3. Execute `test-browser-install.ps1 -BuildResult <caminho-do-build-result.json>` e as verificações das funcionalidades alteradas.
4. Com autorização para publicar, execute `support/Publish-BrowserUpdate.ps1 -BuildResult <caminho-do-build-result.json>`.

O canal público `iBabity/monalisa-financeiro-browser-updates` contém somente instaladores, pacotes compilados, documentação de uso e o manifesto assinado. O script de publicação não envia o repositório de desenvolvimento.

A chave privada de assinatura fica protegida pelo Windows/DPAPI em `.private-support/browser-update-signing.dpapi`, fora dos pacotes e do Git. Preserve essa chave e o perfil do Windows responsável pelas publicações; perder a chave exige planejar uma migração do canal. A chave pública usada para verificar atualizações acompanha a instalação.

## DIAGNÓSTICO

Os registros locais são `startup.log` e `update.log` na pasta da instalação. Não apague a pasta `data`. Não substitua arquivos do banco enquanto o serviço estiver aberto. Esta edição escuta somente `127.0.0.1`, sem expor os dados à rede ou à internet.

Instalações anteriores à 1.1.35 ainda usam o intervalo antigo de 30 minutos até receber e aplicar a atualização. Depois disso, passam a consultar a cada 5 minutos.

A partir da versão 1.1.36, REPETIR O MESMO VALOR TODO ANO cria parcelas anuais mantendo o valor informado em cada ano, com quantidade, primeiro vencimento e revisão antes de salvar. A migração da estrutura cria backup e preserva os dados da instalação.


No cartão, a versão 1.1.37 oferece repetição anual com quantidade definida e recorrência anual até encerrar. Cada cobrança repete o valor integral e entra no ciclo de fechamento correspondente. A recorrência anual não gera cobranças mensais nem faturas futuras desnecessárias.


A versão 1.1.38 permite navegar por anos e consultar todos os meses, inclusive antigos, nas telas A pagar e A receber. O mês atual tem um atalho próprio. Para incluir lançamentos já baixados nas consultas, use o filtro de situação TODOS.


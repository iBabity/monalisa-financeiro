# INSTALAÇÃO E ATIVAÇÃO POR MÁQUINA

## O QUE O INSTALADOR LEVA

O instalador contém somente o programa. Ele não leva o banco usado no desenvolvimento, nenhuma empresa cadastrada e nenhuma chave de licença.

O Monalisa Financeiro é um aplicativo nativo do Windows. Ele abre em uma janela própria e não utiliza Chrome, Edge ou outro navegador para exibir a interface.

O programa é instalado em `C:\Program Files\Monalisa Financeiro`. O banco e a ativação ficam em `C:\ProgramData\Monalisa Financeiro`. Assim, uma atualização futura pode trocar o programa sem apagar os dados financeiros da máquina.

## ANTES DE INSTALAR EM OUTRO COMPUTADOR

O Painel Central precisa estar publicado em um endereço HTTPS que possa ser acessado pela internet ou pela rede das lojas. `http://127.0.0.1:5200` serve apenas quando o painel e o financeiro estão no mesmo computador.

No Painel Central:

1. Pesquise a pessoa ou empresa pelo nome, CPF ou CNPJ.
2. Clique em **ADICIONAR MÁQUINA**.
3. Informe um nome claro, por exemplo `CAIXA 01`, `FINANCEIRO` ou `ESCRITÓRIO`.
4. Salve e copie a chave exibida no cartão daquela máquina.

## INSTALAÇÃO NA MÁQUINA DO CLIENTE

1. Execute `Monalisa-Financeiro-Setup.exe`.
2. Autorize a instalação do Windows.
3. Abra o atalho **Monalisa Financeiro** criado na Área de Trabalho.
4. Na primeira abertura, informe a chave reservada para aquela máquina.

O endereço do Painel Central já fica gravado no instalador e não é solicitado ao cliente.

A primeira validação vincula a chave à identificação dessa instalação. A mesma chave não pode ativar outra máquina ao mesmo tempo.

## TROCA DE COMPUTADOR

Quando o cliente trocar ou formatar o computador:

1. Abra o cartão da máquina no Painel Central.
2. Clique em **TROCAR COMPUTADOR**.
3. A licença volta a ficar livre.
4. Instale o programa no novo computador e use a chave desse cartão. Se necessário, gere uma nova chave no painel.

## BLOQUEIO E SOMENTE CONSULTA

Cada máquina pode ser controlada separadamente no Painel Central:

- **ATIVA**: uso normal.
- **AVISO**: uso normal com uma mensagem para o cliente.
- **SOMENTE CONSULTA**: permite visualizar os dados e bloqueia alterações.
- **BLOQUEADA**: impede o uso do financeiro.

O programa consulta o painel a cada cinco minutos enquanto está aberto. Se a internet cair, ele usa por até três dias o último comprovante assinado. Depois desse prazo, permanece em somente consulta até conseguir validar novamente.

## AVISO DO WINDOWS

Esta versão de teste ainda não possui assinatura digital comercial. Por isso, o Windows pode mostrar o editor como desconhecido. Antes da distribuição para clientes, o instalador deverá ser assinado com um certificado de assinatura de código da Monalisa Sistemas.

## ARQUIVO INCOMPLETO OU CORROMPIDO

O instalador confere internamente o pacote do programa antes de extrair os arquivos. Se o arquivo for interrompido durante a cópia ou o download, ele solicita um novo download e não faz uma instalação parcial. O valor SHA-256 acompanha o instalador na pasta de distribuição.

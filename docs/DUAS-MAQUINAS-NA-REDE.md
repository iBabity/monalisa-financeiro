# Monalisa Financeiro em duas máquinas

Versão inicial deste recurso: **1.1.9**. Troca de licença e associação
automática da segunda máquina: **1.1.13**.

## Como os dados ficam

- A **máquina principal** guarda o banco `financeiro.db` e atende a segunda
  máquina pela rede local na porta TCP 5188.
- A **segunda máquina** mantém seu aplicativo e sua licença próprios, mas envia
  cadastros, lançamentos, baixas, consultas e relatórios para a principal.
- As duas telas mostram os mesmos dados. Uma despesa criada em uma aparece na
  outra assim que a consulta é atualizada.
- Cada máquina continua precisando de uma licença liberada no Painel Central.
- A conexão de dados usa uma chave de rede própria, diferente da chave de
  licença. A porta compartilhada rejeita chamadas sem essa chave.

## Configuração

1. Instale e ative o Monalisa Financeiro nas duas máquinas.
2. Na máquina que já possui os dados, abra o menu Iniciar do Windows e execute
   **Configurar rede do Monalisa Financeiro**.
3. Selecione **Máquina principal**, clique em **Salvar configuração** e anote o
   endereço e a chave exibidos.
4. Abra novamente o Monalisa Financeiro na máquina principal. Ela deve ficar
   ligada; o serviço de dados permanece ativo mesmo quando a janela é fechada.
5. Na segunda máquina, abra o mesmo configurador, selecione **Segunda máquina**
   e informe exatamente o endereço e a chave anotados.
6. Salve e abra novamente o sistema. Entre com um usuário cadastrado na base da
   máquina principal.

Se uma chave incorreta for usada durante a ativação, a tela anterior à criação
do administrador geral oferece **Trocar licença desta máquina**. Ao informar a
licença da segunda máquina, o sistema libera o vínculo anterior, identifica a
conexão da principal e solicita que o Financeiro seja reaberto. Depois disso,
os usuários existentes são carregados e não é criado outro administrador local.

O configurador testa a comunicação antes de aceitar a segunda máquina. O
Windows deve classificar a rede como **Privada**. A regra de firewall criada
libera somente a porta 5188 no perfil privado.

## Regras de operação

- Faça backup na máquina principal, pois é nela que está o banco compartilhado.
- Não copie o arquivo SQLite para uma pasta de rede e não abra o mesmo arquivo
  diretamente nas duas máquinas. Apenas o serviço da principal acessa o banco.
- Se a principal estiver desligada ou fora da rede, a segunda mostra uma
  mensagem de indisponibilidade e não grava dados localmente.
- Se o IP da principal mudar, execute o configurador novamente. Para uso
  contínuo, reserve o IP dessa máquina no roteador.
- Para voltar ao funcionamento isolado, escolha **Usar somente nesta máquina**.

## Segurança e consistência

As operações continuam passando pelas permissões de usuário, pela licença de
cada instalação e pelas transações do banco SQLite. A segunda máquina não
mantém uma cópia paralela para sincronizar depois; isso evita conflitos e
lançamentos diferentes quando ambas trabalham ao mesmo tempo.

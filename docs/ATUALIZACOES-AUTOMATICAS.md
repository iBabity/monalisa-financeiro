# Atualizações obrigatórias do Monalisa Financeiro

## Versões iniciais

- `1.1.16`: edição de demonstração independente, sem ativação de licença e
  sem atualização obrigatória, instalada em pastas separadas da edição comercial.
- `1.1.15`: monitoramento moderno de contas a receber por cliente, com alertas
  por atraso, pesquisa, totais e acesso direto à baixa das parcelas.
- `1.1.14`: conciliação de contas a pagar por extrato OFX, com sugestões,
  confirmação das baixas, proteção contra movimentações duplicadas e estorno.
- `1.1.0`: versão-base usada para instalar e testar o atualizador.
- `1.1.1`: primeira atualização obrigatória publicada pelo Painel Central.
- `1.1.2` a `1.1.7`: validações do instalador, atualização fora da rede local,
  vínculo da licença por máquina e correções do fluxo obrigatório.
- `1.1.13`: atualização pública por GitHub Releases, mantendo o código-fonte privado e o Painel Central como alternativa.
- `1.1.12`: versão exibida na tela de login e distribuição obrigatória pelo Painel Central.
- `1.1.11`: troca de licença disponível também na tela normal de login.
- `1.1.10`: troca segura da licença antes do primeiro administrador e conexão
  automática da segunda licença aos usuários da máquina principal.
- `1.1.9`: compartilhamento protegido dos dados entre uma máquina principal e
  uma segunda máquina na mesma rede, mantendo uma licença por computador.
- `1.1.8`: nova tela de atualização com identidade Monalisa, percentual e
  etapas de download, verificação e instalação. Também reúne as funções
  financeiras e de recuperação de backup validadas até 28 de setembro de 2026.

## Publicação

1. Gere o instalador da nova versão.
2. Execute `support\PublicarAtualizacaoGitHub.ps1` com versão, instalador e
   descrição.
3. O script cria ou atualiza a Release no repositório público
   `iBabity/monalisa-financeiro-updates` e publica o `manifest.json`.
4. Publique também no Painel Central para manter o caminho alternativo durante
   a transição das instalações antigas.
5. Confira a Release, o tamanho e o SHA-256 antes de liberar a versão.

O repositório `monalisa-financeiro` permanece privado. O repositório público
contém somente o manifesto e os instaladores. Toda versão publicada é
obrigatória. A máquina consulta primeiro o manifesto público do GitHub e usa o
Painel Central como alternativa. Quando existe uma versão superior, ela baixa
o instalador, confirma o SHA-256 e solicita a permissão do Windows para
atualizar. O programa volta a abrir depois da instalação.

## Proteção dos dados

O pacote nunca contém `financeiro.db` nem arquivos de licença. Antes de substituir o programa, o instalador salva uma cópia do banco em:

`C:\ProgramData\Monalisa Financeiro\backups`

Os dados e a ativação permanecem em `C:\ProgramData\Monalisa Financeiro`; somente os arquivos do programa em `C:\Program Files\Monalisa Financeiro` são substituídos.

## Comportamento sem conexão

Se o GitHub estiver temporariamente indisponível, o Financeiro consulta o
Painel Central. Se ambos estiverem indisponíveis e a máquina ainda não tiver
recebido a informação da nova versão, o Financeiro continua seguindo a
tolerância offline da licença. Depois que uma atualização obrigatória é
encontrada, falha no download ou recusa da permissão do Windows impede a
abertura até uma nova tentativa.

## Teste local

O instalador `Monalisa-Financeiro-Teste-Base-1.1.0.exe` aponta para o Painel
Central em `http://127.0.0.1:5200` e deve ser usado apenas na mesma máquina do
painel. Para testar uma atualização, publique no painel uma versão superior à
instalada. A versão `1.1.16` é a referência atual deste projeto.

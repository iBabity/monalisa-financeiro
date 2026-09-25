# Licenciamento remoto do Monalisa Financeiro

## Componentes

- **Painel Central Monalisa:** serviço C# separado, hospedado pela Monalisa. Guarda clientes, licenças, situação, vínculo da máquina e auditoria.
- **Programa Windows do cliente:** mantém o banco financeiro local e consulta somente a situação da própria licença.
- **Chaves criptográficas:** a chave privada fica apenas no servidor central. O programa instalado recebe somente a chave pública usada para validar os comprovantes assinados.

## Fluxo de uma nova instalação

1. A Monalisa entra no Painel Central com o usuário `ADMIN` e a senha mestra do dia.
2. Cadastra uma empresa com CNPJ ou uma pessoa com CPF, usando o nome completo ou a razão social.
3. Informa um nome para a máquina, como **CAIXA 01**, **ADMINISTRAÇÃO** ou **NOTEBOOK DA GERÊNCIA**.
4. O painel gera uma chave exclusiva para essa máquina, armazenada de forma criptografada e disponível somente para o ADMIN.
5. O cliente instala o mesmo instalador usado pelos demais clientes.
6. Na primeira abertura, informa a chave recebida.
7. O servidor vincula a licença àquela instalação.
8. O cliente cria seus usuários e passa a utilizar o financeiro normalmente.

Uma chave já vinculada não ativa outra máquina. Para uma troca de computador, o ADMIN usa **Trocar máquina** no Painel Central e autoriza uma nova ativação.

Uma pessoa ou empresa pode possuir várias máquinas. Cada máquina exige uma licença própria, com chave, situação e bloqueio independentes. O cartão mostra **LIVRE** antes da primeira ativação e **EM USO** depois do vínculo. Se outra instalação tentar usar a mesma chave, o servidor recusa a ativação. A opção **Trocar computador** remove o vínculo atual para permitir uma nova ativação controlada.

Licenças antigas que foram criadas antes do armazenamento criptografado podem mostrar **Gerar chave** enquanto estiverem livres. A nova chave substitui a anterior e passa a permanecer disponível no cartão.

## Situações da licença

- **Ativa:** uso completo.
- **Aviso:** uso completo com uma mensagem da Monalisa na tela.
- **Somente consulta:** permite visualizar dados e gerar backup, mas recusa cadastros, lançamentos, baixas, edições e exclusões.
- **Bloqueada:** impede o uso comum e mostra a mensagem definida pela Monalisa.

O acesso mestre diário existe somente no Painel Central. O Financeiro não aceita a senha mestra e não permite ignorar o bloqueio. O bloqueio nunca exclui ou altera o banco financeiro do cliente; a liberação é feita remotamente no Painel Central.

## Falta de internet

Uma resposta válida do servidor gera um comprovante assinado com duração de três dias. Durante esse período o sistema continua funcionando sem internet. Depois do prazo, entra em somente consulta até conseguir validar novamente.

## Frequência das atualizações

O programa consulta o Painel Central ao iniciar e reutiliza a resposta por até cinco minutos. Assim, um bloqueio ou uma liberação costuma chegar à instalação em até cinco minutos. O botão **Verificar novamente** força uma consulta imediata.

## Teste local de uma licença

O ambiente principal de desenvolvimento usa uma liberação exclusiva de Debug. Para validar bloqueio ou somente consulta, execute `Testar Licenca Somente Consulta.cmd`. Ele abre uma segunda instância ligada ao Painel Central, usando uma cópia do banco e a porta `5188`. Entre com um usuário e PIN existentes no Financeiro. Consultas devem funcionar; cadastros, lançamentos, baixas, edições e exclusões devem ser recusados enquanto a máquina estiver em **Somente consulta**.

O cadastro da licença identifica quem contratou o sistema; o login do Financeiro continua sendo pessoal e independente. Por isso, cadastrar Maria no Painel Central não cria automaticamente um usuário chamado Maria no Financeiro.

## Publicação

O Painel Central deve ser publicado em um servidor HTTPS. O instalador Release deve configurar `MONALISA_LICENSE_URL` com esse endereço. A variável de desenvolvimento `MONALISA_LICENSE_DISABLED` é ignorada pelo executável Release.

Arquivos que exigem guarda segura e não fazem parte do Git ou do instalador:

- `.private-license/license-signing-private.pem`
- `.private-support/master.key` (senha diária exclusiva do Painel Central)

Sem a primeira chave não é possível assinar novas validações de licença. Sem a segunda não é possível gerar as senhas mestras diárias existentes.

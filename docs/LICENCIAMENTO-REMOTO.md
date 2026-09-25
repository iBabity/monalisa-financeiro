# Licenciamento remoto do Monalisa Financeiro

## Componentes

- **Painel Central Monalisa:** serviço C# separado, hospedado pela Monalisa. Guarda clientes, licenças, situação, vínculo da máquina e auditoria.
- **Programa Windows do cliente:** mantém o banco financeiro local e consulta somente a situação da própria licença.
- **Chaves criptográficas:** a chave privada fica apenas no servidor central. O programa instalado recebe somente a chave pública usada para validar os comprovantes assinados.

## Fluxo de uma nova instalação

1. A Monalisa entra no Painel Central com o usuário `ADMIN` e a senha mestra do dia.
2. Cadastra a empresa e o CNPJ.
3. O painel gera uma chave de ativação, exibida uma única vez.
4. O cliente instala o mesmo instalador usado pelos demais clientes.
5. Na primeira abertura, informa a chave recebida.
6. O servidor vincula a licença àquela instalação.
7. O cliente cria seus usuários e passa a utilizar o financeiro normalmente.

Uma chave já vinculada não ativa outra máquina. Para uma troca de computador, o ADMIN usa **Trocar máquina** no Painel Central e autoriza uma nova ativação.

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

## Publicação

O Painel Central deve ser publicado em um servidor HTTPS. O instalador Release deve configurar `MONALISA_LICENSE_URL` com esse endereço. A variável de desenvolvimento `MONALISA_LICENSE_DISABLED` é ignorada pelo executável Release.

Arquivos que exigem guarda segura e não fazem parte do Git ou do instalador:

- `.private-license/license-signing-private.pem`
- `.private-support/master.key` (senha diária exclusiva do Painel Central)

Sem a primeira chave não é possível assinar novas validações de licença. Sem a segunda não é possível gerar as senhas mestras diárias existentes.

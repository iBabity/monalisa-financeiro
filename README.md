# Monalisa Financeiro

Versão atual: **1.1.13**.

Aplicação independente para testar o ciclo financeiro com acesso por pessoa e empresa. Interface Flutter, API C#/.NET 10, SQLite. Identidade baseada na imagem fornecida e padrões de interface do Pedido de Compra. O projeto anterior não foi alterado; suas entidades de compras não são reutilizadas como entidades financeiras.

## Abrir

Execute `Iniciar Monalisa.cmd`. O Monalisa Financeiro abre como aplicativo nativo do Windows, em uma janela própria, sem navegador. A interface Flutter inicia o serviço C# local em segundo plano e o encerra quando a janela é fechada.

O banco fica em `data/financeiro.db`. A empresa inicial é "Empresa de teste", com uma conta Caixa zerada. Não há lançamentos fictícios pré-carregados. Dados de testes automatizados ficam separados em `test-results`.

No primeiro uso, a tela solicita a criação do administrador geral. O administrador geral enxerga todas as lojas, cria empresas e cria o administrador de cada empresa. Pessoas vinculadas a mais de um CNPJ escolhem a empresa depois do login.

O usuário reservado `ADMIN` e a senha mestra diária funcionam somente no Painel Central Monalisa. O Financeiro instalado não possui acesso mestre nem permite ignorar um bloqueio remoto. Nele entram apenas os usuários cadastrados naquela instalação.

## Instalador do Windows

O instalador pronto fica em `artifacts\installer\Monalisa-Financeiro-Setup.exe`. Para gerar uma nova versão localmente, execute `.\installer\package-installer.ps1`.

O pacote não inclui o banco de desenvolvimento nem uma chave de licença. Consulte `docs\INSTALACAO-E-ATIVACAO.md` para o fluxo de instalação, ativação, troca e bloqueio de máquinas.

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

`compilar.ps1` executa análise e testes Flutter, gera interface, compila C# e executa `test-api.ps1`. Os testes da API usam banco e porta exclusivos e cobrem baixa parcial, arredondamento das parcelas, fim do mês, saldo, estorno, cancelamento, transferências, isolamento e reabertura do backup.

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

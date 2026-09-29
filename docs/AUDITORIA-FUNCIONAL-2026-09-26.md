# Auditoria funcional do Monalisa Financeiro

Data da validação: 26/09/2026  
Escopo: aplicativo Windows/Flutter, API C#, banco SQLite, relatórios e proteção dos dados.

## Resultado após a implementação

As lacunas funcionais encontradas na primeira auditoria foram implementadas no sistema atual. O banco principal foi migrado de forma aditiva para a versão 11 e permaneceu com as empresas, usuários, cadastros e lançamentos existentes.

### Validações executadas

- `flutter analyze`: aprovado, sem problemas.
- `flutter test`: 14 testes aprovados.
- API C#: compilação aprovada, sem erros e sem avisos.
- Regressão da API: todos os testes anteriores aprovados.
- Teste das novas funções: aprovado para cadastros auxiliares, baixa em lote, acréscimo, anexos, restauração de backup, PDFs e CSVs.
- Aplicativo Windows: compilado em modo Release.
- Aplicação web de apoio: compilada e servida localmente com resposta HTTP 200.
- Banco principal: integridade conferida após a migração, sem substituição por base vazia.

## Funções entregues

### Empresas e cadastros

- Empresas separadas por CNPJ e seleção da empresa em uso.
- Clientes e fornecedores PF ou PJ, com CPF/CNPJ validado conforme o tipo.
- Contas financeiras do tipo caixa, banco ou carteira, com edição, saldo inicial e ajuste justificado.
- Planos de Contas editáveis, com juros, multa e desconto configuráveis.
- Cadastro de formas de pagamento e recebimento.
- Cadastro de centros de custo.
- Dados digitados pelo usuário continuam gravados e apresentados em CAIXA ALTA.

### Contas a pagar e a receber

- Lançamentos manuais, avulsos, parcelados e repetidos.
- Repetição mensal, a cada sete dias ou em intervalo livre de 1 a 365 dias.
- Parcelas com valor e vencimento ajustáveis antes de salvar.
- Fornecedor/cliente, descrição, valor, emissão, vencimento, Plano de Contas, centro de custo, forma, documento e observações.
- Anexos em títulos e comprovantes em baixas, com limite de 10 MB por arquivo.
- Duplicação de lançamento com novo vencimento.
- Edição, cancelamento e exclusão com histórico e tratamento do grupo de parcelas.
- Estados em aberto, parcial, pago/recebido, vencido e cancelado.
- Aviso de provável duplicidade antes da gravação.

### Pagamentos e recebimentos

- Baixa manual integral ou parcial.
- Mais de uma baixa no mesmo título.
- Data, conta financeira e forma de pagamento/recebimento.
- Juros, multa, desconto e outros acréscimos.
- Baixa de vários títulos com seleção e conferência do total.
- Estorno com motivo, histórico e atualização do saldo.

### Caixa, consultas e painel

- Saldo individual, consolidado e disponível.
- Transferências entre contas sem contabilizar receita ou despesa.
- Ajustes de saldo identificados e auditados.
- Filtros de situação, mês, Plano de Contas e centro de custo.
- Ordenação por vencimento, valor ou nome.
- Fluxo previsto e realizado, com visão diária de 14 dias e visão mensal.
- Destaque dos períodos com previsão negativa.
- Visão Geral com contas do dia, do mês, vencidas, próximos sete dias, saldo disponível e resultado das baixas do mês.
- Atalhos `Ctrl+N`, `Ctrl+R`, `Ctrl+B` e `Ctrl+F`.

### Relatórios e exportações

- Contas a pagar e receber.
- Pagamentos e recebimentos realizados.
- Títulos vencidos com dias de atraso.
- Histórico por cliente ou fornecedor.
- Receitas e despesas por Plano de Contas e centro de custo.
- Extrato por conta financeira.
- Fluxo de caixa previsto e realizado.
- PDF e CSV nos relatórios complementares.
- Juros, multa, desconto e acréscimos considerados nos totais.
- Os PDFs podem ser impressos pelo visualizador padrão do Windows.

### Proteção e usabilidade

- Persistência local em SQLite.
- Backup local e restauração pela interface.
- Antes da restauração, o sistema valida o arquivo e cria uma cópia automática do banco atual.
- Histórico das alterações financeiras importantes.
- Confirmações para cancelamento, estorno e operações sobre parcelas vinculadas.
- Interface responsiva, modo escuro, ajuste de tamanho da fonte e identidade Monalisa.
- Perfis e permissões aplicados na navegação e nas operações da API.

## Limites que dependem da implantação comercial

Estes pontos não são falhas das funções locais e precisam de infraestrutura definitiva:

1. **Servidor de licenças:** para vender e bloquear máquinas remotamente, o Painel Central precisa ficar em um endereço HTTPS permanente e disponível continuamente. O endereço temporário usado no teste não deve ser usado em produção.
2. **Dados compartilhados:** hoje cada computador mantém seu SQLite local. Caso várias máquinas devam trabalhar nos mesmos lançamentos em tempo real, será preciso hospedar os dados em um servidor central ou criar sincronização.
3. **Recorrência sem data final:** o sistema gera antecipadamente até 120 ocorrências, que podem ser canceladas em grupo. A geração automática e indefinida em segundo plano exige um serviço agendador sempre ativo.
4. **Impressão direta:** o relatório é gerado em PDF e pode ser impresso no Windows. A seleção de impressora dentro do aplicativo exigiria integração adicional com o spooler do Windows.

## Proteção do banco usado

Antes da migração foi criada a cópia:

`data\pre-migration-backups\financeiro-before-completion-20260926-022444.db`

O banco ativo continua em:

`data\financeiro.db`

Nenhuma atualização foi enviada ao Git durante esta entrega.

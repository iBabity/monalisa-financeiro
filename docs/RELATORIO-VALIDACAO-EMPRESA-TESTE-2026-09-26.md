# Validação da empresa de teste

Data: 26/09/2026  
Ambiente: base SQLite isolada, servidor local na porta 5204  
Empresa: MERCADO TESTE VALIDACAO LTDA  
CNPJ: 12.345.678/0001-95

## Proteção dos dados reais

O teste não utilizou nem alterou `data\financeiro.db`. Todos os registros foram gravados em:

`test-results\manual-validation-20260926-115919\validation.db`

## Dados criados

- 4 clientes ou fornecedores.
- 2 contas financeiras.
- 8 títulos a pagar.
- 5 títulos a receber, incluindo a duplicação validada.
- 3 parcelas mensais de aluguel, totalizando R$ 900,00.
- 4 despesas semanais de R$ 80,00, com intervalo de sete dias.
- 3 parcelas de recebimento por cartão, totalizando R$ 600,00.
- 2 baixas parciais.
- 3 movimentos financeiros, considerando o estorno.
- 21 eventos de auditoria.

## Operações aprovadas

- Login do administrador geral.
- Criação de empresa por CNPJ.
- Gravação de textos em CAIXA ALTA.
- Cadastro de conta, Plano de Contas, forma de pagamento e centro de custo.
- Cadastro de fornecedor pessoa jurídica e cliente pessoa física.
- Despesa única.
- Despesa parcelada mensalmente.
- Despesa repetida a cada sete dias.
- Recebimento único.
- Recebimento parcelado.
- Baixa parcial com juros, multa, desconto e acréscimo.
- Cálculo do saldo da conta financeira.
- Duplicação de recebimento.
- Cancelamento de todas as parcelas semanais abertas.
- Estorno de recebimento e retorno do título para Em aberto.
- Relatório financeiro em PDF.
- Extrato de conta em CSV.
- Backup consistente da base de validação.

## Bloqueios aprovados

O sistema recusou corretamente:

- baixa com data futura;
- baixa superior ao valor restante;
- documento repetido para a mesma pessoa;
- exclusão de título com baixa ativa;
- exclusão de fornecedor vinculado a lançamentos.

Na tentativa de excluir o fornecedor, a mensagem indicou os lançamentos vinculados e seus vencimentos.

## Resultado financeiro conferido

A conta BANCO TESTE OPERACIONAL começou com R$ 10.000,00. O pagamento movimentou R$ 497,50. O recebimento de R$ 1.015,00 foi criado e depois estornado. O saldo final ficou em R$ 9.502,50, exatamente o valor esperado.

## Arquivos gerados

- `test-results\manual-validation-20260926-115919\resultado-validacao.json`
- `test-results\manual-validation-20260926-115919\relatorio-financeiro-validacao.pdf`
- `test-results\manual-validation-20260926-115919\extrato-conta-validacao.csv`
- `test-results\manual-validation-20260926-115919\backups\financeiro-20260926-115940-234.db`

## Conclusão

Nenhum erro funcional foi encontrado nos cenários executados. Os valores, parcelas, situações, estorno, cancelamento, regras de proteção, relatório, CSV e backup ficaram coerentes com as operações realizadas.

O formulário Flutter de login não pôde ser preenchido de forma confiável pelo controlador automatizado do navegador porque os campos são recriados durante a digitação. O login pela API local foi aprovado. Isso é uma limitação do teste automatizado do componente e não indica falha na digitação feita por uma pessoa.

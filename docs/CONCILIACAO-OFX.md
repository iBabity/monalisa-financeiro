# Baixa de contas a pagar pelo OFX

1. Cadastre a conta financeira do banco, por exemplo `ITAU`, usando o tipo
   **Conta bancária**.
2. Abra **A pagar** e selecione **Baixar pelo OFX**.
3. Escolha o arquivo `.ofx` exportado pelo banco e a conta financeira.
4. Confira as sugestões. O sistema compara valor, data, fornecedor e descrição.
5. Marque somente as movimentações corretas e selecione manualmente um
   lançamento quando não houver sugestão.
6. Confirme as baixas. O saldo da conta financeira, o histórico e a situação
   dos lançamentos serão atualizados normalmente.

Somente os débitos do extrato são tratados nesta primeira versão. Cada
movimentação é identificada pelo `FITID` do banco para impedir duplicidade. Um
valor menor gera baixa parcial. Quando o valor pago for maior que o saldo do
lançamento, a diferença aparece na conferência e é registrada como acréscimo.
O estorno libera a movimentação para uma nova conferência.

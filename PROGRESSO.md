# Entrega inicial — 21/09/2026

## Decisões confirmadas

- Produto independente, sem integração com o One.
- Flutter + C#, banco SQLite como no Pedido de Compra.
- Entradas e baixas manuais.
- Testar funcionalidades antes de definir acessos.
- Futuramente uma pessoa pode acessar vários CNPJs; não existe usuário administrador separado. Senha mestra diária será projetada depois.
- Marca e paleta da referência anexada pelo usuário.

## Validação realizada

- API C# compilada sem avisos ou erros.
- Flutter analyze sem apontamentos e dois testes de conversão monetária/situação aprovados.
- Onze verificações de integração aprovadas, além dos casos de rejeição de operações inválidas: previsto versus realizado; baixa parcial com ajustes; excesso; estorno; parcelamento e fim do mês; transferência; isolamento; cancelamento; pagamento; backup; reabertura do backup.
- Interface conferida no navegador local em 1280×720.
- Fluxo pela interface: despesa de R$ 100, baixa de R$ 40, confirmação de saldo de -R$ 40 e aberto de R$ 60, estorno e cancelamento com motivo. O título de validação foi preservado como cancelado; os saldos ficaram zerados.

## Limite da entrega

Primeira fatia funcional, não conclusão da lista completa. Pendências detalhadas no README. A máquina não tem Visual Studio/C++ para gerar o executável Flutter Windows; a versão de teste usa navegador local, com API e banco na máquina. SDK .NET 10 instalado em C:\Tools\dotnet.

import 'package:flutter_test/flutter_test.dart';
import 'package:app/finance.dart';

void main() {
  test('histórico antigo traduz baixa e ajustes sem expor JSON', () {
    final text = historyText(
      {
        'action': 'Baixa',
        'detail': '{"titleId":1,"settlementId":2,"input":{"AccountId":3,"Principal":10000,"Interest":100,"Penalty":0,"Discount":200,"Date":"2026-09-21"}}',
      },
      [
        {'id': 1, 'description': 'Aluguel', 'kind': 'payable'},
      ],
      [
        {'id': 3, 'name': 'Banco'},
      ],
      [],
    );
    expect(text, contains('Pagamento de'));
    expect(text, contains('99,00'));
    expect(text, contains('Aluguel'));
    expect(text, contains('Banco'));
    expect(text, contains('21/09/2026'));
    expect(text, contains('desconto'));
    expect(text, isNot(contains('AccountId')));
  });
  test('registro inválido não exibe conteúdo técnico', () {
    expect(
      historyText({'action': 'Baixa', 'detail': '{invalid'}, [], [], []),
      contains('não estão disponíveis'),
    );
  });
}

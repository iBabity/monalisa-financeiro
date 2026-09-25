import 'package:flutter_test/flutter_test.dart';
import 'package:app/finance.dart';

void main() {
  test('valores brasileiros preservam centavos sem ponto flutuante', () {
    expect(parseMoney('1.234,56'), 123456);
    expect(parseMoney('0,01'), 1);
    expect(parseMoney('-10,50'), -1050);
    expect(() => parseMoney('12.34'), throwsFormatException);
    expect(() => parseMoney('1,234'), throwsFormatException);
  });
  test('situação dá prioridade ao cancelamento e à liquidação', () {
    expect(status({'cancelled': 1, 'remaining': 200}), 'Cancelado');
    expect(status({'cancelled': 0, 'remaining': 0}), 'Pago');
    expect(
      status({
        'cancelled': 0,
        'remaining': 50,
        'settled': 50,
        'due': '2099-01-01',
      }),
      'Parcial',
    );
    expect(
      status({
        'cancelled': 0,
        'remaining': 50,
        'settled': 50,
        'due': '2000-01-01',
      }),
      'Vencido',
    );
  });
}

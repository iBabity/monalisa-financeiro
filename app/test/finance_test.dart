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

  test('repetição semanal mantém o valor e avança de sete em sete dias', () {
    expect(scheduledAmounts(5000, 4, repeatEach: true), [
      5000,
      5000,
      5000,
      5000,
    ]);
    expect(scheduledDues('2026-09-03', 4, intervalDays: 7), [
      '2026-09-03',
      '2026-09-10',
      '2026-09-17',
      '2026-09-24',
    ]);
  });

  test('repetição mensal usa o valor integral em cada lançamento', () {
    expect(repeatsEachTitle('monthly_repeat'), isTrue);
    expect(generatedSeriesTotal(6135, 13, 'monthly_repeat'), 79755);
    expect(scheduledAmounts(6135, 13, repeatEach: true), [
      6135,
      6135,
      6135,
      6135,
      6135,
      6135,
      6135,
      6135,
      6135,
      6135,
      6135,
      6135,
      6135,
    ]);
  });

  test('divisão mensal continua preservando somente o total informado', () {
    expect(repeatsEachTitle('monthly'), isFalse);
    expect(generatedSeriesTotal(6135, 13, 'monthly'), 6135);
  });

  test('selecionar todas considera somente contas abertas da lista', () {
    final selectable = selectableTitleIds([
      {'id': 1, 'cancelled': 0, 'remaining': 100},
      {'id': 2, 'cancelled': 0, 'remaining': 50},
      {'id': 3, 'cancelled': 1, 'remaining': 80},
      {'id': 4, 'cancelled': 0, 'remaining': 0},
    ]);
    expect(selectable, {1, 2});
    expect(selectAllState(selectable, {}), isFalse);
    expect(selectAllState(selectable, {1}), isNull);
    expect(selectAllState(selectable, {1, 2}), isTrue);
  });

  test('parcelas mensais preservam o total e ajustam fim do mês', () {
    expect(scheduledAmounts(10000, 3), [3334, 3333, 3333]);
    expect(scheduledDues('2026-01-31', 3), [
      '2026-01-31',
      '2026-02-28',
      '2026-03-31',
    ]);
  });

  test('repetição aceita intervalo personalizado em dias', () {
    expect(scheduledDues('2026-09-01', 3, intervalDays: 15), [
      '2026-09-01',
      '2026-09-16',
      '2026-10-01',
    ]);
    expect(
      () => scheduledDues('2026-09-01', 2, intervalDays: 366),
      throwsFormatException,
    );
  });
}

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:app/entry_form.dart';

void main() {
  for (final mode in ['yearly', 'recurring_yearly']) {
    testWidgets('prévia anual do cartão mantém valor integral: $mode', (
      tester,
    ) async {
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: EntryDialog(
              title: 'COMPRA NO CARTÃO',
              fields: [
                FormFieldSpec(
                  'amount',
                  'VALOR',
                  initial: '150,00',
                  money: true,
                ),
                FormFieldSpec(
                  'mode',
                  'TIPO',
                  initial: mode,
                  options: [MapEntry(mode, 'ANUAL')],
                ),
                FormFieldSpec('installments', 'QUANTIDADE', initial: '3'),
              ],
              save: (_) async {},
            ),
          ),
        ),
      );
      expect(
        find.textContaining(
          mode == 'yearly' ? '3 COBRANÇAS ANUAIS DE' : 'TODO ANO, ATÉ ENCERRAR',
        ),
        findsOneWidget,
      );
      expect(find.textContaining('R\$ 50,00'), findsNothing);
    });
  }
}

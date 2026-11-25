import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:app/entry_form.dart';

void main() {
  testWidgets(
    'cadastro rápido seleciona novo fornecedor e preserva o lançamento',
    (tester) async {
      Map<String, String>? saved;
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: EntryDialog(
              title: 'TESTE',
              fields: [
                FormFieldSpec(
                  'description',
                  'DESCRIÇÃO',
                  initial: 'COMPRA PREENCHIDA',
                ),
                FormFieldSpec(
                  'partyId',
                  'FORNECEDOR',
                  initial: '',
                  options: [const MapEntry('', 'SEM VÍNCULO')],
                  createLabel: 'NOVO FORNECEDOR',
                  createOption: (_) async =>
                      const MapEntry('42', 'FORNECEDOR NOVO'),
                ),
              ],
              save: (values) async => saved = values,
            ),
          ),
        ),
      );
      await tester.tap(find.text('NOVO FORNECEDOR'));
      await tester.pumpAndSettle();
      expect(find.text('COMPRA PREENCHIDA'), findsOneWidget);
      expect(find.text('FORNECEDOR NOVO'), findsOneWidget);
      await tester.tap(find.text('Salvar'));
      await tester.pumpAndSettle();
      expect(saved?['partyId'], '42');
      expect(saved?['description'], 'COMPRA PREENCHIDA');
    },
  );
  testWidgets('cancelar cadastro rápido não altera o texto digitado', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: EntryDialog(
            title: 'TESTE',
            fields: [
              FormFieldSpec(
                'partyName',
                'CLIENTE',
                initial: 'NOME DIGITADO',
                createLabel: 'NOVO CLIENTE',
                createOption: (_) async => null,
              ),
            ],
            save: (_) async {},
          ),
        ),
      ),
    );
    await tester.tap(find.text('NOVO CLIENTE'));
    await tester.pumpAndSettle();
    expect(find.text('NOME DIGITADO'), findsOneWidget);
  });
}

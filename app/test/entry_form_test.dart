import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:app/entry_form.dart';

void main() {
  test(
    'CNPJ aceita digitação, colagem e edição mantendo pontuação e cursor',
    () {
      final formatter = FriendlyInputFormatter('cnpj');
      var value = TextEditingValue.empty;
      for (final digit in '29775093000198'.split('')) {
        final text = value.text + digit;
        value = formatter.formatEditUpdate(
          value,
          TextEditingValue(
            text: text,
            selection: TextSelection.collapsed(offset: text.length),
          ),
        );
      }
      expect(value.text, '29.775.093/0001-98');
      expect(
        formatter
            .formatEditUpdate(
              TextEditingValue.empty,
              const TextEditingValue(
                text: '29.775.093/0001-98',
                selection: TextSelection.collapsed(offset: 18),
              ),
            )
            .text,
        value.text,
      );
      final edited = formatter.formatEditUpdate(
        value,
        const TextEditingValue(
          text: '29.775.093/0001-97',
          selection: TextSelection.collapsed(offset: 18),
        ),
      );
      expect(edited.text, '29.775.093/0001-97');
      expect(maskedDocument('12345678901'), '123.456.789-01');
      expect(maskedDocument('29775093000198'), '29.775.093/0001-98');
      final deletion = formatter.formatEditUpdate(
        const TextEditingValue(
          text: '29.775',
          selection: TextSelection.collapsed(offset: 3),
        ),
        const TextEditingValue(
          text: '29775',
          selection: TextSelection.collapsed(offset: 2),
        ),
      );
      expect(deletion.text, '27.75');
    },
  );
  test('datas brasileiras não normalizam datas impossíveis', () {
    expect(parseFriendlyDate('29/02/2024'), DateTime(2024, 2, 29));
    expect(parseFriendlyDate('29/02/2025'), isNull);
    expect(parseFriendlyDate('31/04/2026'), isNull);
    expect(parseFriendlyDate('2026-09-21'), isNull);
    expect(
      FriendlyInputFormatter('date')
          .formatEditUpdate(
            TextEditingValue.empty,
            const TextEditingValue(
              text: '21092026',
              selection: TextSelection.collapsed(offset: 8),
            ),
          )
          .text,
      '21/09/2026',
    );
  });

  test('data com ano de dois dígitos completa o século atual', () {
    expect(parseFriendlyDate('23/09/26'), DateTime(2026, 9, 23));
    expect(parseFriendlyDate('23/09/2026'), DateTime(2026, 9, 23));
  });
  testWidgets(
    'formulário converte data brasileira para API e mostra máscara do CNPJ',
    (tester) async {
      Map<String, String>? saved;
      await tester.pumpWidget(
        MaterialApp(
          home: Builder(
            builder: (context) => Scaffold(
              body: TextButton(
                onPressed: () => showDialog(
                  context: context,
                  builder: (_) => EntryDialog(
                    title: 'Empresa',
                    fields: [
                      FormFieldSpec(
                        'cnpj',
                        'CNPJ',
                        document: 'cnpj',
                        required: true,
                      ),
                      FormFieldSpec(
                        'date',
                        'Data',
                        date: true,
                        initial: '2026-09-21',
                      ),
                    ],
                    save: (v) async {
                      saved = v;
                    },
                  ),
                ),
                child: const Text('Abrir'),
              ),
            ),
          ),
        ),
      );
      await tester.tap(find.text('Abrir'));
      await tester.pumpAndSettle();
      await tester.enterText(
        find.byType(TextFormField).first,
        '29775093000198',
      );
      await tester.pump();
      expect(find.text('29.775.093/0001-98'), findsOneWidget);
      await tester.ensureVisible(find.text('Salvar'));
      await tester.tap(find.text('Salvar'));
      await tester.pumpAndSettle();
      expect(saved?['date'], '2026-09-21');
      expect(saved?['cnpj'], '29.775.093/0001-98');
    },
  );
  testWidgets('data opcional vazia permite salvar o formulário', (
    tester,
  ) async {
    Map<String, String>? saved;
    await tester.pumpWidget(
      MaterialApp(
        home: Builder(
          builder: (context) => Scaffold(
            body: TextButton(
              onPressed: () => showDialog(
                context: context,
                builder: (_) => EntryDialog(
                  title: 'Nova conta a receber',
                  fields: [
                    FormFieldSpec(
                      'issueDate',
                      'Data de emissão (opcional)',
                      date: true,
                    ),
                  ],
                  save: (values) async {
                    saved = values;
                  },
                ),
              ),
              child: const Text('Abrir'),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('Abrir'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Salvar'));
    await tester.pumpAndSettle();
    expect(saved?['issueDate'], '');
  });
  testWidgets('calendário permite escolher o dia sem alternar modos', (
    tester,
  ) async {
    DateTime? selected;
    await tester.pumpWidget(
      MaterialApp(
        home: Builder(
          builder: (context) => Scaffold(
            body: TextButton(
              onPressed: () async {
                selected = await showDialog<DateTime>(
                  context: context,
                  builder: (_) => SimpleCalendar(
                    initial: DateTime(2026, 9, 21),
                    last: DateTime(2100, 12, 31),
                  ),
                );
              },
              child: const Text('Abrir'),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('Abrir'));
    await tester.pumpAndSettle();
    expect(find.text('Setembro'), findsOneWidget);
    await tester.tap(find.text('25'));
    await tester.pumpAndSettle();
    expect(selected, DateTime(2026, 9, 25));
  });

  testWidgets('repetição semanal mostra quantidade e total gerado', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Builder(
          builder: (context) => Scaffold(
            body: TextButton(
              onPressed: () => showDialog(
                context: context,
                builder: (_) => EntryDialog(
                  title: 'Nova conta a pagar',
                  fields: [
                    FormFieldSpec(
                      'amount',
                      'Valor (R\$)',
                      money: true,
                      required: true,
                    ),
                    FormFieldSpec(
                      'repeatMode',
                      'Como deseja lançar esta despesa?',
                      initial: 'single',
                      options: const [
                        MapEntry('single', 'CONTA ÚNICA'),
                        MapEntry(
                          'weekly',
                          'REPETIR O MESMO VALOR A CADA 7 DIAS',
                        ),
                      ],
                    ),
                    FormFieldSpec(
                      'installments',
                      'Quantidade de lançamentos',
                      initial: '1',
                      visibleWhenKey: 'repeatMode',
                      visibleWhenValues: const {'weekly'},
                    ),
                  ],
                  save: (_) async {},
                ),
              ),
              child: const Text('Abrir'),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('Abrir'));
    await tester.pumpAndSettle();
    expect(find.text('Quantidade de lançamentos'), findsNothing);
    await tester.tap(find.text('CONTA ÚNICA'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('REPETIR O MESMO VALOR A CADA 7 DIAS').last);
    await tester.pumpAndSettle();
    expect(find.textContaining('Quantidade de lançamentos'), findsOneWidget);
    await tester.enterText(find.byType(TextFormField).first, '50');
    await tester.enterText(find.byType(TextFormField).last, '4');
    await tester.pump();
    expect(find.textContaining('Total gerado: R\$ 200,00'), findsOneWidget);
  });
}

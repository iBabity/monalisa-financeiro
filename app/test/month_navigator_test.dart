import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:app/month_navigator.dart';

void main() {
  testWidgets('permite escolher meses antigos e voltar ao mês atual', (
    tester,
  ) async {
    var value = '2026-08';
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: StatefulBuilder(
            builder: (context, update) => MonthNavigator(
              value: value,
              onChanged: (next) => update(() => value = next),
            ),
          ),
        ),
      ),
    );
    expect(find.text('AGOSTO DE 2026'), findsOneWidget);
    await tester.tap(find.byTooltip('ANO ANTERIOR'));
    await tester.pumpAndSettle();
    expect(value, '2025-08');
    await tester.tap(find.text('FEV'));
    await tester.pumpAndSettle();
    expect(value, '2025-02');
    expect(find.text('FEVEREIRO DE 2025'), findsOneWidget);
    await tester.tap(find.text('2025'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('2024'));
    await tester.pumpAndSettle();
    expect(value, '2024-02');
    await tester.tap(find.text('MÊS ATUAL'));
    await tester.pumpAndSettle();
    final now = DateTime.now();
    expect(value, '${now.year}-${now.month.toString().padLeft(2, '0')}');
  });
  testWidgets(
    'os doze meses ficam disponíveis no celular sem rolagem horizontal',
    (tester) async {
      tester.view.physicalSize = const Size(390, 844);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: Padding(
              padding: const EdgeInsets.all(16),
              child: MonthNavigator(value: '2026-11', onChanged: (_) {}),
            ),
          ),
        ),
      );
      for (final month in MonthNavigator.shortNames) {
        expect(find.text(month), findsOneWidget);
        final rect = tester.getRect(find.text(month));
        expect(rect.left, greaterThanOrEqualTo(0));
        expect(rect.right, lessThanOrEqualTo(390));
      }
      expect(tester.takeException(), isNull);
    },
  );
}

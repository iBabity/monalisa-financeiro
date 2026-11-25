import 'package:flutter/material.dart';

class MonthNavigator extends StatelessWidget {
  final String value;
  final ValueChanged<String> onChanged;
  const MonthNavigator({
    super.key,
    required this.value,
    required this.onChanged,
  });

  static const names = [
    'JANEIRO',
    'FEVEREIRO',
    'MARÇO',
    'ABRIL',
    'MAIO',
    'JUNHO',
    'JULHO',
    'AGOSTO',
    'SETEMBRO',
    'OUTUBRO',
    'NOVEMBRO',
    'DEZEMBRO',
  ];
  static const shortNames = [
    'JAN',
    'FEV',
    'MAR',
    'ABR',
    'MAI',
    'JUN',
    'JUL',
    'AGO',
    'SET',
    'OUT',
    'NOV',
    'DEZ',
  ];
  int get year => int.parse(value.substring(0, 4));
  int get month => int.parse(value.substring(5, 7));
  void select(int nextYear, int nextMonth) => onChanged(
    '${nextYear.toString().padLeft(4, '0')}-${nextMonth.toString().padLeft(2, '0')}',
  );

  Future<void> chooseYear(BuildContext context) async {
    final selected = await showDialog<int>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('ESCOLHER ANO'),
        content: SizedBox(
          width: 330,
          height: 330,
          child: YearPicker(
            firstDate: DateTime(1900),
            lastDate: DateTime(2200),
            selectedDate: DateTime(year, month),
            onChanged: (date) => Navigator.pop(dialogContext, date.year),
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext),
            child: const Text('VOLTAR'),
          ),
        ],
      ),
    );
    if (selected != null) select(selected, month);
  }

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    final now = DateTime.now();
    return Container(
      padding: const EdgeInsets.all(18),
      decoration: BoxDecoration(
        color: colors.surface,
        borderRadius: BorderRadius.circular(20),
        border: Border.all(color: colors.outlineVariant.withValues(alpha: .65)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Wrap(
            alignment: WrapAlignment.spaceBetween,
            crossAxisAlignment: WrapCrossAlignment.center,
            spacing: 16,
            runSpacing: 12,
            children: [
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Container(
                    padding: const EdgeInsets.all(10),
                    decoration: BoxDecoration(
                      color: colors.primary.withValues(alpha: .09),
                      borderRadius: BorderRadius.circular(13),
                    ),
                    child: Icon(
                      Icons.calendar_month_outlined,
                      color: colors.primary,
                      size: 23,
                    ),
                  ),
                  const SizedBox(width: 12),
                  Flexible(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          'PERÍODO DOS LANÇAMENTOS',
                          style: TextStyle(
                            fontSize: 11,
                            letterSpacing: .7,
                            color: colors.onSurfaceVariant,
                          ),
                        ),
                        const SizedBox(height: 3),
                        Text(
                          '${names[month - 1]} DE $year',
                          style: TextStyle(
                            fontSize: 17,
                            fontWeight: FontWeight.w700,
                            color: colors.onSurface,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
              Wrap(
                spacing: 10,
                runSpacing: 8,
                crossAxisAlignment: WrapCrossAlignment.center,
                children: [
                  Container(
                    decoration: BoxDecoration(
                      color: colors.surfaceContainerLow,
                      borderRadius: BorderRadius.circular(13),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        IconButton(
                          tooltip: 'ANO ANTERIOR',
                          onPressed: year > 1900
                              ? () => select(year - 1, month)
                              : null,
                          icon: const Icon(Icons.chevron_left),
                        ),
                        TextButton(
                          onPressed: () => chooseYear(context),
                          child: Text(
                            '$year',
                            style: const TextStyle(
                              fontSize: 16,
                              fontWeight: FontWeight.w700,
                            ),
                          ),
                        ),
                        IconButton(
                          tooltip: 'PRÓXIMO ANO',
                          onPressed: year < 2200
                              ? () => select(year + 1, month)
                              : null,
                          icon: const Icon(Icons.chevron_right),
                        ),
                      ],
                    ),
                  ),
                  TextButton.icon(
                    onPressed: () => select(now.year, now.month),
                    icon: const Icon(Icons.today_outlined, size: 18),
                    label: const Text('MÊS ATUAL'),
                  ),
                ],
              ),
            ],
          ),
          const SizedBox(height: 18),
          LayoutBuilder(
            builder: (context, constraints) {
              final columns = constraints.maxWidth >= 1056
                  ? 12
                  : constraints.maxWidth >= 560
                  ? 6
                  : 4;
              final width =
                  (constraints.maxWidth - (columns - 1) * 8) / columns;
              return Wrap(
                spacing: 8,
                runSpacing: 8,
                children: List.generate(12, (index) {
                  final selected = index + 1 == month;
                  return SizedBox(
                    width: width,
                    height: 46,
                    child: Semantics(
                      button: true,
                      selected: selected,
                      label: '${names[index]} DE $year',
                      child: Tooltip(
                        message: '${names[index]} DE $year',
                        child: Material(
                          color: selected
                              ? colors.primary
                              : colors.surfaceContainerLow,
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(12),
                            side: BorderSide(
                              color: selected
                                  ? colors.primary
                                  : colors.outlineVariant.withValues(
                                      alpha: .55,
                                    ),
                            ),
                          ),
                          clipBehavior: Clip.antiAlias,
                          child: InkWell(
                            onTap: () => select(year, index + 1),
                            child: Center(
                              child: Text(
                                shortNames[index],
                                style: TextStyle(
                                  fontSize: 14,
                                  fontWeight: selected
                                      ? FontWeight.w800
                                      : FontWeight.w500,
                                  color: selected
                                      ? colors.onPrimary
                                      : colors.onSurfaceVariant,
                                ),
                              ),
                            ),
                          ),
                        ),
                      ),
                    ),
                  );
                }),
              );
            },
          ),
        ],
      ),
    );
  }
}

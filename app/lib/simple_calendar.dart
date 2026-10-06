import 'package:flutter/material.dart';

import 'finance.dart';

const _teal = Color(0xFF073F43);

class SimpleCalendar extends StatefulWidget {
  final DateTime initial, last;
  const SimpleCalendar({super.key, required this.initial, required this.last});
  @override
  State<SimpleCalendar> createState() => _SimpleCalendarState();
}

class _SimpleCalendarState extends State<SimpleCalendar> {
  late DateTime month = DateTime(widget.initial.year, widget.initial.month);
  static const months = [
    'Janeiro',
    'Fevereiro',
    'Março',
    'Abril',
    'Maio',
    'Junho',
    'Julho',
    'Agosto',
    'Setembro',
    'Outubro',
    'Novembro',
    'Dezembro',
  ];
  bool allowed(DateTime date) =>
      date.year >= 2000 && !date.isAfter(widget.last);
  void changeMonth(DateTime date) {
    if (allowed(date)) setState(() => month = date);
  }

  @override
  Widget build(BuildContext context) {
    final offset = (month.weekday - 1) % 7;
    final days = DateUtils.getDaysInMonth(month.year, month.month);
    final today = DateUtils.dateOnly(DateTime.now());
    return AlertDialog(
      backgroundColor: Theme.of(context).colorScheme.surface,
      surfaceTintColor: Colors.transparent,
      title: const Text('Escolha a data'),
      content: SizedBox(
        width: 385,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Row(
              children: [
                IconButton(
                  tooltip: 'Mês anterior',
                  onPressed: month.year == 2000 && month.month == 1
                      ? null
                      : () =>
                            changeMonth(DateTime(month.year, month.month - 1)),
                  icon: const Icon(Icons.chevron_left),
                ),
                Expanded(
                  child: DropdownButtonHideUnderline(
                    child: DropdownButton<int>(
                      value: month.month,
                      isExpanded: true,
                      items: [
                        for (var m = 1; m <= 12; m++)
                          DropdownMenuItem(
                            value: m,
                            enabled: allowed(DateTime(month.year, m)),
                            child: Text(
                              months[m - 1],
                              style: const TextStyle(fontSize: 14),
                            ),
                          ),
                      ],
                      onChanged: (m) {
                        if (m != null) changeMonth(DateTime(month.year, m));
                      },
                    ),
                  ),
                ),
                DropdownButtonHideUnderline(
                  child: DropdownButton<int>(
                    value: month.year,
                    items: [
                      for (var y = 2000; y <= widget.last.year; y++)
                        DropdownMenuItem(
                          value: y,
                          child: Text(
                            '$y',
                            style: const TextStyle(fontSize: 14),
                          ),
                        ),
                    ],
                    onChanged: (y) {
                      if (y != null) {
                        changeMonth(
                          DateTime(
                            y,
                            y == widget.last.year &&
                                    month.month > widget.last.month
                                ? widget.last.month
                                : month.month,
                          ),
                        );
                      }
                    },
                  ),
                ),
                IconButton(
                  tooltip: 'Próximo mês',
                  onPressed: allowed(DateTime(month.year, month.month + 1))
                      ? () => changeMonth(DateTime(month.year, month.month + 1))
                      : null,
                  icon: const Icon(Icons.chevron_right),
                ),
              ],
            ),
            const SizedBox(height: 14),
            Row(
              children: [
                for (final label in [
                  'Seg',
                  'Ter',
                  'Qua',
                  'Qui',
                  'Sex',
                  'Sáb',
                  'Dom',
                ])
                  Expanded(
                    child: Center(
                      child: Text(
                        label,
                        style: TextStyle(
                          fontSize: 12,
                          color: Theme.of(context).colorScheme.onSurfaceVariant,
                        ),
                      ),
                    ),
                  ),
              ],
            ),
            const SizedBox(height: 8),
            for (var week = 0; week < ((offset + days) / 7).ceil(); week++)
              Row(
                children: [
                  for (var col = 0; col < 7; col++)
                    Expanded(
                      child: Builder(
                        builder: (context) {
                          final day = week * 7 + col - offset + 1;
                          if (day < 1 || day > days) {
                            return const SizedBox(height: 42);
                          }
                          final date = DateTime(month.year, month.month, day);
                          final selected = DateUtils.isSameDay(
                            date,
                            widget.initial,
                          );
                          return SizedBox(
                            height: 42,
                            child: TextButton(
                              style: TextButton.styleFrom(
                                padding: EdgeInsets.zero,
                                backgroundColor: selected ? _teal : null,
                                foregroundColor: selected
                                    ? Colors.white
                                    : _teal,
                                shape: RoundedRectangleBorder(
                                  borderRadius: BorderRadius.circular(9),
                                  side: DateUtils.isSameDay(date, today)
                                      ? const BorderSide(color: _teal)
                                      : BorderSide.none,
                                ),
                              ),
                              onPressed: allowed(date)
                                  ? () => Navigator.pop(context, date)
                                  : null,
                              child: Text(
                                '$day',
                                semanticsLabel: dateLabel(iso(date)),
                              ),
                            ),
                          );
                        },
                      ),
                    ),
                ],
              ),
            const SizedBox(height: 12),
            Text(
              'Clique no dia para escolher.',
              style: TextStyle(
                fontSize: 12,
                color: Theme.of(context).colorScheme.onSurfaceVariant,
              ),
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context),
          child: const Text('Voltar'),
        ),
        TextButton(
          onPressed: () => Navigator.pop(context, today),
          child: const Text('Usar hoje'),
        ),
      ],
    );
  }
}

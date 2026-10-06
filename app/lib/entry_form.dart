import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'finance.dart';
import 'form_support.dart';
import 'simple_calendar.dart';

export 'expense_conversation.dart';
export 'form_support.dart';
export 'simple_calendar.dart';

const _teal = Color(0xFF073F43);

class EntryDialog extends StatefulWidget {
  final String title;
  final List<FormFieldSpec> fields;
  final Future<void> Function(Map<String, String>) save;
  final String? note;
  const EntryDialog({
    super.key,
    required this.title,
    required this.fields,
    required this.save,
    this.note,
  });
  @override
  State<EntryDialog> createState() => _EntryDialogState();
}

class _EntryDialogState extends State<EntryDialog> {
  final key = GlobalKey<FormState>();
  late final controllers = {
    for (final f in widget.fields)
      f.keyName: TextEditingController(
        text: f.date && f.initial.isNotEmpty ? dateLabel(f.initial) : f.initial,
      ),
  };
  late final focusNodes = {
    for (final f in widget.fields) f.keyName: FocusNode(),
  };
  bool busy = false;
  bool showAdvanced = false;
  String? error;
  @override
  void dispose() {
    for (final c in controllers.values) {
      c.dispose();
    }
    for (final f in focusNodes.values) {
      f.dispose();
    }
    super.dispose();
  }

  Future<void> submit() async {
    if (busy || !key.currentState!.validate()) return;
    setState(() {
      busy = true;
      error = null;
    });
    try {
      final values = controllers.map((k, v) => MapEntry(k, v.text.trim()));
      for (final f in widget.fields.where((f) => f.date)) {
        final value = values[f.keyName]!;
        values[f.keyName] = value.isEmpty ? '' : iso(parseFriendlyDate(value)!);
      }
      await widget.save(values);
      if (mounted) Navigator.pop(context, true);
    } catch (e) {
      if (mounted) {
        setState(() {
          error = e.toString().replaceFirst('Exception: ', '');
          busy = false;
        });
      }
    }
  }

  String? validate(FormFieldSpec f, String? input) {
    final value = input?.trim() ?? '';
    if (f.required && value.isEmpty) {
      return 'Informe ${f.label.toLowerCase().replaceAll(' (r\$)', '')} para continuar.';
    }
    if (f.date && value.isNotEmpty) {
      final parsed = parseFriendlyDate(value);
      if (parsed == null || parsed.year < 2000 || parsed.year > 2100) {
        return 'Digite uma data válida, por exemplo 25/09/2026.';
      }
      if (f.pastOnly && parsed.isAfter(DateUtils.dateOnly(DateTime.now()))) {
        return 'Escolha hoje ou uma data anterior.';
      }
    }
    if (f.money) {
      try {
        final cents = parseMoney(value);
        if (f.keyName != 'opening' && cents < 0) {
          return 'Use um valor igual ou maior que zero.';
        }
        if (['amount', 'principal'].contains(f.keyName) && cents == 0) {
          return 'Informe um valor maior que zero.';
        }
      } catch (_) {
        return 'Use vírgula para os centavos. Exemplo: 1.250,50.';
      }
    }
    if (f.document != null && value.isNotEmpty) {
      final length = value.replaceAll(RegExp('[^A-Za-z0-9]'), '').length;
      if (f.document == 'cnpj' ? length != 14 : length != 11 && length != 14) {
        return f.document == 'cnpj'
            ? 'Complete os 14 caracteres do CNPJ.'
            : 'Complete o CPF (11 dígitos) ou o CNPJ (14 caracteres).';
      }
    }
    if (f.keyName == 'installments') {
      final count = int.tryParse(value);
      if (count == null || count < 1 || count > 120) {
        return 'Escolha de 1 a 120 parcelas.';
      }
    }
    if (f.keyName.endsWith('Rate')) {
      final rate = double.tryParse(value.replaceAll(',', '.'));
      if (rate == null || rate < 0 || rate > 100) {
        return 'Informe um percentual entre 0 e 100.';
      }
    }
    return null;
  }

  Future<void> pickDate(FormFieldSpec f) async {
    final today = DateUtils.dateOnly(DateTime.now());
    var selected = parseFriendlyDate(controllers[f.keyName]!.text) ?? today;
    final last = f.pastOnly ? today : DateTime(2100, 12, 31);
    if (selected.isAfter(last) || selected.year < 2000) selected = today;
    final value = await showDialog<DateTime>(
      context: context,
      builder: (_) => SimpleCalendar(initial: selected, last: last),
    );
    if (value != null && mounted) {
      setState(() => controllers[f.keyName]!.text = dateLabel(iso(value)));
    }
  }

  Widget field(FormFieldSpec f, int index) {
    final controller = controllers[f.keyName]!;
    final label = '${f.label}${f.required ? ' *' : ''}';
    if (f.options != null) {
      return DropdownButtonFormField<String>(
        initialValue: controller.text,
        isExpanded: true,
        decoration: InputDecoration(
          labelText: label,
          helperText: f.helper,
          helperMaxLines: 3,
        ),
        items: f.options!
            .map((o) => DropdownMenuItem(value: o.key, child: Text(o.value)))
            .toList(),
        onChanged: busy
            ? null
            : (v) => setState(() => controller.text = v ?? ''),
        autovalidateMode: AutovalidateMode.onUserInteraction,
        validator: (v) => validate(f, v),
      );
    }
    if (f.suggestions != null) {
      return RawAutocomplete<String>(
        textEditingController: controller,
        focusNode: focusNodes[f.keyName],
        optionsBuilder: (value) {
          final query = value.text.trim().toLowerCase();
          if (query.isEmpty) return f.suggestions!;
          return f.suggestions!.where(
            (item) => item.toLowerCase().contains(query),
          );
        },
        onSelected: (value) => setState(() => controller.text = value),
        fieldViewBuilder: (context, textController, focusNode, onSubmitted) =>
            TextFormField(
              controller: textController,
              focusNode: focusNode,
              decoration: InputDecoration(
                labelText: label,
                hintText: f.hint,
                helperText: f.helper,
                helperMaxLines: 3,
              ),
              validator: (v) => validate(f, v),
              onChanged: (_) => setState(() {}),
              inputFormatters: [UpperCaseTextFormatter()],
            ),
        optionsViewBuilder: (context, onSelected, values) => Align(
          alignment: Alignment.topLeft,
          child: Material(
            elevation: 6,
            borderRadius: BorderRadius.circular(10),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxHeight: 220, maxWidth: 430),
              child: ListView(
                padding: EdgeInsets.zero,
                shrinkWrap: true,
                children: values
                    .map(
                      (value) => ListTile(
                        title: Text(value),
                        onTap: () => onSelected(value),
                      ),
                    )
                    .toList(),
              ),
            ),
          ),
        ),
      );
    }
    final input = TextFormField(
      controller: controller,
      enabled: !busy,
      autofocus: index == 0,
      obscureText: f.secret,
      keyboardType: f.money
          ? const TextInputType.numberWithOptions(decimal: true, signed: true)
          : f.date ||
                f.secret ||
                f.keyName == 'installments' ||
                f.keyName.endsWith('Rate')
          ? TextInputType.number
          : TextInputType.text,
      textInputAction: TextInputAction.next,
      onChanged: (_) => setState(() {}),
      onTap: f.money
          ? () => controller.selection = TextSelection(
              baseOffset: 0,
              extentOffset: controller.text.length,
            )
          : null,
      onTapOutside: f.money
          ? (_) {
              try {
                controller.text = inputMoney(parseMoney(controller.text));
              } on FormatException {
                // Mantém a digitação para que o usuário possa corrigi-la.
              }
              FocusScope.of(context).unfocus();
              setState(() {});
            }
          : null,
      inputFormatters: [
        if (f.keyName != 'email') UpperCaseTextFormatter(),
        if (f.date) FriendlyInputFormatter('date'),
        if (f.document != null) FriendlyInputFormatter(f.document!),
        if (f.keyName == 'installments') FilteringTextInputFormatter.digitsOnly,
        if (f.secret) FilteringTextInputFormatter.digitsOnly,
        if (f.secret) LengthLimitingTextInputFormatter(12),
      ],
      maxLength: f.money || f.date || f.document != null ? null : 500,
      decoration: InputDecoration(
        labelText: label,
        hintText:
            f.hint ??
            (f.money
                ? 'Ex.: 1.250,00'
                : f.date
                ? 'dd/mm/aaaa'
                : null),
        floatingLabelBehavior: FloatingLabelBehavior.always,
        counterText: '',
        helperText:
            f.helper ??
            (f.date ? 'Digite a data ou use o calendário ao lado.' : null),
        helperMaxLines: 3,
        errorMaxLines: 2,
        prefixIcon: f.money
            ? const Icon(Icons.payments_outlined, size: 19)
            : f.document != null
            ? const Icon(Icons.badge_outlined, size: 19)
            : null,
        suffixIcon: f.date
            ? IconButton(
                tooltip: 'Escolher data no calendário',
                onPressed: busy ? null : () => pickDate(f),
                icon: const Icon(Icons.calendar_month_outlined),
              )
            : null,
      ),
      autovalidateMode: AutovalidateMode.onUserInteraction,
      validator: (v) => validate(f, v),
    );
    if (!f.date) return input;
    final today = DateUtils.dateOnly(DateTime.now());
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        input,
        const SizedBox(height: 8),
        Wrap(
          spacing: 8,
          children: [
            for (final quick in [
              MapEntry('Hoje', today),
              if (!f.pastOnly)
                MapEntry('Amanhã', today.add(const Duration(days: 1))),
              if (!f.pastOnly)
                MapEntry('Em 7 dias', today.add(const Duration(days: 7))),
            ])
              ActionChip(
                label: Text(quick.key, style: const TextStyle(fontSize: 12)),
                onPressed: busy
                    ? null
                    : () => setState(
                        () => controller.text = dateLabel(iso(quick.value)),
                      ),
              ),
          ],
        ),
      ],
    );
  }

  String? preview() {
    try {
      if (controllers.containsKey('installments')) {
        final repeatMode = controllers['repeatMode']?.text ?? 'monthly';
        final count = int.tryParse(controllers['installments']!.text) ?? 0;
        final amount = parseMoney(controllers['amount']!.text);
        if (repeatMode == 'single') return 'Uma conta de ${money(amount)}.';
        if (count < 1 || count > 120 || amount <= 0) return null;
        if (repeatsEachTitle(repeatMode)) {
          final interval = int.tryParse(
            controllers['intervalDays']?.text ?? '',
          );
          final cadence = repeatMode == 'monthly_repeat'
              ? 'todo mês'
              : repeatMode == 'weekly'
              ? 'a cada 7 dias'
              : 'a cada ${interval ?? 0} dias';
          return '$count lançamentos de ${money(amount)} $cadence. Total gerado: ${money(generatedSeriesTotal(amount, count, repeatMode))}.';
        }
        if (amount < count) return null;
        final base = amount ~/ count, remainder = amount % count;
        return count == 1
            ? 'Uma conta de ${money(amount)}.'
            : 'Total: ${money(amount)} em $count parcelas mensais. ${remainder == 0 ? 'Cada parcela: ${money(base)}.' : '$remainder de ${money(base + 1)} e ${count - remainder} de ${money(base)}.'}';
      }
      if (controllers.containsKey('principal')) {
        final amount =
            parseMoney(controllers['principal']!.text) +
            parseMoney(controllers['interest']!.text) +
            parseMoney(controllers['penalty']!.text) -
            parseMoney(controllers['discount']!.text);
        return 'Valor que movimentará a conta: ${money(amount)}.';
      }
    } catch (_) {
      return null;
    }
    return null;
  }

  bool visible(FormFieldSpec field) {
    if (field.visibleWhenKey == null) return true;
    final value = controllers[field.visibleWhenKey]?.text ?? '';
    return field.visibleWhenValues?.contains(value) ?? false;
  }

  @override
  Widget build(BuildContext context) {
    final summary = preview();
    return PopScope(
      canPop: !busy,
      child: AlertDialog(
        surfaceTintColor: Colors.transparent,
        title: Text(
          widget.title,
          style: const TextStyle(color: _teal, fontWeight: FontWeight.w700),
        ),
        content: SizedBox(
          width: 560,
          child: SingleChildScrollView(
            child: Form(
              key: key,
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  if (widget.note != null)
                    Container(
                      margin: const EdgeInsets.only(bottom: 16),
                      padding: const EdgeInsets.all(14),
                      decoration: BoxDecoration(
                        color: const Color(0xFFF1F5F1),
                        borderRadius: BorderRadius.circular(12),
                      ),
                      child: Text(
                        widget.note!,
                        style: const TextStyle(
                          color: _teal,
                          fontSize: 13,
                          height: 1.5,
                        ),
                      ),
                    ),
                  Padding(
                    padding: const EdgeInsets.only(bottom: 20),
                    child: Text(
                      '* Campos obrigatórios. Os demais são opcionais.',
                      style: TextStyle(
                        fontSize: 11,
                        color: Theme.of(context).colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ),
                  if (widget.fields.any((f) => f.advanced))
                    Padding(
                      padding: const EdgeInsets.only(bottom: 16),
                      child: OutlinedButton.icon(
                        onPressed: () =>
                            setState(() => showAdvanced = !showAdvanced),
                        icon: Icon(
                          showAdvanced ? Icons.expand_less : Icons.tune,
                        ),
                        label: Text(
                          showAdvanced
                              ? 'Ocultar ajustes opcionais'
                              : 'Juros, multa e desconto (opcional)',
                        ),
                      ),
                    ),
                  for (var i = 0; i < widget.fields.length; i++)
                    if (visible(widget.fields[i]) &&
                        (!widget.fields[i].advanced || showAdvanced))
                      Padding(
                        padding: const EdgeInsets.only(bottom: 22),
                        child: field(widget.fields[i], i),
                      ),
                  if (summary != null)
                    Container(
                      padding: const EdgeInsets.all(14),
                      decoration: BoxDecoration(
                        color: const Color(0xFFFFF2DE),
                        borderRadius: BorderRadius.circular(12),
                      ),
                      child: Text(
                        summary,
                        style: const TextStyle(
                          color: _teal,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                    ),
                  if (error != null)
                    Padding(
                      padding: const EdgeInsets.only(top: 16),
                      child: Text(
                        error!,
                        style: TextStyle(color: Colors.red.shade700),
                      ),
                    ),
                ],
              ),
            ),
          ),
        ),
        actions: [
          TextButton(
            onPressed: busy ? null : () => Navigator.pop(context),
            child: const Text('Voltar'),
          ),
          FilledButton(
            onPressed: busy ? null : submit,
            child: Text(
              busy
                  ? 'Salvando…'
                  : widget.title.startsWith('Cancelar')
                  ? 'Confirmar cancelamento'
                  : widget.title.startsWith('Registrar')
                  ? 'Confirmar registro'
                  : 'Salvar',
            ),
          ),
        ],
      ),
    );
  }
}

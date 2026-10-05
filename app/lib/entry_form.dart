import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'finance.dart';

const _teal = Color(0xFF073F43);

class UpperCaseTextFormatter extends TextInputFormatter {
  @override
  TextEditingValue formatEditUpdate(
    TextEditingValue oldValue,
    TextEditingValue newValue,
  ) => newValue.copyWith(text: newValue.text.toUpperCase());
}

String maskedDocument(String value, {bool cnpj = false}) {
  final raw = value.toUpperCase().replaceAll(RegExp('[^A-Z0-9]'), '');
  final pattern = cnpj || raw.length > 11 || RegExp('[A-Z]').hasMatch(raw)
      ? '##.###.###/####-##'
      : '###.###.###-##';
  return applyMask(raw, pattern);
}

String applyMask(String raw, String pattern) {
  var i = 0;
  final result = StringBuffer();
  for (final char in pattern.split('')) {
    if (i >= raw.length) break;
    if (char == '#') {
      result.write(raw[i++]);
    } else {
      result.write(char);
    }
  }
  return result.toString();
}

class FriendlyInputFormatter extends TextInputFormatter {
  final String kind;
  FriendlyInputFormatter(this.kind);
  @override
  TextEditingValue formatEditUpdate(
    TextEditingValue oldValue,
    TextEditingValue newValue,
  ) {
    final valid = kind == 'date' ? RegExp('[0-9]') : RegExp('[a-zA-Z0-9]');
    var text = newValue.text;
    var caret = newValue.selection.extentOffset.clamp(0, text.length);
    // Backspace over a separator removes the preceding digit rather than trapping the caret.
    if (oldValue.selection.isCollapsed &&
        text.length == oldValue.text.length - 1 &&
        caret < oldValue.text.length &&
        !valid.hasMatch(oldValue.text[caret]) &&
        caret > 0) {
      text = text.replaceRange(caret - 1, caret, '');
      caret--;
    }
    final rawBefore = valid.allMatches(text.substring(0, caret)).length;
    final raw = valid.allMatches(text).map((m) => m.group(0)!).join();
    final formatted = kind == 'date'
        ? applyMask(raw, raw.length <= 6 ? '##/##/##' : '##/##/####')
        : maskedDocument(raw, cnpj: kind == 'cnpj');
    var offset = 0, count = 0;
    while (offset < formatted.length && count < rawBefore) {
      if (valid.hasMatch(formatted[offset])) count++;
      offset++;
    }
    return TextEditingValue(
      text: formatted,
      selection: TextSelection.collapsed(offset: offset),
    );
  }
}

DateTime? parseFriendlyDate(String text) {
  if (!RegExp(r'^\d{2}/\d{2}/(?:\d{2}|\d{4})$').hasMatch(text)) return null;
  final rawParts = text.split('/');
  final parts = rawParts.map(int.parse).toList();
  final year = rawParts[2].length == 2 ? 2000 + parts[2] : parts[2];
  final value = DateTime(year, parts[1], parts[0]);
  return value.year == year && value.month == parts[1] && value.day == parts[0]
      ? value
      : null;
}

class FormFieldSpec {
  final String keyName, label, initial;
  final String? hint, helper, document, visibleWhenKey;
  final bool required, date, money, pastOnly, advanced, secret;
  final List<MapEntry<String, String>>? options;
  final List<String>? suggestions;
  final Set<String>? visibleWhenValues;
  FormFieldSpec(
    this.keyName,
    this.label, {
    this.initial = '',
    this.required = false,
    this.date = false,
    this.money = false,
    this.options,
    this.suggestions,
    this.hint,
    this.helper,
    this.document,
    this.visibleWhenKey,
    this.visibleWhenValues,
    this.pastOnly = false,
    this.advanced = false,
    this.secret = false,
  });
}

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
              } catch (_) {}
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

class ExpenseConversationDialog extends StatefulWidget {
  final bool receivable;
  final Future<void> Function(String description, int amount, String due) save;
  const ExpenseConversationDialog({
    super.key,
    required this.save,
    this.receivable = false,
  });

  @override
  State<ExpenseConversationDialog> createState() =>
      _ExpenseConversationDialogState();
}

class _ExpenseConversationDialogState extends State<ExpenseConversationDialog> {
  final controller = TextEditingController();
  final focus = FocusNode();
  int step = 0;
  String description = '';
  int amount = 0;
  String due = '';
  String? error;
  bool busy = false;

  List<String> get questions => [
    widget.receivable
        ? 'Olá! O que você vai receber?'
        : 'Olá! O que você precisa pagar?',
    widget.receivable
        ? 'Certo. Qual é o valor desse recebimento?'
        : 'Certo. Qual é o valor dessa despesa?',
    'E qual é a data de vencimento?',
  ];

  @override
  void dispose() {
    controller.dispose();
    focus.dispose();
    super.dispose();
  }

  String answerFor(int index) => switch (index) {
    0 => description,
    1 => money(amount),
    _ => dateLabel(due),
  };

  void next() {
    final value = controller.text.trim();
    String? validation;
    if (step == 0) {
      if (value.isEmpty) {
        validation = 'Conte em poucas palavras o que será pago.';
      } else {
        description = value.toUpperCase();
      }
    } else if (step == 1) {
      try {
        amount = parseMoney(value);
        if (amount <= 0) validation = 'Informe um valor maior que zero.';
      } catch (_) {
        validation = 'Digite o valor assim: 1.250,00.';
      }
    } else {
      final parsed = parseFriendlyDate(value);
      if (parsed == null || parsed.year < 2000 || parsed.year > 2100) {
        validation = 'Digite uma data válida, por exemplo 25/09/2026.';
      } else {
        due = iso(parsed);
      }
    }
    if (validation != null) {
      setState(() => error = validation);
      return;
    }
    setState(() {
      step++;
      error = null;
      controller.clear();
    });
    WidgetsBinding.instance.addPostFrameCallback((_) => focus.requestFocus());
  }

  Future<void> confirm() async {
    if (busy) return;
    setState(() {
      busy = true;
      error = null;
    });
    try {
      await widget.save(description, amount, due);
      if (mounted) Navigator.pop(context, true);
    } catch (e) {
      if (mounted) {
        setState(() {
          busy = false;
          error = e.toString().replaceFirst('Exception: ', '');
        });
      }
    }
  }

  void previous() {
    if (step == 0 || busy) return;
    setState(() {
      step--;
      error = null;
      controller.text = step == 0
          ? description
          : step == 1
          ? inputMoney(amount)
          : dateLabel(due);
    });
    focus.requestFocus();
  }

  Future<void> pickDueDate() async {
    final typed = parseFriendlyDate(controller.text);
    final picked = await showDatePicker(
      context: context,
      initialDate: typed ?? DateTime.now(),
      firstDate: DateTime(2000),
      lastDate: DateTime(2100),
      helpText: 'VENCIMENTO',
      cancelText: 'VOLTAR',
      confirmText: 'ESCOLHER',
    );
    if (picked == null || !mounted) return;
    setState(() => controller.text = dateLabel(iso(picked)));
    focus.requestFocus();
  }

  Widget bubble(String text, {required bool user}) => Align(
    alignment: user ? Alignment.centerRight : Alignment.centerLeft,
    child: Container(
      constraints: const BoxConstraints(maxWidth: 390),
      margin: const EdgeInsets.only(bottom: 10),
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      decoration: BoxDecoration(
        color: user
            ? Theme.of(context).colorScheme.primary
            : Theme.of(context).colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.only(
          topLeft: const Radius.circular(18),
          topRight: const Radius.circular(18),
          bottomLeft: Radius.circular(user ? 18 : 4),
          bottomRight: Radius.circular(user ? 4 : 18),
        ),
      ),
      child: Text(
        text,
        style: TextStyle(
          color: user
              ? Theme.of(context).colorScheme.onPrimary
              : Theme.of(context).colorScheme.onSurface,
          height: 1.35,
        ),
      ),
    ),
  );

  @override
  Widget build(BuildContext context) {
    final complete = step == questions.length;
    return PopScope(
      canPop: !busy,
      child: AlertDialog(
        surfaceTintColor: Colors.transparent,
        title: Row(
          children: [
            const CircleAvatar(
              backgroundColor: _teal,
              foregroundColor: Colors.white,
              child: Icon(Icons.auto_awesome_outlined, size: 19),
            ),
            const SizedBox(width: 12),
            Text(
              widget.receivable
                  ? 'Criar recebimento por conversa'
                  : 'Criar despesa por conversa',
            ),
          ],
        ),
        content: SizedBox(
          width: 540,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              ConstrainedBox(
                constraints: const BoxConstraints(maxHeight: 390),
                child: SingleChildScrollView(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      for (var i = 0; i < step; i++) ...[
                        bubble(questions[i], user: false),
                        bubble(answerFor(i), user: true),
                      ],
                      if (!complete) bubble(questions[step], user: false),
                      if (complete)
                        bubble(
                          'Tudo certo! Vou criar ${widget.receivable ? 'um recebimento' : 'uma despesa'} de ${money(amount)}, com vencimento em ${dateLabel(due)}. Confira antes de confirmar.',
                          user: false,
                        ),
                    ],
                  ),
                ),
              ),
              const SizedBox(height: 10),
              if (!complete) ...[
                TextField(
                  controller: controller,
                  focusNode: focus,
                  autofocus: true,
                  textCapitalization: step == 0
                      ? TextCapitalization.characters
                      : TextCapitalization.none,
                  keyboardType: step == 0
                      ? TextInputType.text
                      : TextInputType.number,
                  inputFormatters: [
                    if (step == 0) UpperCaseTextFormatter(),
                    if (step == 2) FriendlyInputFormatter('date'),
                  ],
                  decoration: InputDecoration(
                    hintText: step == 0
                        ? null
                        : step == 1
                        ? 'Digite o valor'
                        : 'dd/mm/aaaa',
                    suffixIcon: step == 2
                        ? Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              IconButton(
                                tooltip: 'Escolher data no calendário',
                                onPressed: pickDueDate,
                                icon: const Icon(Icons.calendar_month_outlined),
                              ),
                              IconButton(
                                tooltip: 'Enviar resposta',
                                onPressed: next,
                                icon: const Icon(Icons.send_rounded),
                              ),
                            ],
                          )
                        : IconButton(
                            tooltip: 'Enviar resposta',
                            onPressed: next,
                            icon: const Icon(Icons.send_rounded),
                          ),
                    suffixIconConstraints: step == 2
                        ? const BoxConstraints(minWidth: 96)
                        : null,
                  ),
                  onSubmitted: (_) => next(),
                ),
                if (step == 2)
                  Padding(
                    padding: const EdgeInsets.only(top: 9),
                    child: Wrap(
                      spacing: 8,
                      children: [
                        for (final option in [
                          MapEntry('Hoje', DateTime.now()),
                          MapEntry(
                            'Amanhã',
                            DateTime.now().add(const Duration(days: 1)),
                          ),
                          MapEntry(
                            'Em 7 dias',
                            DateTime.now().add(const Duration(days: 7)),
                          ),
                        ])
                          ActionChip(
                            label: Text(option.key),
                            onPressed: () => setState(
                              () => controller.text = dateLabel(
                                iso(option.value),
                              ),
                            ),
                          ),
                      ],
                    ),
                  ),
              ],
              if (error != null)
                Padding(
                  padding: const EdgeInsets.only(top: 10),
                  child: Text(
                    error!,
                    style: const TextStyle(color: Colors.red),
                  ),
                ),
            ],
          ),
        ),
        actions: [
          TextButton(
            onPressed: busy
                ? null
                : step > 0
                ? previous
                : () => Navigator.pop(context),
            child: Text(step > 0 ? 'Voltar uma pergunta' : 'Fechar'),
          ),
          if (!complete)
            FilledButton.icon(
              onPressed: next,
              icon: const Icon(Icons.arrow_forward, size: 18),
              label: const Text('Responder'),
            )
          else
            FilledButton.icon(
              onPressed: busy ? null : confirm,
              icon: const Icon(Icons.check, size: 18),
              label: Text(busy ? 'Criando…' : 'Confirmar e criar'),
            ),
        ],
      ),
    );
  }
}

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

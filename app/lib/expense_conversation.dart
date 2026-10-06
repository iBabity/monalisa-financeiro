import 'package:flutter/material.dart';

import 'finance.dart';
import 'form_support.dart';

const _teal = Color(0xFF073F43);

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

import 'package:flutter/services.dart';

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

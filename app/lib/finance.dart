import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;
import 'package:intl/intl.dart';

typedef RecordData = Map<String, dynamic>;
const apiBase = String.fromEnvironment(
  'API_URL',
  defaultValue: 'http://127.0.0.1:5187',
);
String money(num cents) =>
    NumberFormat.currency(locale: 'pt_BR', symbol: 'R\$').format(cents / 100);
String iso(DateTime d) => DateFormat('yyyy-MM-dd').format(d);
String dateLabel(String d) =>
    DateFormat('dd/MM/yyyy').format(DateTime.parse(d));
String cnpjLabel(Object? value) {
  final original = (value ?? '').toString();
  final raw = original.replaceAll(RegExp(r'[^A-Za-z0-9]'), '');
  if (raw.length != 14) return original;
  return '${raw.substring(0, 2)}.${raw.substring(2, 5)}.${raw.substring(5, 8)}/${raw.substring(8, 12)}-${raw.substring(12)}';
}

int parseMoney(String text) {
  final value = text.trim().replaceAll('R\$', '').replaceAll(' ', '');
  if (!RegExp(r'^-?(?:\d+|\d{1,3}(?:\.\d{3})+)(?:,\d{1,2})?$')
      .hasMatch(value)) {
    throw const FormatException('Informe o valor no formato 1.234,56.');
  }
  final parts = value.replaceAll('.', '').replaceAll('-', '').split(',');
  return (int.parse(parts[0]) * 100 +
          int.parse(parts.length == 1 ? '0' : parts[1].padRight(2, '0'))) *
      (value.startsWith('-') ? -1 : 1);
}

String inputMoney(num cents) =>
    (cents / 100).toStringAsFixed(2).replaceAll('.', ',');
String status(RecordData t) {
  if (t['cancelled'] == 1) return 'Cancelado';
  if (t['remaining'] == 0) return 'Pago';
  if ((t['due'] as String).compareTo(iso(DateTime.now())) < 0) return 'Vencido';
  if (t['settled'] > 0) return 'Parcial';
  return 'Em aberto';
}

String historyDate(String value) {
  final parsed = DateTime.tryParse('${value.replaceFirst(' ', 'T')}Z');
  return parsed == null
      ? 'Data não disponível'
      : DateFormat('dd/MM/yyyy • HH:mm').format(parsed.toLocal());
}

String historyText(
  RecordData event,
  List<RecordData> titles,
  List<RecordData> accounts,
  List<RecordData> settlements,
) {
  RecordData? find(List<RecordData> rows, dynamic id) {
    for (final row in rows) {
      if (row['id'] == id) return row;
    }
    return null;
  }

  String account(dynamic id) =>
      find(accounts, id)?['name'] ?? 'conta financeira';
  String title(dynamic id) => find(titles, id)?['description'] ?? 'lançamento';
  try {
    final decoded = jsonDecode(event['detail'] as String);
    if (event['action'] == 'Cadastro de pessoa' && decoded is String) {
      return 'Cadastro de "$decoded" criado.';
    }
    if (decoded is! Map) return 'Operação registrada no sistema.';
    final d = Map<String, dynamic>.from(decoded);
    switch (event['action']) {
      case 'Cadastro de conta':
        return 'Conta "${d['Name']}" cadastrada com saldo inicial de ${money(d['Opening'])}.';
      case 'Lançamento':
        final count = d['Installments'] as int? ?? 1;
        return '${d['Kind'] == 'payable' ? 'Conta a pagar' : 'Conta a receber'} "${d['Description']}" criada no valor ${count > 1 ? 'total ' : ''}de ${money(d['Amount'])}. ${count > 1 ? '$count parcelas mensais, com primeiro vencimento' : 'Vencimento'} em ${dateLabel(d['Due'])}.';
      case 'Baixa':
        final input = Map<String, dynamic>.from(d['input']);
        final payable = find(titles, d['titleId'])?['kind'] == 'payable';
        final cash =
            input['Principal'] +
            input['Interest'] +
            input['Penalty'] -
            input['Discount'];
        final adjustments = <String>[
          if (input['Interest'] > 0) 'juros de ${money(input['Interest'])}',
          if (input['Penalty'] > 0) 'multa de ${money(input['Penalty'])}',
          if (input['Discount'] > 0) 'desconto de ${money(input['Discount'])}',
        ];
        return '${payable ? 'Pagamento' : 'Recebimento'} de ${money(cash)} registrado para "${title(d['titleId'])}" em ${dateLabel(input['Date'])}, na conta "${account(input['AccountId'])}". Valor do título liquidado: ${money(input['Principal'])}.${adjustments.isEmpty ? '' : ' Ajustes: ${adjustments.join(', ')}.'}';
      case 'Estorno':
        final settlement = find(settlements, d['settlementId']);
        final label = settlement == null
            ? 'lançamento'
            : title(settlement['titleId']);
        return 'A baixa de "$label" foi desfeita e o movimento da conta foi revertido. Motivo: ${d['reason']}';
      case 'Cancelamento':
        return 'O lançamento "${title(d['titleId'])}" foi cancelado e deixou de fazer parte dos valores em aberto. Motivo: ${d['reason']}';
      case 'Edição':
        return 'O lançamento "${d['description']}" foi atualizado para ${money(d['amount'])}, com vencimento em ${dateLabel(d['due'])}.';
      case 'Exclusão':
        return 'O lançamento "${d['description']}" foi excluído da lista. Motivo: ${d['reason']}';
      case 'Transferência':
        return '${money(d['Amount'])} transferidos da conta "${account(d['FromId'])}" para "${account(d['ToId'])}" em ${dateLabel(d['Date'])}.';
      default:
        return 'Operação registrada no sistema.';
    }
  } catch (_) {
    return 'Operação registrada. Os detalhes deste registro antigo não estão disponíveis para exibição.';
  }
}

class FinanceApi {
  final http.Client client = http.Client();
  String? token;
  FinanceApi({this.token});
  Future<dynamic> request(String path, [RecordData? data]) async {
    final uri = Uri.parse('${kIsWeb ? Uri.base.origin : apiBase}/api$path');
    final headers = <String, String>{
      if (data != null) 'Content-Type': 'application/json',
      if (token != null) 'Authorization': 'Bearer $token',
    };
    final response =
        await (data == null
                ? client.get(uri, headers: headers)
                : client.post(uri, headers: headers, body: jsonEncode(data)))
            .timeout(const Duration(seconds: 15));
    dynamic body;
    try {
      body = jsonDecode(utf8.decode(response.bodyBytes));
    } catch (_) {
      throw Exception('O serviço local não respondeu corretamente.');
    }
    if (response.statusCode >= 400) {
      throw Exception(
        body is Map
            ? body['error'] ?? 'Operação recusada.'
            : 'Operação recusada.',
      );
    }
    return body;
  }
}

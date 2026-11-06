import 'package:flutter_test/flutter_test.dart';
import 'package:app/finance.dart';

void main() {
  test('valores móveis aceitam ponto sem alterar milhares brasileiros', () {
    expect(parseMoney('78.50'), 7850);
    expect(parseMoney('78.5'), 7850);
    expect(parseMoney('78,50'), 7850);
    expect(parseMoney('1.250'), 125000);
    expect(parseMoney('1.250,50'), 125050);
    expect(parseMoney('1250.50'), 125050);
    expect(parseMoney('-78.50'), -7850);
    expect(() => parseMoney('78.5012'), throwsFormatException);
  });
}

import 'dart:io';
import 'dart:typed_data';

Future<void> openGeneratedPdf(
  Uint8List bytes,
  String fileName, {
  String? savedPath,
}) async {
  if (savedPath == null || savedPath.trim().isEmpty) {
    throw ArgumentError('O arquivo precisa ser salvo antes de ser aberto.');
  }
  if (Platform.isWindows) {
    await Process.start('explorer.exe', [savedPath]);
    return;
  }
  await Process.start('xdg-open', [savedPath]);
}

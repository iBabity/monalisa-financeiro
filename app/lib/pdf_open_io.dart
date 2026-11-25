import 'dart:io';
import 'dart:typed_data';

class PdfPreviewSession {
  Future<void> show(Uint8List bytes, String fileName) async {
    final directory = await Directory.systemTemp.createTemp('monalisa-report-');
    final file = File('${directory.path}${Platform.pathSeparator}$fileName');
    await file.writeAsBytes(bytes);
    await openGeneratedPdf(bytes, fileName, savedPath: file.path);
  }

  void close() {}
}

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

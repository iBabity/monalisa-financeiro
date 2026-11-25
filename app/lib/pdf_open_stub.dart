import 'dart:typed_data';

class PdfPreviewSession {
  Future<void> show(Uint8List bytes, String fileName) async {
    throw UnsupportedError('PRÉVIA INDISPONÍVEL NESTE DISPOSITIVO.');
  }

  void close() {}
}

Future<void> openGeneratedPdf(
  Uint8List bytes,
  String fileName, {
  String? savedPath,
}) async {
  throw UnsupportedError('Não foi possível abrir o PDF neste dispositivo.');
}

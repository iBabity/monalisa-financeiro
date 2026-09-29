import 'dart:typed_data';

Future<void> saveBrowserDownload(
  Uint8List bytes,
  String fileName,
  String mimeType,
) async {
  throw UnsupportedError('Download pelo navegador indisponível.');
}

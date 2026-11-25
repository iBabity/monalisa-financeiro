// ignore_for_file: avoid_web_libraries_in_flutter, deprecated_member_use

import 'dart:html' as html;
import 'dart:typed_data';

// Reserve a tab during the click, before the authenticated request starts.
class PdfPreviewSession {
  final html.WindowBase _window = html.window.open(
    'report-preview.html',
    '_blank',
  );

  Future<void> show(Uint8List bytes, String fileName) async {
    if (_window.closed == true) {
      throw StateError('A PRÉVIA FOI FECHADA. CLIQUE EM VISUALIZAR NOVAMENTE.');
    }
    final url = html.Url.createObjectUrlFromBlob(
      html.Blob(<Object>[bytes], 'application/pdf'),
    );
    _window.location.href = url;
    Future<void>.delayed(
      const Duration(minutes: 10),
      () => html.Url.revokeObjectUrl(url),
    );
  }

  void close() => _window.close();
}

Future<void> openGeneratedPdf(
  Uint8List bytes,
  String fileName, {
  String? savedPath,
}) async {
  final blob = html.Blob(<Object>[bytes], 'application/pdf');
  final url = html.Url.createObjectUrlFromBlob(blob);
  html.window.open(url, '_blank');
  Future<void>.delayed(
    const Duration(minutes: 1),
    () => html.Url.revokeObjectUrl(url),
  );
}

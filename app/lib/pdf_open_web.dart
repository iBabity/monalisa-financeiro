// ignore_for_file: avoid_web_libraries_in_flutter, deprecated_member_use

import 'dart:html' as html;
import 'dart:typed_data';

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

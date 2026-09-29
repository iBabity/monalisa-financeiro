export 'pdf_open_stub.dart'
    if (dart.library.io) 'pdf_open_io.dart'
    if (dart.library.html) 'pdf_open_web.dart';

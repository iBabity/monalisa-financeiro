// ignore_for_file: avoid_web_libraries_in_flutter, deprecated_member_use
import 'dart:html' as html;

const _key = 'monalisa.daily-session';
String? readDailySession() {
  try {
    return html.window.localStorage[_key];
  } catch (_) {
    return null;
  }
}

void writeDailySession(String? token) {
  try {
    if (token == null) {
      html.window.localStorage.remove(_key);
    } else {
      html.window.localStorage[_key] = token;
    }
  } catch (_) {
    /* Login remains available if browser storage is disabled. */
  }
}

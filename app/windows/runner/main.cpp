#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <windows.h>
#include <winhttp.h>
#include <bcrypt.h>
#include <shellapi.h>
#include <commctrl.h>

#include <chrono>
#include <algorithm>
#include <array>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iomanip>
#include <regex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include "flutter_window.h"
#include "resource.h"
#include "utils.h"

namespace {

std::filesystem::path ExecutableDirectory() {
  std::vector<wchar_t> buffer(32768);
  const DWORD length = GetModuleFileNameW(nullptr, buffer.data(),
                                          static_cast<DWORD>(buffer.size()));
  return std::filesystem::path(std::wstring(buffer.data(), length)).parent_path();
}

std::filesystem::path ProgramDataDirectory() {
  std::vector<wchar_t> buffer(32768);
  const DWORD length = GetEnvironmentVariableW(
      L"ProgramData", buffer.data(), static_cast<DWORD>(buffer.size()));
  if (length == 0 || length >= static_cast<DWORD>(buffer.size())) {
    return std::filesystem::path(L"C:\\ProgramData") / L"Monalisa Financeiro";
  }
  return std::filesystem::path(std::wstring(buffer.data(), length)) /
         L"Monalisa Financeiro";
}

std::wstring EnvironmentValue(const wchar_t* name) {
  std::vector<wchar_t> buffer(32768);
  const DWORD length = GetEnvironmentVariableW(
      name, buffer.data(), static_cast<DWORD>(buffer.size()));
  if (length == 0 || length >= static_cast<DWORD>(buffer.size())) return L"";
  return std::wstring(buffer.data(), length);
}

std::wstring EncodeVersionForQuery(const std::wstring& value) {
  std::wstring encoded;
  encoded.reserve(value.size() + 4);
  for (const wchar_t character : value) {
    if (character == L'+') {
      encoded.append(L"%2B");
    } else {
      encoded.push_back(character);
    }
  }
  return encoded;
}

std::wstring ReadLicenseServer(const std::filesystem::path& path) {
  std::ifstream input(path, std::ios::binary);
  if (!input) return L"https://licencas.monalisasistemas.com.br";
  std::string value((std::istreambuf_iterator<char>(input)),
                    std::istreambuf_iterator<char>());
  while (!value.empty() && (value.back() == '\r' || value.back() == '\n' ||
                            value.back() == ' ' || value.back() == '\t')) {
    value.pop_back();
  }
  return std::wstring(value.begin(), value.end());
}

std::wstring ReadConfigurationValue(const std::filesystem::path& path,
                                    const std::wstring& fallback = L"") {
  std::ifstream input(path, std::ios::binary);
  if (!input) return fallback;
  std::string value((std::istreambuf_iterator<char>(input)),
                    std::istreambuf_iterator<char>());
  while (!value.empty() &&
         (value.back() == '\r' || value.back() == '\n' ||
          value.back() == ' ' || value.back() == '\t')) {
    value.pop_back();
  }
  while (!value.empty() &&
         (value.front() == ' ' || value.front() == '\t')) {
    value.erase(value.begin());
  }
  return std::wstring(value.begin(), value.end());
}

struct WebAddress {
  std::wstring host;
  std::wstring path;
  INTERNET_PORT port = 0;
  bool secure = false;
};

class UpdateProgressWindow {
 public:
  explicit UpdateProgressWindow(const std::wstring& version) {
    version_ = version;
    const HINSTANCE instance = GetModuleHandleW(nullptr);
    WNDCLASSEXW window_class{};
    window_class.cbSize = sizeof(window_class);
    window_class.style = CS_HREDRAW | CS_VREDRAW;
    window_class.lpfnWndProc = WindowProcedure;
    window_class.hInstance = instance;
    window_class.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(IDI_APP_ICON));
    window_class.hIconSm = window_class.hIcon;
    window_class.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    window_class.lpszClassName = L"MonalisaFinanceiroUpdateWindow";
    RegisterClassExW(&window_class);

    const int width = 650;
    const int height = 420;
    RECT work_area{};
    SystemParametersInfoW(SPI_GETWORKAREA, 0, &work_area, 0);
    const int left = work_area.left + (work_area.right - work_area.left - width) / 2;
    const int top = work_area.top + (work_area.bottom - work_area.top - height) / 2;
    window_ = CreateWindowExW(
        WS_EX_TOPMOST | WS_EX_DLGMODALFRAME, window_class.lpszClassName,
        L"MONALISA FINANCEIRO - ATUALIZAÇÃO", WS_OVERLAPPED | WS_CAPTION,
        left, top, width, height, nullptr, nullptr, instance, this);
    if (!window_) return;
    ShowWindow(window_, SW_SHOWNORMAL);
    SetForegroundWindow(window_);
    PumpMessages();
  }

  ~UpdateProgressWindow() {
    if (window_ && IsWindow(window_)) DestroyWindow(window_);
  }

  void SetStatus(const std::wstring& text, int percent) {
    if (!window_ || !IsWindow(window_)) return;
    status_ = text;
    percent_ = percent >= 0 ? std::clamp(percent, 0, 100) : percent_;
    if (text.find(L"SALVANDO") != std::wstring::npos ||
        text.find(L"VERIFICANDO") != std::wstring::npos) {
      stage_ = 1;
    } else if (text.find(L"CONCLUÍDO") != std::wstring::npos ||
               text.find(L"AUTORIZAÇÃO") != std::wstring::npos) {
      stage_ = 2;
    } else {
      stage_ = 0;
    }
    InvalidateRect(window_, nullptr, FALSE);
    UpdateWindow(window_);
    PumpMessages();
  }

 private:
  static constexpr COLORREF kCream = RGB(247, 243, 235);
  static constexpr COLORREF kWhite = RGB(255, 255, 255);
  static constexpr COLORREF kTeal = RGB(4, 73, 78);
  static constexpr COLORREF kOrange = RGB(242, 117, 27);
  static constexpr COLORREF kInk = RGB(31, 43, 45);
  static constexpr COLORREF kMuted = RGB(102, 111, 112);
  static constexpr COLORREF kLine = RGB(221, 225, 221);

  static HFONT Font(int height, int weight, const wchar_t* face = L"Segoe UI") {
    return CreateFontW(-height, 0, 0, 0, weight, FALSE, FALSE, FALSE,
                       DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                       CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, face);
  }

  static void FillRoundedRect(HDC dc, const RECT& rect, int radius,
                              COLORREF color) {
    HBRUSH brush = CreateSolidBrush(color);
    HPEN pen = CreatePen(PS_SOLID, 1, color);
    const auto old_brush = SelectObject(dc, brush);
    const auto old_pen = SelectObject(dc, pen);
    RoundRect(dc, rect.left, rect.top, rect.right, rect.bottom, radius, radius);
    SelectObject(dc, old_pen);
    SelectObject(dc, old_brush);
    DeleteObject(pen);
    DeleteObject(brush);
  }

  static void Text(HDC dc, const std::wstring& value, RECT rect, HFONT font,
                   COLORREF color, UINT format) {
    const auto old_font = SelectObject(dc, font);
    SetTextColor(dc, color);
    SetBkMode(dc, TRANSPARENT);
    DrawTextW(dc, value.c_str(), -1, &rect, format);
    SelectObject(dc, old_font);
  }

  void Paint(HDC dc) {
    RECT client{};
    GetClientRect(window_, &client);
    HBRUSH background = CreateSolidBrush(kCream);
    FillRect(dc, &client, background);
    DeleteObject(background);

    RECT card{24, 20, client.right - 24, client.bottom - 20};
    FillRoundedRect(dc, card, 24, kWhite);
    RECT accent{24, 20, client.right - 24, 27};
    FillRoundedRect(dc, accent, 8, kOrange);

    HFONT brand_font = Font(31, FW_BOLD);
    HFONT brand_detail_font = Font(11, FW_SEMIBOLD);
    HFONT heading_font = Font(21, FW_BOLD);
    HFONT normal_font = Font(15, FW_NORMAL);
    HFONT strong_font = Font(15, FW_SEMIBOLD);
    HFONT small_font = Font(12, FW_NORMAL);

    Text(dc, L"monalisa", RECT{52, 48, 250, 88}, brand_font, kTeal,
         DT_LEFT | DT_SINGLELINE | DT_VCENTER);
    Text(dc, L"F I N A N C E I R O", RECT{54, 80, 260, 101},
         brand_detail_font, kOrange, DT_LEFT | DT_SINGLELINE | DT_VCENTER);

    RECT badge{client.right - 205, 54, client.right - 52, 84};
    FillRoundedRect(dc, badge, 15, RGB(235, 244, 243));
    Text(dc, L"VERSÃO " + version_, badge, small_font, kTeal,
         DT_CENTER | DT_SINGLELINE | DT_VCENTER);

    Text(dc, L"ATUALIZANDO SEU SISTEMA", RECT{52, 115, client.right - 52, 145},
         heading_font, kInk, DT_LEFT | DT_SINGLELINE | DT_VCENTER);
    Text(dc, L"AGUARDE ENQUANTO PREPARAMOS A NOVA VERSÃO.",
         RECT{52, 145, client.right - 52, 170}, normal_font, kMuted,
         DT_LEFT | DT_SINGLELINE | DT_VCENTER);

    Text(dc, status_, RECT{52, 190, client.right - 115, 218}, strong_font,
         kTeal, DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS);
    Text(dc, std::to_wstring(percent_) + L"%",
         RECT{client.right - 112, 190, client.right - 52, 218}, strong_font,
         kTeal, DT_RIGHT | DT_SINGLELINE | DT_VCENTER);

    RECT progress_track{52, 226, client.right - 52, 240};
    FillRoundedRect(dc, progress_track, 10, RGB(228, 233, 229));
    if (percent_ > 0) {
      RECT progress_value = progress_track;
      progress_value.right = progress_value.left +
                             ((progress_track.right - progress_track.left) *
                              percent_) /
                                 100;
      if (progress_value.right < progress_value.left + 14) {
        progress_value.right = progress_value.left + 14;
      }
      FillRoundedRect(dc, progress_value, 10, kOrange);
    }

    const wchar_t* labels[] = {L"BAIXAR", L"VERIFICAR", L"INSTALAR"};
    const int centers[] = {132, client.right / 2, client.right - 132};
    for (int index = 0; index < 3; ++index) {
      if (index < 2) {
        HPEN line_pen = CreatePen(
            PS_SOLID, 2, index < stage_ ? kTeal : kLine);
        const auto old_pen = SelectObject(dc, line_pen);
        MoveToEx(dc, centers[index] + 17, 286, nullptr);
        LineTo(dc, centers[index + 1] - 17, 286);
        SelectObject(dc, old_pen);
        DeleteObject(line_pen);
      }
      const bool completed = index < stage_;
      const bool active = index == stage_;
      const COLORREF circle_color = completed || active ? kTeal : kWhite;
      RECT circle{centers[index] - 14, 272, centers[index] + 14,
                  300};
      FillRoundedRect(dc, circle, 28, circle_color);
      if (!completed && !active) {
        HPEN outline = CreatePen(PS_SOLID, 2, kLine);
        const auto old_pen = SelectObject(dc, outline);
        const auto old_brush = SelectObject(dc, GetStockObject(NULL_BRUSH));
        Ellipse(dc, circle.left, circle.top, circle.right, circle.bottom);
        SelectObject(dc, old_brush);
        SelectObject(dc, old_pen);
        DeleteObject(outline);
      }
      const std::wstring marker = completed ? L"✓" : std::to_wstring(index + 1);
      Text(dc, marker, circle, strong_font,
           completed || active ? kWhite : kMuted,
           DT_CENTER | DT_SINGLELINE | DT_VCENTER);
      Text(dc, labels[index],
           RECT{centers[index] - 70, 306, centers[index] + 70, 330},
           small_font, active ? kTeal : kMuted,
           DT_CENTER | DT_SINGLELINE | DT_VCENTER);
    }

    Text(dc, L"NÃO DESLIGUE O COMPUTADOR DURANTE A ATUALIZAÇÃO.",
         RECT{52, client.bottom - 55, client.right - 52, client.bottom - 31},
         small_font, kMuted, DT_CENTER | DT_SINGLELINE | DT_VCENTER);

    DeleteObject(brand_font);
    DeleteObject(brand_detail_font);
    DeleteObject(heading_font);
    DeleteObject(normal_font);
    DeleteObject(strong_font);
    DeleteObject(small_font);
  }

  static LRESULT CALLBACK WindowProcedure(HWND window, UINT message,
                                           WPARAM w_param, LPARAM l_param) {
    auto* self = reinterpret_cast<UpdateProgressWindow*>(
        GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
      const auto* create = reinterpret_cast<CREATESTRUCTW*>(l_param);
      self = static_cast<UpdateProgressWindow*>(create->lpCreateParams);
      SetWindowLongPtrW(window, GWLP_USERDATA,
                        reinterpret_cast<LONG_PTR>(self));
      if (self) self->window_ = window;
    }
    if (message == WM_ERASEBKGND) return 1;
    if (message == WM_PAINT && self) {
      PAINTSTRUCT paint{};
      HDC dc = BeginPaint(window, &paint);
      self->Paint(dc);
      EndPaint(window, &paint);
      return 0;
    }
    if (message == WM_CLOSE) return 0;
    return DefWindowProcW(window, message, w_param, l_param);
  }

  static void PumpMessages() {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
      TranslateMessage(&message);
      DispatchMessageW(&message);
    }
  }

  HWND window_ = nullptr;
  std::wstring version_;
  std::wstring status_ = L"PREPARANDO O DOWNLOAD...";
  int percent_ = 0;
  int stage_ = 0;
};

bool ParseAddress(const std::wstring& value, WebAddress& result) {
  URL_COMPONENTSW parts{};
  parts.dwStructSize = sizeof(parts);
  parts.dwHostNameLength = static_cast<DWORD>(-1);
  parts.dwUrlPathLength = static_cast<DWORD>(-1);
  parts.dwExtraInfoLength = static_cast<DWORD>(-1);
  if (!WinHttpCrackUrl(value.c_str(), 0, 0, &parts)) return false;
  result.host.assign(parts.lpszHostName, parts.dwHostNameLength);
  result.path.assign(parts.lpszUrlPath, parts.dwUrlPathLength);
  if (parts.dwExtraInfoLength > 0) {
    result.path.append(parts.lpszExtraInfo, parts.dwExtraInfoLength);
  }
  if (result.path.empty()) result.path = L"/";
  result.port = parts.nPort;
  result.secure = parts.nScheme == INTERNET_SCHEME_HTTPS;
  return !result.host.empty();
}

bool Download(
    const std::wstring& url, std::vector<unsigned char>& content,
    const std::function<void(size_t, size_t)>& progress = nullptr) {
  WebAddress address;
  if (!ParseAddress(url, address)) return false;
  HINTERNET session = WinHttpOpen(L"MonalisaFinanceiro/Updater",
                                  WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
                                  WINHTTP_NO_PROXY_NAME,
                                  WINHTTP_NO_PROXY_BYPASS, 0);
  if (!session) return false;
  WinHttpSetTimeouts(session, 5000, 5000, 15000, 120000);
  HINTERNET connection = WinHttpConnect(session, address.host.c_str(),
                                        address.port, 0);
  const DWORD flags = address.secure ? WINHTTP_FLAG_SECURE : 0;
  HINTERNET request = connection
                          ? WinHttpOpenRequest(connection, L"GET",
                                               address.path.c_str(), nullptr,
                                               WINHTTP_NO_REFERER,
                                               WINHTTP_DEFAULT_ACCEPT_TYPES,
                                               flags)
                          : nullptr;
  bool success = request &&
                 WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0,
                                    WINHTTP_NO_REQUEST_DATA, 0, 0, 0) &&
                 WinHttpReceiveResponse(request, nullptr);
  DWORD status = 0;
  DWORD status_size = sizeof(status);
  success = success && WinHttpQueryHeaders(
                           request,
                           WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                           WINHTTP_HEADER_NAME_BY_INDEX, &status, &status_size,
                           WINHTTP_NO_HEADER_INDEX) &&
            status == 200;
  DWORD total = 0;
  DWORD total_size = sizeof(total);
  if (success) {
    WinHttpQueryHeaders(request,
                        WINHTTP_QUERY_CONTENT_LENGTH |
                            WINHTTP_QUERY_FLAG_NUMBER,
                        WINHTTP_HEADER_NAME_BY_INDEX, &total, &total_size,
                        WINHTTP_NO_HEADER_INDEX);
    if (progress) progress(0, total);
  }
  while (success) {
    DWORD available = 0;
    if (!WinHttpQueryDataAvailable(request, &available)) {
      success = false;
      break;
    }
    if (available == 0) break;
    const size_t offset = content.size();
    content.resize(offset + available);
    DWORD read = 0;
    if (!WinHttpReadData(request, content.data() + offset, available, &read)) {
      success = false;
      break;
    }
    content.resize(offset + read);
    if (progress) progress(content.size(), total);
  }
  if (request) WinHttpCloseHandle(request);
  if (connection) WinHttpCloseHandle(connection);
  WinHttpCloseHandle(session);
  return success;
}

std::string JsonText(const std::string& json, const std::string& name) {
  const std::regex expression("\\\"" + name +
                              "\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"");
  std::smatch match;
  return std::regex_search(json, match, expression) ? match[1].str() : "";
}

bool JsonTrue(const std::string& json, const std::string& name) {
  const std::regex expression("\\\"" + name + "\\\"\\s*:\\s*true");
  return std::regex_search(json, expression);
}

std::array<unsigned long, 3> VersionParts(const std::string& value) {
  std::smatch match;
  const std::regex expression(R"((\d+)\.(\d+)\.(\d+))");
  if (!std::regex_search(value, match, expression)) return {0, 0, 0};
  return {std::stoul(match[1].str()), std::stoul(match[2].str()),
          std::stoul(match[3].str())};
}

bool IsNewerVersion(const std::string& latest, const std::string& current) {
  return VersionParts(latest) > VersionParts(current);
}

std::wstring Wide(const std::string& value) {
  if (value.empty()) return L"";
  const int length = MultiByteToWideChar(CP_UTF8, 0, value.data(),
                                         static_cast<int>(value.size()), nullptr, 0);
  std::wstring result(length, L'\0');
  MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                      result.data(), length);
  return result;
}

std::string FileHash(const std::filesystem::path& path) {
  BCRYPT_ALG_HANDLE algorithm = nullptr;
  BCRYPT_HASH_HANDLE hash = nullptr;
  DWORD object_size = 0;
  DWORD result_size = 0;
  std::vector<unsigned char> object;
  std::vector<unsigned char> digest(32);
  std::string result;
  if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr,
                                  0) < 0 ||
      BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH,
                        reinterpret_cast<PUCHAR>(&object_size),
                        sizeof(object_size), &result_size, 0) < 0) {
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    return result;
  }
  object.resize(object_size);
  if (BCryptCreateHash(algorithm, &hash, object.data(), object_size, nullptr, 0,
                       0) >= 0) {
    std::ifstream input(path, std::ios::binary);
    std::vector<unsigned char> buffer(1024 * 1024);
    while (input) {
      input.read(reinterpret_cast<char*>(buffer.data()), buffer.size());
      const auto read = input.gcount();
      if (read > 0 && BCryptHashData(hash, buffer.data(),
                                    static_cast<ULONG>(read), 0) < 0) {
        result.clear();
        break;
      }
    }
    if (input.eof() &&
        BCryptFinishHash(hash, digest.data(),
                         static_cast<ULONG>(digest.size()), 0) >= 0) {
      std::ostringstream text;
      text << std::uppercase << std::hex << std::setfill('0');
      for (const auto byte : digest) text << std::setw(2) << int(byte);
      result = text.str();
    }
  }
  if (hash) BCryptDestroyHash(hash);
  BCryptCloseAlgorithmProvider(algorithm, 0);
  return result;
}

bool InstalledForAutomaticUpdate(const std::filesystem::path& directory) {
  if (EnvironmentValue(L"MONALISA_UPDATE_TEST") == L"1") return true;
  const std::wstring program_files = EnvironmentValue(L"ProgramFiles");
  if (program_files.empty()) return false;
  std::wstring installed = std::filesystem::weakly_canonical(directory).wstring();
  std::wstring expected = std::filesystem::weakly_canonical(program_files).wstring();
  std::transform(installed.begin(), installed.end(), installed.begin(), towlower);
  std::transform(expected.begin(), expected.end(), expected.begin(), towlower);
  return installed.rfind(expected + L"\\", 0) == 0;
}

enum class UpdateResult { none, started, failed };

UpdateResult ApplyMandatoryUpdate(const std::filesystem::path& directory,
                                  const std::wstring& server_url,
                                  const std::wstring& public_manifest_url) {
  if (!InstalledForAutomaticUpdate(directory) || server_url.empty()) {
    return UpdateResult::none;
  }
  std::vector<unsigned char> manifest_bytes;
  bool public_manifest = false;
  if (!public_manifest_url.empty()) {
    public_manifest = Download(public_manifest_url, manifest_bytes);
  }
  if (!public_manifest) {
    const std::wstring panel_manifest_url =
        server_url + L"/api/update/manifest?version=" +
        EncodeVersionForQuery(Wide(FLUTTER_VERSION));
    if (!Download(panel_manifest_url, manifest_bytes)) {
      return UpdateResult::none;
    }
  }
  const std::string manifest(manifest_bytes.begin(), manifest_bytes.end());
  if (!JsonTrue(manifest, "mandatory")) return UpdateResult::none;
  const std::string version = JsonText(manifest, "latestVersion");
  const std::string expected_hash = JsonText(manifest, "sha256");
  std::string download_path = JsonText(manifest, "downloadUrl");
  if (download_path.empty()) {
    download_path = JsonText(manifest, "downloadPath");
  }
  if (!IsNewerVersion(version, FLUTTER_VERSION)) return UpdateResult::none;
  if (version.empty() || expected_hash.size() != 64 || download_path.empty()) {
    MessageBoxW(nullptr, L"O PAINEL INFORMOU UMA ATUALIZAÇÃO INVÁLIDA.",
                L"ATUALIZAÇÃO OBRIGATÓRIA", MB_OK | MB_ICONERROR);
    return UpdateResult::failed;
  }
  UpdateProgressWindow progress_window(Wide(version));
  const auto update_directory = ProgramDataDirectory() / L"updates";
  std::error_code error;
  std::filesystem::create_directories(update_directory, error);
  const auto installer = update_directory /
                         (L"Monalisa-Financeiro-" + Wide(version) + L".exe");
  std::vector<unsigned char> package;
  const std::wstring package_url =
      download_path.rfind("http", 0) == 0 ? Wide(download_path)
                                           : server_url + Wide(download_path);
  int last_percent = -1;
  if (!Download(package_url, package,
                [&progress_window, &last_percent](size_t received,
                                                   size_t total) {
                  const int percent = total > 0
                                          ? static_cast<int>((received * 100) /
                                                             total)
                                          : 0;
                  if (percent == last_percent) return;
                  last_percent = percent;
                  progress_window.SetStatus(
                      L"BAIXANDO A ATUALIZAÇÃO... " +
                          std::to_wstring(percent) + L"%",
                      percent);
                })) {
    MessageBoxW(nullptr,
                L"A ATUALIZAÇÃO É OBRIGATÓRIA, MAS O ARQUIVO NÃO PÔDE SER BAIXADO. VERIFIQUE A CONEXÃO E TENTE NOVAMENTE.",
                L"ATUALIZAÇÃO OBRIGATÓRIA", MB_OK | MB_ICONERROR);
    return UpdateResult::failed;
  }
  progress_window.SetStatus(L"SALVANDO O ARQUIVO DA ATUALIZAÇÃO...", 100);
  { std::ofstream output(installer, std::ios::binary | std::ios::trunc);output.write(reinterpret_cast<const char*>(package.data()),package.size()); }
  progress_window.SetStatus(L"VERIFICANDO A SEGURANÇA DO ARQUIVO...", 100);
  if (FileHash(installer) != expected_hash) {
    std::filesystem::remove(installer, error);
    MessageBoxW(nullptr,
                L"A ATUALIZAÇÃO BAIXADA NÃO PASSOU NA VERIFICAÇÃO DE SEGURANÇA.",
                L"ATUALIZAÇÃO OBRIGATÓRIA", MB_OK | MB_ICONERROR);
    return UpdateResult::failed;
  }
  SetEnvironmentVariableW(L"MONALISA_LICENSE_URL", server_url.c_str());
  SetEnvironmentVariableW(L"MONALISA_UPDATE_MODE", L"1");
  progress_window.SetStatus(
      L"DOWNLOAD CONCLUÍDO. AGUARDANDO A AUTORIZAÇÃO DO WINDOWS...", 100);
  if (EnvironmentValue(L"MONALISA_UPDATE_DRY_RUN") == L"1") {
    return UpdateResult::started;
  }
  const auto launched = reinterpret_cast<INT_PTR>(ShellExecuteW(
      nullptr, L"runas", installer.c_str(), nullptr,
      update_directory.c_str(), SW_SHOWNORMAL));
  if (launched <= 32) {
    MessageBoxW(nullptr,
                L"O WINDOWS NÃO AUTORIZOU A INSTALAÇÃO DA ATUALIZAÇÃO OBRIGATÓRIA.",
                L"ATUALIZAÇÃO OBRIGATÓRIA", MB_OK | MB_ICONERROR);
    return UpdateResult::failed;
  }
  return UpdateResult::started;
}

bool ServerReady() {
  HINTERNET session = WinHttpOpen(L"MonalisaFinanceiro/1.0",
                                  WINHTTP_ACCESS_TYPE_NO_PROXY,
                                  WINHTTP_NO_PROXY_NAME,
                                  WINHTTP_NO_PROXY_BYPASS, 0);
  if (!session) return false;
  WinHttpSetTimeouts(session, 500, 500, 500, 500);
  HINTERNET connection = WinHttpConnect(session, L"127.0.0.1", 5187, 0);
  HINTERNET request = connection
                          ? WinHttpOpenRequest(connection, L"GET", L"/api/health",
                                               nullptr, WINHTTP_NO_REFERER,
                                               WINHTTP_DEFAULT_ACCEPT_TYPES, 0)
                          : nullptr;
  bool ready = false;
  if (request && WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0,
                                    WINHTTP_NO_REQUEST_DATA, 0, 0, 0) &&
      WinHttpReceiveResponse(request, nullptr)) {
    DWORD status = 0;
    DWORD size = sizeof(status);
    ready = WinHttpQueryHeaders(request,
                                WINHTTP_QUERY_STATUS_CODE |
                                    WINHTTP_QUERY_FLAG_NUMBER,
                                WINHTTP_HEADER_NAME_BY_INDEX, &status, &size,
                                WINHTTP_NO_HEADER_INDEX) &&
            status == 200;
  }
  if (request) WinHttpCloseHandle(request);
  if (connection) WinHttpCloseHandle(connection);
  WinHttpCloseHandle(session);
  return ready;
}

struct ServerProcess {
  HANDLE process = nullptr;
  HANDLE job = nullptr;
};

bool StartServer(const std::filesystem::path& application_directory,
                 ServerProcess& server) {
  const auto configuration_directory = ProgramDataDirectory();
  const auto data_directory = configuration_directory / L"data";
  std::error_code error;
  std::filesystem::create_directories(data_directory, error);

  if (EnvironmentValue(L"FINANCE_DB").empty()) {
    const std::wstring database = (data_directory / L"financeiro.db").wstring();
    SetEnvironmentVariableW(L"FINANCE_DB", database.c_str());
  }
  const std::wstring network_role = ReadConfigurationValue(
      configuration_directory / L"network-role.txt", L"standalone");
  const std::wstring network_key = ReadConfigurationValue(
      configuration_directory / L"network-key.txt");
  const std::wstring data_server = ReadConfigurationValue(
      configuration_directory / L"network-server.txt");
  SetEnvironmentVariableW(L"MONALISA_NETWORK_ROLE", network_role.c_str());
  SetEnvironmentVariableW(L"MONALISA_NETWORK_KEY", network_key.c_str());
  SetEnvironmentVariableW(L"MONALISA_NETWORK_SERVER_URL", data_server.c_str());
  SetEnvironmentVariableW(L"MONALISA_NETWORK_SHARED_PORT", L"5188");
  if (network_role == L"client") {
    SetEnvironmentVariableW(L"MONALISA_DATA_SERVER_URL", data_server.c_str());
  } else {
    SetEnvironmentVariableW(L"MONALISA_DATA_SERVER_URL", nullptr);
  }
  if (EnvironmentValue(L"FINANCE_URL").empty()) {
    SetEnvironmentVariableW(
        L"FINANCE_URL",
        network_role == L"server"
            ? L"http://127.0.0.1:5187;http://0.0.0.0:5188"
            : L"http://127.0.0.1:5187");
  }
  if (EnvironmentValue(L"MONALISA_LICENSE_URL").empty()) {
    const std::wstring license_server =
        ReadLicenseServer(configuration_directory / L"license-server.txt");
    SetEnvironmentVariableW(L"MONALISA_LICENSE_URL", license_server.c_str());
  }

  if (ServerReady()) return true;

  const auto server_directory = application_directory / L"server";
  const auto executable = server_directory / L"Monalisa.Financeiro.Server.exe";
  if (!std::filesystem::exists(executable)) {
    MessageBoxW(nullptr, L"O SERVIÇO LOCAL DO MONALISA FINANCEIRO NÃO FOI ENCONTRADO.",
                L"MONALISA FINANCEIRO", MB_OK | MB_ICONERROR);
    return false;
  }

  std::wstring command = L"\"" + executable.wstring() + L"\"";
  std::vector<wchar_t> command_line(command.begin(), command.end());
  command_line.push_back(L'\0');
  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  PROCESS_INFORMATION process_info{};
  if (!CreateProcessW(executable.c_str(), command_line.data(), nullptr, nullptr,
                      FALSE, CREATE_NO_WINDOW, nullptr, server_directory.c_str(),
                      &startup, &process_info)) {
    MessageBoxW(nullptr, L"NÃO FOI POSSÍVEL INICIAR O SERVIÇO LOCAL DO FINANCEIRO.",
                L"MONALISA FINANCEIRO", MB_OK | MB_ICONERROR);
    return false;
  }
  CloseHandle(process_info.hThread);
  server.process = process_info.hProcess;
  // A máquina principal continua servindo os dados mesmo quando sua janela é
  // fechada. O instalador e o configurador encerram esse processo quando
  // precisam atualizar ou alterar o modo da rede.
  if (network_role != L"server") {
    server.job = CreateJobObjectW(nullptr, nullptr);
  }
  if (server.job) {
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    SetInformationJobObject(server.job, JobObjectExtendedLimitInformation,
                            &limits, sizeof(limits));
    AssignProcessToJobObject(server.job, server.process);
  }

  for (int attempt = 0; attempt < 60; ++attempt) {
    if (ServerReady()) return true;
    if (WaitForSingleObject(server.process, 0) == WAIT_OBJECT_0) break;
    std::this_thread::sleep_for(std::chrono::milliseconds(250));
  }
  MessageBoxW(nullptr, L"O SERVIÇO LOCAL NÃO RESPONDEU. REINICIE O MONALISA FINANCEIRO.",
              L"MONALISA FINANCEIRO", MB_OK | MB_ICONERROR);
  return false;
}

void CloseServer(ServerProcess& server) {
  if (server.job) CloseHandle(server.job);
  if (server.process) CloseHandle(server.process);
}

}  // namespace

int APIENTRY wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE prev,
                      _In_ wchar_t* command_line, _In_ int show_command) {
  if (!::AttachConsole(ATTACH_PARENT_PROCESS) && ::IsDebuggerPresent()) {
    CreateAndAttachConsole();
  }
  ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

  SetEnvironmentVariableW(L"MONALISA_APP_VERSION", Wide(FLUTTER_VERSION).c_str());
  const auto configuration_directory = ProgramDataDirectory();
  const auto license_server = EnvironmentValue(L"MONALISA_LICENSE_URL").empty()
                                  ? ReadLicenseServer(configuration_directory /
                                                      L"license-server.txt")
                                  : EnvironmentValue(L"MONALISA_LICENSE_URL");
  const auto public_update_manifest =
      EnvironmentValue(L"MONALISA_UPDATE_MANIFEST_URL").empty()
          ? L"https://raw.githubusercontent.com/iBabity/monalisa-financeiro-updates/main/manifest.json"
          : EnvironmentValue(L"MONALISA_UPDATE_MANIFEST_URL");
  const auto update = ApplyMandatoryUpdate(ExecutableDirectory(), license_server,
                                           public_update_manifest);
  if (update != UpdateResult::none) {
    ::CoUninitialize();
    return update == UpdateResult::started ? EXIT_SUCCESS : EXIT_FAILURE;
  }

  ServerProcess server;
  if (!StartServer(ExecutableDirectory(), server)) {
    CloseServer(server);
    ::CoUninitialize();
    return EXIT_FAILURE;
  }

  flutter::DartProject project(L"data");
  project.set_dart_entrypoint_arguments(GetCommandLineArguments());

  FlutterWindow window(project);
  Win32Window::Point origin(40, 40);
  Win32Window::Size size(1280, 760);
  if (!window.Create(L"Monalisa Financeiro", origin, size)) {
    CloseServer(server);
    ::CoUninitialize();
    return EXIT_FAILURE;
  }
  window.SetQuitOnClose(true);

  ::MSG msg;
  while (::GetMessage(&msg, nullptr, 0, 0)) {
    ::TranslateMessage(&msg);
    ::DispatchMessage(&msg);
  }

  CloseServer(server);
  ::CoUninitialize();
  return EXIT_SUCCESS;
}

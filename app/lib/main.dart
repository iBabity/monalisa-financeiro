import 'dart:convert';

import 'package:file_selector/file_selector.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter/services.dart';

import 'finance.dart';
import 'entry_form.dart';
import 'access.dart';
import 'browser_download.dart';
import 'pdf_open.dart';
import 'app_version.dart';
import 'ui_components.dart';

const teal = Color(0xFF073F43);
const orange = Color(0xFFED791B);
const cream = Color(0xFFF7F2EA);
void main() => runApp(const FinanceApp());

class FinanceApp extends StatelessWidget {
  const FinanceApp({super.key});
  @override
  Widget build(BuildContext context) => MaterialApp(
    title: 'Monalisa • Financeiro',
    locale: const Locale('pt', 'BR'),
    supportedLocales: const [Locale('pt', 'BR')],
    localizationsDelegates: GlobalMaterialLocalizations.delegates,
    debugShowCheckedModeBanner: false,
    theme: ThemeData(
      useMaterial3: true,
      fontFamily: 'Segoe UI',
      scaffoldBackgroundColor: cream,
      colorScheme: ColorScheme.fromSeed(
        seedColor: teal,
        primary: teal,
        secondary: orange,
        surface: Colors.white,
      ),
      cardTheme: CardThemeData(
        elevation: 0,
        color: Colors.white,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(18),
          side: const BorderSide(color: Color(0xFFE8E6DF)),
        ),
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: const Color(0xFFF8F9F7),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: const BorderSide(color: Color(0xFFDAE1DC)),
        ),
        contentPadding: const EdgeInsets.all(15),
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          padding: const EdgeInsets.symmetric(horizontal: 22, vertical: 19),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(12),
          ),
        ),
      ),
    ),
    home: AccessGate(
      builder: (api, user, companyId, license, logout) => Workspace(
        api: api,
        user: user,
        initialCompany: companyId,
        license: license,
        onLogout: logout,
      ),
    ),
  );
}

class Workspace extends StatefulWidget {
  final FinanceApi api;
  final RecordData user;
  final RecordData license;
  final int initialCompany;
  final VoidCallback onLogout;
  const Workspace({
    super.key,
    required this.api,
    required this.user,
    required this.initialCompany,
    required this.license,
    required this.onLogout,
  });
  @override
  State<Workspace> createState() => _WorkspaceState();
}

class _WorkspaceState extends State<Workspace> {
  late final FinanceApi api = widget.api;
  late RecordData license = widget.license;
  List<RecordData> companies = [],
      titles = [],
      parties = [],
      accounts = [],
      settlements = [],
      movements = [],
      audit = [],
      plans = [],
      paymentMethods = [],
      costCenters = [],
      creditCards = [],
      creditCardInvoices = [],
      creditCardPurchases = [],
      creditCardRecurringCharges = [],
      attachments = [],
      companyUsers = [],
      allUsers = [];
  RecordData permissions = {};
  late int company = widget.initialCompany;
  int page = 0, generation = 0;
  bool loading = true;
  bool reportBusy = false;
  double fontScale = 1;
  bool darkMode = false;
  String? error;
  String search = '', filter = 'Todos';
  String categoryFilter = '', costCenterFilter = '', sortTitles = 'due';
  final Set<int> selectedTitleIds = {};
  String registrySearch = '';
  String historySearch = '';
  String cashMovementSearch = '';
  String cashMovementType = 'all';
  String cashMovementAccount = '';
  String? historyDay = iso(DateTime.now());
  String selectedMonth = iso(DateTime.now()).substring(0, 7);
  DateTime reportStart = DateUtils.dateOnly(DateTime.now());
  DateTime reportEnd = DateUtils.dateOnly(DateTime.now());
  static const labels = [
    'Visão geral',
    'A pagar',
    'A receber',
    'Fluxo de caixa',
    'Cadastros',
    'Histórico',
    'Relatórios',
    'Administração',
    'Cartões de crédito',
  ];
  static const icons = [
    Icons.space_dashboard_outlined,
    Icons.north_east,
    Icons.south_west,
    Icons.account_balance_wallet_outlined,
    Icons.people_outline,
    Icons.history,
    Icons.picture_as_pdf_outlined,
    Icons.admin_panel_settings_outlined,
    Icons.credit_card_outlined,
  ];
  bool get globalAdmin => widget.user['globalAdmin'] == true;
  String roleForCompany(int companyId) {
    if (globalAdmin) return 'global';
    for (final item in companies) {
      if (item['id'] == companyId) return '${item['role']}';
    }
    return 'user';
  }

  bool get financialUser => roleForCompany(company) == 'user';
  bool get canManagePlanRates =>
      !financialUser || permissions['canManagePlanRates'] == true;
  bool pageAllowed(int index) =>
      !financialUser || (index != 3 && index != 5 && index != 7);
  bool get canManageAccess {
    if (globalAdmin) return true;
    return companies.any(
      (item) => item['id'] == company && item['role'] == 'admin',
    );
  }

  List<RecordData> get relatedCompanyUnits {
    final current = companies.where((item) => item['id'] == company).toList();
    if (current.isEmpty) return const [];
    final digits = '${current.first['cnpj']}'.replaceAll(RegExp(r'\D'), '');
    if (digits.length != 14) return const [];
    final root = digits.substring(0, 8);
    return companies.where((item) {
      final candidate = '${item['cnpj']}'.replaceAll(RegExp(r'\D'), '');
      return candidate.length == 14 && candidate.substring(0, 8) == root;
    }).toList();
  }

  bool get hasMatrixOrBranch => relatedCompanyUnits.length > 1;

  String get currentCompanyName {
    final current = companies.where((item) => item['id'] == company).toList();
    return current.isEmpty ? '' : '${current.first['name']}';
  }

  List<MapEntry<String, String>> branchOptions([String? existing]) {
    final values = <String, String>{};
    for (final item in relatedCompanyUnits) {
      final name = '${item['name']}';
      values[name] = '$name • ${maskedDocument('${item['cnpj']}')}';
    }
    if (existing != null && existing.trim().isNotEmpty) {
      values.putIfAbsent(existing, () => existing);
    }
    return values.entries.toList();
  }

  @override
  void initState() {
    super.initState();
    load();
  }

  @override
  void dispose() {
    super.dispose();
  }

  List<RecordData> rows(dynamic value) =>
      (value as List).map((e) => RecordData.from(e)).toList();
  Future<void> load() async {
    final request = ++generation;
    setState(() {
      loading = true;
      error = null;
    });
    try {
      final currentLicense = RecordData.from(
        await api.request('/license/status'),
      );
      final cs = rows(await api.request('/companies'));
      final selected = cs.any((e) => e['id'] == company)
          ? company
          : cs.first['id'] as int;
      if (currentLicense['status'] == 'active') {
        await api.request(
          '/companies/$selected/credit-card-recurrences/refresh',
          {},
        );
      }
      final data = await api.request('/companies/$selected/state');
      if (!mounted || request != generation) return;
      setState(() {
        companies = cs;
        license = currentLicense;
        company = selected;
        if (roleForCompany(selected) == 'user' && !pageAllowed(page)) page = 0;
        titles = rows(data['titles']);
        parties = rows(data['parties']);
        accounts = rows(data['accounts']);
        settlements = rows(data['settlements']);
        movements = rows(data['movements']);
        audit = rows(data['audit']);
        plans = rows(data['plans']);
        paymentMethods = rows(data['paymentMethods'] ?? []);
        costCenters = rows(data['costCenters'] ?? []);
        creditCards = rows(data['creditCards'] ?? []);
        creditCardInvoices = rows(data['creditCardInvoices'] ?? []);
        creditCardPurchases = rows(data['creditCardPurchases'] ?? []);
        creditCardRecurringCharges = rows(
          data['creditCardRecurringCharges'] ?? [],
        );
        attachments = rows(data['attachments'] ?? []);
        permissions = RecordData.from(data['permissions'] ?? const {});
        selectedTitleIds.clear();
        loading = false;
      });
    } catch (e) {
      if (mounted && request == generation) {
        setState(() {
          error = e.toString();
          loading = false;
        });
      }
    }
  }

  Future<void> loadCompanyUsers() async {
    if (!canManageAccess) return;
    try {
      final result = rows(await api.request('$base/users'));
      if (mounted) setState(() => companyUsers = result);
    } catch (e) {
      if (mounted) message(e.toString().replaceFirst('Exception: ', ''));
    }
  }

  Future<void> showInstallInstructions() async {
    var mobileUrl = '';
    try {
      final access = await api.request('/health/mobile-access');
      if (access['enabled'] == true) mobileUrl = '${access['url'] ?? ''}';
    } catch (_) {}
    if (!mounted) return;
    await showDialog<void>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        icon: const Icon(Icons.install_desktop, color: teal, size: 42),
        title: const Text('Instale o Monalisa'),
        content: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 560),
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Text(
                  'O sistema ficará com ícone próprio e abrirá em uma janela, como um aplicativo instalado.',
                ),
                const SizedBox(height: 20),
                const Text(
                  'NO COMPUTADOR • GOOGLE CHROME',
                  style: TextStyle(fontWeight: FontWeight.w800),
                ),
                const SizedBox(height: 8),
                const Text('1. Abra o menu de três pontos ⋮ do Chrome.'),
                const Text('2. Entre em “Transmitir, salvar e compartilhar”.'),
                const Text('3. Clique em “Instalar esta página como um app”.'),
                const Text('4. Confirme em “Instalar”.'),
                const SizedBox(height: 12),
                const Text(
                  'O ícone de instalação também pode aparecer à direita da barra de endereço.',
                ),
                const SizedBox(height: 20),
                const Text(
                  'MICROSOFT EDGE',
                  style: TextStyle(fontWeight: FontWeight.w800),
                ),
                const SizedBox(height: 8),
                const Text(
                  'Abra o menu ⋯, escolha “Aplicativos” e depois “Instalar Monalisa Financeiro”.',
                ),
                const SizedBox(height: 20),
                const Text(
                  'CELULAR',
                  style: TextStyle(fontWeight: FontWeight.w800),
                ),
                const SizedBox(height: 8),
                if (mobileUrl.isNotEmpty) ...[
                  const Text('Conecte o celular à mesma rede Wi-Fi e abra:'),
                  const SizedBox(height: 8),
                  SelectableText(
                    mobileUrl,
                    style: const TextStyle(
                      color: teal,
                      fontWeight: FontWeight.w800,
                      fontSize: 17,
                    ),
                  ),
                  Align(
                    alignment: Alignment.centerLeft,
                    child: TextButton.icon(
                      onPressed: () async {
                        await Clipboard.setData(ClipboardData(text: mobileUrl));
                        if (dialogContext.mounted) {
                          ScaffoldMessenger.of(dialogContext).showSnackBar(
                            const SnackBar(content: Text('Endereço copiado.')),
                          );
                        }
                      },
                      icon: const Icon(Icons.copy),
                      label: const Text('Copiar endereço'),
                    ),
                  ),
                ] else
                  const Text(
                    'O acesso pelo celular ainda não está liberado nesta máquina.',
                  ),
                const Text(
                  'Android • Chrome: menu ⋮ → Instalar aplicativo ou Adicionar à tela inicial.',
                ),
                const Text(
                  'iPhone • Safari: Compartilhar → Adicionar à Tela de Início.',
                ),
                const SizedBox(height: 20),
                const Text(
                  'Nesta edição de teste, os dados continuam salvos neste computador. O serviço do Monalisa iniciará automaticamente com o Windows.',
                  style: TextStyle(color: teal, fontWeight: FontWeight.w600),
                ),
              ],
            ),
          ),
        ),
        actions: [
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext),
            child: const Text('Entendi'),
          ),
        ],
      ),
    );
  }

  Future<void> loadAdminData() async {
    await loadCompanyUsers();
    if (!globalAdmin) return;
    try {
      final result = rows(await api.request('/users'));
      if (mounted) setState(() => allUsers = result);
    } catch (e) {
      if (mounted) message(e.toString().replaceFirst('Exception: ', ''));
    }
  }

  String get base => '/companies/$company';
  String get serviceOrigin => kIsWeb ? Uri.base.origin : apiBase;
  Color get mutedText => darkMode ? const Color(0xFFE1E8E6) : Colors.black54;
  Color get faintText => darkMode ? const Color(0xFFBEC9C6) : Colors.black45;
  Color get headingColor => darkMode ? const Color(0xFF8ED6D0) : teal;
  void message(String text) {
    if (mounted) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(text)));
    }
  }

  num total(Iterable<RecordData> list, String field) =>
      list.fold<num>(0, (sum, e) => sum + (e[field] as num));
  Iterable<RecordData> get open =>
      titles.where((t) => t['cancelled'] == 0 && t['remaining'] > 0);

  void selectPage(int targetPage, {bool closeDrawer = false}) {
    setState(() {
      page = targetPage;
      search = '';
      selectedTitleIds.clear();
      filter = targetPage == 1 || targetPage == 2 ? 'Em aberto' : 'Todos';
      if (targetPage == 1 || targetPage == 2) {
        selectedMonth = iso(DateTime.now()).substring(0, 7);
      }
    });
    if (targetPage == 7) loadAdminData();
    if (closeDrawer) Navigator.of(context).maybePop();
  }

  void selectCompany(int value) {
    setState(() {
      company = value;
      if (roleForCompany(value) == 'user' && !pageAllowed(page)) page = 0;
    });
    load().then((_) {
      if (page == 7) loadAdminData();
    });
  }

  Widget navigationSidebar({bool drawerMode = false}) => SizedBox(
    width: drawerMode ? 280 : 224,
    child: ColoredBox(
      color: teal,
      child: SafeArea(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            const Padding(
              padding: EdgeInsets.fromLTRB(25, 24, 20, 5),
              child: Text(
                'monalisa',
                style: TextStyle(
                  fontSize: 32,
                  fontWeight: FontWeight.w800,
                  letterSpacing: -1.5,
                  color: Colors.white,
                ),
              ),
            ),
            const Padding(
              padding: EdgeInsets.fromLTRB(27, 0, 20, 22),
              child: Text(
                'F I N A N C E I R O',
                style: TextStyle(
                  color: Color(0xFFFFBE67),
                  fontSize: 11,
                  fontWeight: FontWeight.w600,
                ),
              ),
            ),
            if (drawerMode && companies.length > 1)
              Padding(
                padding: const EdgeInsets.fromLTRB(18, 0, 18, 12),
                child: DropdownButtonFormField<int>(
                  initialValue: company,
                  dropdownColor: teal,
                  iconEnabledColor: Colors.white,
                  style: const TextStyle(color: Colors.white, fontSize: 13),
                  decoration: InputDecoration(
                    labelText: 'EMPRESA',
                    labelStyle: const TextStyle(color: Colors.white70),
                    filled: true,
                    fillColor: Colors.white.withValues(alpha: .08),
                    border: const OutlineInputBorder(),
                  ),
                  items: companies
                      .map(
                        (item) => DropdownMenuItem<int>(
                          value: item['id'],
                          child: Text(
                            '${item['name']}',
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                      )
                      .toList(),
                  onChanged: loading
                      ? null
                      : (value) {
                          if (value != null) selectCompany(value);
                        },
                ),
              ),
            Expanded(
              child: ListView(
                padding: EdgeInsets.zero,
                children: [
                  for (int i = 0; i < labels.length; i++)
                    if (pageAllowed(i) && (i != 7 || canManageAccess))
                      Padding(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 12,
                          vertical: 3,
                        ),
                        child: ListTile(
                          dense: true,
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(12),
                          ),
                          selected: page == i,
                          selectedTileColor: const Color(0xFF226064),
                          leading: Icon(
                            icons[i],
                            size: 21,
                            color: page == i
                                ? const Color(0xFFFFB651)
                                : Colors.white60,
                          ),
                          title: Text(
                            labels[i],
                            style: TextStyle(
                              color: page == i ? Colors.white : Colors.white70,
                              fontSize: 14,
                              fontWeight: page == i
                                  ? FontWeight.w600
                                  : FontWeight.normal,
                            ),
                          ),
                          onTap: () => selectPage(i, closeDrawer: drawerMode),
                        ),
                      ),
                  if (kIsWeb)
                    Padding(
                      padding: const EdgeInsets.fromLTRB(18, 10, 18, 4),
                      child: OutlinedButton.icon(
                        style: OutlinedButton.styleFrom(
                          foregroundColor: Colors.white,
                          side: BorderSide(
                            color: Colors.white.withValues(alpha: .28),
                          ),
                          padding: const EdgeInsets.symmetric(
                            horizontal: 12,
                            vertical: 13,
                          ),
                        ),
                        onPressed: showInstallInstructions,
                        icon: const Icon(Icons.install_desktop, size: 18),
                        label: const Text('Instalar aplicativo'),
                      ),
                    ),
                ],
              ),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(18, 8, 18, 18),
              child: Container(
                padding: const EdgeInsets.all(13),
                decoration: BoxDecoration(
                  color: Colors.white.withValues(alpha: .07),
                  borderRadius: BorderRadius.circular(12),
                ),
                child: const Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Icon(Icons.circle, size: 7, color: Color(0xFFBFD4AF)),
                        SizedBox(width: 8),
                        Text(
                          'Ambiente de teste',
                          style: TextStyle(color: Colors.white, fontSize: 12),
                        ),
                      ],
                    ),
                    SizedBox(height: 5),
                    Text(
                      'Dados salvos nesta máquina',
                      style: TextStyle(color: Colors.white54, fontSize: 11),
                    ),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    ),
  );

  PreferredSizeWidget mobileAppBar() => AppBar(
    toolbarHeight: 68,
    backgroundColor: darkMode ? const Color(0xFF1E1E1E) : Colors.white,
    foregroundColor: darkMode ? Colors.white : teal,
    titleSpacing: 4,
    title: Row(
      children: [
        const Icon(Icons.business_outlined, size: 20),
        const SizedBox(width: 9),
        Expanded(
          child: Text(
            companies.isEmpty
                ? 'MONALISA FINANCEIRO'
                : '${companies.firstWhere((item) => item['id'] == company, orElse: () => companies.first)['name']}',
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w700),
          ),
        ),
      ],
    ),
    actions: [
      IconButton(
        tooltip: darkMode ? 'Usar modo claro' : 'Usar modo escuro',
        onPressed: () => setState(() => darkMode = !darkMode),
        icon: Icon(
          darkMode ? Icons.light_mode_outlined : Icons.dark_mode_outlined,
          size: 20,
        ),
      ),
      IconButton(
        tooltip: 'Atualizar',
        onPressed: loading ? null : load,
        icon: const Icon(Icons.refresh, size: 21),
      ),
    ],
  );

  @override
  Widget build(BuildContext context) => Theme(
    data: darkMode
        ? ThemeData.dark(useMaterial3: true).copyWith(
            colorScheme: ColorScheme.fromSeed(
              seedColor: teal,
              brightness: Brightness.dark,
            ),
          )
        : Theme.of(context),
    child: MediaQuery(
      data: MediaQuery.of(context)
          .copyWith(textScaler: TextScaler.linear(fontScale)),
      child: CallbackShortcuts(
        bindings: {
          const SingleActivator(LogicalKeyboardKey.keyN, control: true): () {
            if (page == 1) titleForm('payable');
            if (page == 2) titleForm('receivable');
          },
          const SingleActivator(LogicalKeyboardKey.keyR, control: true): load,
          const CharacterActivator('ç', alt: true): showBackupTools,
          const CharacterActivator('Ç', alt: true): showBackupTools,
          const SingleActivator(LogicalKeyboardKey.keyF, control: true): () {
            if (page != 1 && page != 2) setState(() => page = 1);
          },
        },
        child: Focus(
          autofocus: true,
          child: Scaffold(
            drawer: MediaQuery.sizeOf(context).width < 760
                ? Drawer(width: 280, child: navigationSidebar(drawerMode: true))
                : null,
            appBar: MediaQuery.sizeOf(context).width < 760
                ? mobileAppBar()
                : null,
            body: Row(
              children: [
                if (MediaQuery.sizeOf(context).width >= 760)
                  navigationSidebar(),
                Expanded(
                  child: Column(
                    children: [
                      if (MediaQuery.sizeOf(context).width >= 760)
                        Container(
                          height: 82,
                          padding: const EdgeInsets.symmetric(horizontal: 30),
                          decoration: BoxDecoration(
                            color: darkMode
                                ? const Color(0xFF1E1E1E)
                                : Colors.white,
                            border: const Border(
                              bottom: BorderSide(color: Color(0xFFE7E5DD)),
                            ),
                          ),
                          child: Row(
                            children: [
                              const Icon(
                                Icons.business_outlined,
                                color: teal,
                                size: 21,
                              ),
                              const SizedBox(width: 12),
                              if (companies.length == 1)
                                Text(
                                  '${companies.first['name']}',
                                  overflow: TextOverflow.ellipsis,
                                  style: const TextStyle(
                                    fontWeight: FontWeight.w600,
                                    fontSize: 14,
                                  ),
                                )
                              else if (companies.length > 1)
                                SizedBox(
                                  width: 245,
                                  child: DropdownButtonHideUnderline(
                                    child: DropdownButton<int>(
                                      isExpanded: true,
                                      value: company,
                                      items: companies
                                          .map(
                                            (c) => DropdownMenuItem<int>(
                                              value: c['id'],
                                              child: Text(
                                                c['name'],
                                                overflow: TextOverflow.ellipsis,
                                                style: const TextStyle(
                                                  fontWeight: FontWeight.w600,
                                                  fontSize: 14,
                                                ),
                                              ),
                                            ),
                                          )
                                          .toList(),
                                      onChanged: loading
                                          ? null
                                          : (v) {
                                              if (v != null) {
                                                selectCompany(v);
                                              }
                                            },
                                    ),
                                  ),
                                ),
                              if (license['showcase'] == true) ...[
                                const SizedBox(width: 14),
                                Container(
                                  padding: const EdgeInsets.symmetric(
                                    horizontal: 10,
                                    vertical: 5,
                                  ),
                                  decoration: BoxDecoration(
                                    color: orange.withValues(alpha: .12),
                                    borderRadius: BorderRadius.circular(20),
                                    border: Border.all(
                                      color: orange.withValues(alpha: .35),
                                    ),
                                  ),
                                  child: const Text(
                                    'DEMONSTRAÇÃO',
                                    style: TextStyle(
                                      color: orange,
                                      fontSize: 10,
                                      fontWeight: FontWeight.w800,
                                      letterSpacing: .5,
                                    ),
                                  ),
                                ),
                              ],
                              const Spacer(),
                              IconButton(
                                tooltip: 'Diminuir letras',
                                onPressed: fontScale <= .9
                                    ? null
                                    : () => setState(
                                        () => fontScale = (fontScale - .1)
                                            .clamp(.9, 1.4),
                                      ),
                                icon: const Icon(Icons.text_decrease, size: 20),
                              ),
                              Text(
                                '${(fontScale * 100).round()}%',
                                style: TextStyle(
                                  fontSize: 11,
                                  color: mutedText,
                                ),
                              ),
                              IconButton(
                                tooltip: 'Aumentar letras',
                                onPressed: fontScale >= 1.4
                                    ? null
                                    : () => setState(
                                        () => fontScale = (fontScale + .1)
                                            .clamp(.9, 1.4),
                                      ),
                                icon: const Icon(Icons.text_increase, size: 20),
                              ),
                              IconButton(
                                tooltip: darkMode
                                    ? 'Usar modo claro'
                                    : 'Usar modo escuro',
                                onPressed: () =>
                                    setState(() => darkMode = !darkMode),
                                icon: Icon(
                                  darkMode
                                      ? Icons.light_mode_outlined
                                      : Icons.dark_mode_outlined,
                                  size: 20,
                                ),
                              ),
                              Text(
                                dateLabel(iso(DateTime.now())),
                                style: TextStyle(
                                  color: mutedText,
                                  fontSize: 12,
                                ),
                              ),
                              const SizedBox(width: 16),
                              IconButton(
                                tooltip: 'Atualizar',
                                onPressed: loading ? null : load,
                                icon: const Icon(Icons.refresh, size: 21),
                              ),
                              PopupMenuButton<String>(
                                tooltip: 'USUÁRIO CONECTADO',
                                onSelected: (value) {
                                  if (value == 'logout') widget.onLogout();
                                },
                                itemBuilder: (_) => [
                                  PopupMenuItem<String>(
                                    enabled: false,
                                    child: Text(
                                      '${widget.user['name']}\n${globalAdmin
                                          ? 'ADMINISTRADOR GERAL'
                                          : financialUser
                                          ? 'USUÁRIO FINANCEIRO'
                                          : 'ADMINISTRADOR DA EMPRESA'}',
                                    ),
                                  ),
                                  const PopupMenuDivider(),
                                  const PopupMenuItem<String>(
                                    value: 'logout',
                                    child: ListTile(
                                      contentPadding: EdgeInsets.zero,
                                      leading: Icon(Icons.logout),
                                      title: Text('SAIR'),
                                    ),
                                  ),
                                ],
                                child: Padding(
                                  padding: const EdgeInsets.symmetric(
                                    horizontal: 10,
                                    vertical: 8,
                                  ),
                                  child: Row(
                                    children: [
                                      const Icon(Icons.account_circle_outlined),
                                      const SizedBox(width: 7),
                                      Text(
                                        widget.user['username'],
                                        style: const TextStyle(
                                          fontSize: 12,
                                          fontWeight: FontWeight.w600,
                                        ),
                                      ),
                                    ],
                                  ),
                                ),
                              ),
                            ],
                          ),
                        ),
                      if (license['status'] == 'warning' ||
                          license['status'] == 'read_only')
                        Container(
                          width: double.infinity,
                          padding: const EdgeInsets.symmetric(
                            horizontal: 24,
                            vertical: 12,
                          ),
                          color: license['status'] == 'read_only'
                              ? const Color(0xFFD9EAF5)
                              : const Color(0xFFFFE5B8),
                          child: Row(
                            children: [
                              Icon(
                                license['status'] == 'read_only'
                                    ? Icons.visibility_outlined
                                    : Icons.warning_amber_rounded,
                                color: teal,
                              ),
                              const SizedBox(width: 12),
                              Expanded(
                                child: Text(
                                  license['status'] == 'read_only'
                                      ? 'SOMENTE CONSULTA • ${license['message'] ?? 'ENTRE EM CONTATO COM A MONALISA.'}'
                                      : 'AVISO DA MONALISA • ${license['message'] ?? ''}',
                                  style: const TextStyle(
                                    color: teal,
                                    fontWeight: FontWeight.w700,
                                    fontSize: 12,
                                  ),
                                ),
                              ),
                            ],
                          ),
                        ),
                      Expanded(
                        child: loading
                            ? const Center(child: CircularProgressIndicator())
                            : error != null
                            ? Center(
                                child: Column(
                                  mainAxisSize: MainAxisSize.min,
                                  children: [
                                    const Icon(
                                      Icons.cloud_off,
                                      size: 44,
                                      color: teal,
                                    ),
                                    const SizedBox(height: 16),
                                    Text(error!),
                                    const SizedBox(height: 20),
                                    FilledButton(
                                      onPressed: load,
                                      child: const Text('Tentar novamente'),
                                    ),
                                  ],
                                ),
                              )
                            : SingleChildScrollView(
                                padding: EdgeInsets.all(
                                  MediaQuery.sizeOf(context).width < 760
                                      ? 16
                                      : 30,
                                ),
                                child: Column(
                                  crossAxisAlignment:
                                      CrossAxisAlignment.stretch,
                                  children: [
                                    Wrap(
                                      alignment: WrapAlignment.spaceBetween,
                                      crossAxisAlignment:
                                          WrapCrossAlignment.start,
                                      spacing: 16,
                                      runSpacing: 16,
                                      children: [
                                        SizedBox(
                                          width:
                                              MediaQuery.sizeOf(context).width <
                                                  760
                                              ? MediaQuery.sizeOf(context)
                                                        .width -
                                                    32
                                              : 330,
                                          child: Column(
                                            crossAxisAlignment:
                                                CrossAxisAlignment.start,
                                            children: [
                                              Text(
                                                labels[page],
                                                style: TextStyle(
                                                  fontSize: 28,
                                                  fontWeight: FontWeight.w700,
                                                  color: headingColor,
                                                  letterSpacing: -.7,
                                                ),
                                              ),
                                              const SizedBox(height: 5),
                                              Text(
                                                [
                                                  'O financeiro do supermercado em um só lugar.',
                                                  'Compras, fornecedores e despesas da operação.',
                                                  'Cartões, PIX, vales, convênios e outros recebimentos.',
                                                  'Movimento realizado e previsão de caixa da loja.',
                                                  'Fornecedores, clientes, planos, setores e contas.',
                                                  'Rastreabilidade das operações da empresa.',
                                                  'Documentos claros para conferir, salvar e compartilhar.',
                                                  'Empresas, lojas e pessoas autorizadas a acessar o sistema.',
                                                  'Compras detalhadas e faturas reunidas no Contas a Pagar.',
                                                ][page],
                                                style: TextStyle(
                                                  color: mutedText,
                                                  fontSize: 13,
                                                ),
                                              ),
                                            ],
                                          ),
                                        ),
                                        if (page == 0)
                                          Wrap(
                                            spacing: 10,
                                            runSpacing: 10,
                                            children: [
                                              FilledButton.icon(
                                                onPressed: () =>
                                                    quickTitleConversation(
                                                      'payable',
                                                    ),
                                                icon: const Icon(
                                                  Icons.add,
                                                  size: 19,
                                                ),
                                                label: const Text(
                                                  'Criar despesa',
                                                ),
                                              ),
                                              FilledButton.icon(
                                                onPressed: () =>
                                                    quickTitleConversation(
                                                      'receivable',
                                                    ),
                                                icon: const Icon(
                                                  Icons.add,
                                                  size: 19,
                                                ),
                                                label: const Text(
                                                  'Criar recebimento',
                                                ),
                                              ),
                                            ],
                                          )
                                        else if (page <= 2)
                                          Wrap(
                                            spacing: 10,
                                            runSpacing: 10,
                                            children: [
                                              FilledButton.icon(
                                                onPressed: () => titleForm(
                                                  page == 2
                                                      ? 'receivable'
                                                      : 'payable',
                                                ),
                                                icon: const Icon(
                                                  Icons.add,
                                                  size: 19,
                                                ),
                                                label: Text(
                                                  page == 2
                                                      ? 'Nova receita'
                                                      : 'Nova despesa',
                                                ),
                                              ),
                                              if (page == 1 || page == 2)
                                                FilledButton.icon(
                                                  onPressed: reportBusy
                                                      ? null
                                                      : () => showTitleReports(
                                                          page == 1
                                                              ? 'payable'
                                                              : 'receivable',
                                                        ),
                                                  icon: const Icon(
                                                    Icons
                                                        .picture_as_pdf_outlined,
                                                    size: 19,
                                                  ),
                                                  label: const Text(
                                                    'Relatórios',
                                                  ),
                                                ),
                                              if (page == 2)
                                                FilledButton.icon(
                                                  onPressed:
                                                      showReceivablesMonitor,
                                                  icon: const Icon(
                                                    Icons
                                                        .monitor_heart_outlined,
                                                    size: 19,
                                                  ),
                                                  label: const Text(
                                                    'Monitoramento',
                                                  ),
                                                ),
                                              if (page == 1)
                                                FilledButton.icon(
                                                  onPressed: () =>
                                                      setState(() => page = 8),
                                                  icon: const Icon(
                                                    Icons.credit_card_outlined,
                                                    size: 19,
                                                  ),
                                                  label: const Text(
                                                    'Cartões de crédito',
                                                  ),
                                                ),
                                            ],
                                          )
                                        else if (page == 8)
                                          Wrap(
                                            spacing: 10,
                                            runSpacing: 10,
                                            children: [
                                              FilledButton.icon(
                                                onPressed: creditCardForm,
                                                icon: const Icon(
                                                  Icons.add_card_outlined,
                                                  size: 19,
                                                ),
                                                label: const Text(
                                                  'Novo cartão',
                                                ),
                                              ),
                                              if (creditCards.isNotEmpty)
                                                FilledButton.icon(
                                                  onPressed:
                                                      creditCardPurchaseForm,
                                                  icon: const Icon(
                                                    Icons.add_shopping_cart,
                                                    size: 19,
                                                  ),
                                                  label: const Text(
                                                    'Nova compra',
                                                  ),
                                                ),
                                            ],
                                          ),
                                      ],
                                    ),
                                    const SizedBox(height: 25),
                                    if (page == 0) ...dashboard(),
                                    if (page == 1 || page == 2) ...titlePage(),
                                    if (page == 3 && !financialUser)
                                      ...cashPage(),
                                    if (page == 4) ...registryPage(),
                                    if (page == 5 && !financialUser)
                                      ...historyPage(),
                                    if (page == 6) ...reportsPage(),
                                    if (page == 7 && canManageAccess)
                                      ...adminPage(),
                                    if (page == 8) ...creditCardsPage(),
                                    const SizedBox(height: 25),
                                    Text(
                                      'MONALISA SISTEMAS  /  ${kIsWeb ? 'FINANCEIRO WEB' : 'FINANCEIRO WINDOWS'} • VERSÃO $appVersion',
                                      style: TextStyle(
                                        color: faintText,
                                        fontSize: 10,
                                        letterSpacing: .8,
                                      ),
                                    ),
                                  ],
                                ),
                              ),
                      ),
                      if (page == 1 || page == 2)
                        Wrap(
                          spacing: 8,
                          runSpacing: 8,
                          alignment: WrapAlignment.end,
                          children: [
                            FilledButton.icon(
                              onPressed: openFinanceSandbox,
                              icon: const Icon(Icons.account_balance_outlined),
                              label: const Text('OPEN FINANCE — TESTE'),
                            ),
                            FilledButton.icon(
                              onPressed: importOfxPayables,
                              icon: const Icon(
                                Icons.upload_file_outlined,
                                size: 19,
                              ),
                              label: const Text('CONCILIAR EXTRATO OFX'),
                            ),
                          ],
                        ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    ),
  );

  List<Widget> adminPage() => [
    if (globalAdmin) ...[
      panel(
        'Empresas e lojas',
        Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Wrap(
              alignment: WrapAlignment.spaceBetween,
              crossAxisAlignment: WrapCrossAlignment.center,
              spacing: 16,
              runSpacing: 12,
              children: [
                const SizedBox(
                  width: 520,
                  child: Text(
                    'O ADMINISTRADOR GERAL ENXERGA TODAS AS EMPRESAS. CADA NOVO CNPJ RECEBE SEUS PRÓPRIOS CADASTROS, CONTAS E LANÇAMENTOS.',
                  ),
                ),
                FilledButton.icon(
                  onPressed: companyForm,
                  icon: const Icon(Icons.add_business_outlined),
                  label: const Text('NOVA EMPRESA'),
                ),
              ],
            ),
            const SizedBox(height: 18),
            ...companies.map(
              (item) => ListTile(
                contentPadding: EdgeInsets.zero,
                leading: CircleAvatar(
                  backgroundColor: teal.withValues(alpha: .10),
                  child: const Icon(Icons.storefront_outlined, color: teal),
                ),
                title: Text(
                  item['name'],
                  style: const TextStyle(fontWeight: FontWeight.w700),
                ),
                subtitle: Text('CNPJ ${cnpjLabel(item['cnpj'])}'),
                trailing: item['id'] == company
                    ? const Chip(label: Text('EMPRESA ATUAL'))
                    : TextButton(
                        onPressed: () {
                          setState(() => company = item['id']);
                          load().then((_) => loadAdminData());
                        },
                        child: const Text('ABRIR'),
                      ),
              ),
            ),
          ],
        ),
      ),
      const SizedBox(height: 22),
    ],
    panel(
      'Acessos desta empresa',
      Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Wrap(
            alignment: WrapAlignment.spaceBetween,
            crossAxisAlignment: WrapCrossAlignment.center,
            spacing: 16,
            runSpacing: 12,
            children: [
              SizedBox(
                width: 520,
                child: Text(
                  globalAdmin
                      ? 'CRIE O ADMINISTRADOR RESPONSÁVEL POR ESTA EMPRESA OU UM ACESSO OPERACIONAL.'
                      : 'CRIE E BLOQUEIE ACESSOS OPERACIONAIS DESTA EMPRESA.',
                ),
              ),
              Wrap(
                spacing: 10,
                runSpacing: 10,
                children: [
                  FilledButton.icon(
                    onPressed: userAccessForm,
                    icon: const Icon(Icons.person_add_alt_1_outlined),
                    label: Text(
                      globalAdmin ? 'CRIAR ACESSO OU ADMIN' : 'CRIAR ACESSO',
                    ),
                  ),
                  if (globalAdmin)
                    FilledButton.tonalIcon(
                      onPressed: linkExistingUserForm,
                      icon: const Icon(Icons.link),
                      label: const Text('VINCULAR PESSOA EXISTENTE'),
                    ),
                ],
              ),
            ],
          ),
          const SizedBox(height: 18),
          if (companyUsers.isEmpty)
            const Padding(
              padding: EdgeInsets.symmetric(vertical: 20),
              child: Text(
                'NENHUM ACESSO ESPECÍFICO FOI CRIADO PARA ESTA EMPRESA.',
              ),
            )
          else
            ...companyUsers.map((access) {
              final active = access['active'] == 1;
              final admin = access['role'] == 'admin';
              return Card(
                margin: const EdgeInsets.only(bottom: 10),
                child: Padding(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 14,
                    vertical: 8,
                  ),
                  child: Column(
                    children: [
                      ListTile(
                        contentPadding: EdgeInsets.zero,
                        leading: CircleAvatar(
                          backgroundColor: active
                              ? teal.withValues(alpha: .10)
                              : Colors.grey.withValues(alpha: .15),
                          child: Icon(
                            admin
                                ? Icons.admin_panel_settings_outlined
                                : Icons.person_outline,
                            color: active ? teal : Colors.grey,
                          ),
                        ),
                        title: Text(
                          access['name'],
                          style: const TextStyle(fontWeight: FontWeight.w700),
                        ),
                        subtitle: Text(
                          '${access['username']} • ${admin ? 'ADMINISTRADOR DA EMPRESA' : 'USUÁRIO FINANCEIRO'}',
                        ),
                        trailing: FilledButton.tonalIcon(
                          onPressed: () => changeUserAccess(access, !active),
                          icon: Icon(
                            active ? Icons.lock_outline : Icons.lock_open,
                          ),
                          label: Text(active ? 'BLOQUEAR' : 'LIBERAR'),
                        ),
                      ),
                      if (!admin)
                        SwitchListTile(
                          contentPadding: EdgeInsets.zero,
                          title: const Text(
                            'PERMITIR ALTERAR JUROS, MULTA E DESCONTO',
                          ),
                          subtitle: const Text(
                            'QUANDO DESATIVADO, O USUÁRIO APENAS USA OS PERCENTUAIS DEFINIDOS PELO ADMINISTRADOR.',
                          ),
                          value: access['canManagePlanRates'] == 1,
                          onChanged: active
                              ? (value) =>
                                    changePlanRatePermission(access, value)
                              : null,
                        ),
                    ],
                  ),
                ),
              );
            }),
        ],
      ),
    ),
  ];

  void userAccessForm() {
    form(
      globalAdmin ? 'Criar acesso para a empresa' : 'Criar usuário financeiro',
      [
        FormFieldSpec('name', 'Nome completo', required: true),
        FormFieldSpec(
          'username',
          'Nome de usuário',
          required: true,
          hint: 'Ex.: MARIA.GERENTE',
          helper: 'Será usado junto com o PIN na tela de entrada.',
        ),
        FormFieldSpec(
          'pin',
          'PIN inicial',
          required: true,
          secret: true,
          hint: '6 a 12 números',
        ),
        FormFieldSpec(
          'pinConfirm',
          'Confirme o PIN inicial',
          required: true,
          secret: true,
        ),
        FormFieldSpec(
          'role',
          'Tipo de acesso',
          initial: globalAdmin ? 'admin' : 'user',
          required: true,
          options: globalAdmin
              ? const [
                  MapEntry('admin', 'Administrador desta empresa'),
                  MapEntry('user', 'Usuário financeiro'),
                ]
              : const [MapEntry('user', 'Usuário financeiro')],
        ),
        FormFieldSpec(
          'canManagePlanRates',
          'Pode alterar juros, multa e desconto?',
          initial: 'false',
          options: const [
            MapEntry(
              'false',
              'NÃO, USAR SOMENTE A CONFIGURAÇÃO DO ADMINISTRADOR',
            ),
            MapEntry('true', 'SIM, PERMITIR ALTERAÇÕES'),
          ],
        ),
      ],
      (values) async {
        final accessPin = values['pin'] ?? '';
        if (!RegExp(r'^\d{6,12}$').hasMatch(accessPin)) {
          throw Exception('O PIN deve conter de 6 a 12 números.');
        }
        if (accessPin != values['pinConfirm']) {
          throw Exception('Os dois PINs precisam ser iguais.');
        }
        await api.request('$base/users', {
          'name': values['name'],
          'username': values['username'],
          'pin': accessPin,
          'role': values['role'],
          'canManagePlanRates': values['canManagePlanRates'] == 'true',
        });
        await loadCompanyUsers();
      },
      note: 'O acesso ficará limitado a esta empresa. O administrador geral continuará com acesso a todas as lojas.',
    );
  }

  void linkExistingUserForm() {
    final linkedIds = companyUsers.map((item) => item['id']).toSet();
    final available = allUsers
        .where(
          (item) =>
              item['globalAdmin'] != 1 &&
              item['active'] == 1 &&
              !linkedIds.contains(item['id']),
        )
        .toList();
    if (available.isEmpty) {
      message('NÃO HÁ OUTRA PESSOA DISPONÍVEL PARA VINCULAR.');
      return;
    }
    form(
      'Vincular pessoa a esta empresa',
      [
        FormFieldSpec(
          'userId',
          'Pessoa já cadastrada',
          required: true,
          initial: '${available.first['id']}',
          options: options(available),
        ),
        FormFieldSpec(
          'role',
          'Tipo de acesso nesta empresa',
          required: true,
          initial: 'admin',
          options: const [
            MapEntry('admin', 'Administrador desta empresa'),
            MapEntry('user', 'Usuário financeiro'),
          ],
        ),
        FormFieldSpec(
          'canManagePlanRates',
          'Pode alterar juros, multa e desconto?',
          initial: 'false',
          options: const [
            MapEntry(
              'false',
              'NÃO, USAR SOMENTE A CONFIGURAÇÃO DO ADMINISTRADOR',
            ),
            MapEntry('true', 'SIM, PERMITIR ALTERAÇÕES'),
          ],
        ),
      ],
      (values) async {
        await api.request('$base/users/${values['userId']}/link', {
          'role': values['role'],
          'canManagePlanRates': values['canManagePlanRates'] == 'true',
        });
        await loadAdminData();
      },
      note: 'A pessoa continuará usando o mesmo usuário e PIN, mas poderá escolher este CNPJ depois do login.',
    );
  }

  Future<void> changeUserAccess(RecordData access, bool active) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(active ? 'LIBERAR ACESSO?' : 'BLOQUEAR ACESSO?'),
        content: Text(
          active
              ? '${access['name']} VOLTARÁ A ENTRAR NESTA EMPRESA.'
              : '${access['name']} NÃO CONSEGUIRÁ MAIS ENTRAR NESTA EMPRESA.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('VOLTAR'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: Text(active ? 'LIBERAR' : 'BLOQUEAR'),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    try {
      await api.request('$base/users/${access['id']}/access', {
        'role': access['role'],
        'active': active,
        'canManagePlanRates': access['canManagePlanRates'] == 1,
      });
      await loadCompanyUsers();
      message(active ? 'ACESSO LIBERADO.' : 'ACESSO BLOQUEADO.');
    } catch (e) {
      message(e.toString().replaceFirst('Exception: ', ''));
    }
  }

  Future<void> changePlanRatePermission(RecordData access, bool allowed) async {
    try {
      await api.request('$base/users/${access['id']}/access', {
        'role': access['role'],
        'active': access['active'] == 1,
        'canManagePlanRates': allowed,
      });
      await loadCompanyUsers();
      message(
        allowed
            ? 'ALTERAÇÃO DE JUROS, MULTA E DESCONTO LIBERADA.'
            : 'ALTERAÇÃO DE JUROS, MULTA E DESCONTO BLOQUEADA.',
      );
    } catch (e) {
      message(e.toString().replaceFirst('Exception: ', ''));
    }
  }

  List<Widget> reportsPage() {
    return [
      panel(
        'Relatório financeiro por data',
        Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              'Escolha uma data específica ou um período. Para consultar somente um dia, deixe a data inicial e a data final iguais.',
              style: TextStyle(color: mutedText, height: 1.5),
            ),
            const SizedBox(height: 18),
            LayoutBuilder(
              builder: (context, constraints) => Wrap(
                spacing: 12,
                runSpacing: 12,
                crossAxisAlignment: WrapCrossAlignment.end,
                children: [
                  SizedBox(
                    width: constraints.maxWidth < 560
                        ? constraints.maxWidth
                        : 235,
                    child: reportDateField(
                      'Data inicial',
                      reportStart,
                      () => pickReportDate(true),
                    ),
                  ),
                  SizedBox(
                    width: constraints.maxWidth < 560
                        ? constraints.maxWidth
                        : 235,
                    child: reportDateField(
                      'Data final',
                      reportEnd,
                      () => pickReportDate(false),
                    ),
                  ),
                  FilledButton.icon(
                    onPressed: reportBusy ? null : saveFinancialReport,
                    icon: reportBusy
                        ? const SizedBox(
                            width: 18,
                            height: 18,
                            child: CircularProgressIndicator(strokeWidth: 2),
                          )
                        : const Icon(Icons.picture_as_pdf_outlined),
                    label: Text(reportBusy ? 'Gerando...' : 'Gerar PDF'),
                  ),
                ],
              ),
            ),
            const SizedBox(height: 22),
            const Divider(),
            const SizedBox(height: 12),
            const Wrap(
              spacing: 28,
              runSpacing: 12,
              children: [
                ReportFeature(
                  icon: Icons.assessment_outlined,
                  title: 'Resumo do período',
                  text: 'Recebido, pago, resultado e valores em aberto.',
                ),
                ReportFeature(
                  icon: Icons.payments_outlined,
                  title: 'Baixas realizadas',
                  text: 'Pagamentos e recebimentos que movimentaram o caixa.',
                ),
                ReportFeature(
                  icon: Icons.list_alt_outlined,
                  title: 'Lançamentos',
                  text: 'Contas do período com valor, saldo e situação.',
                ),
              ],
            ),
          ],
        ),
      ),
      const SizedBox(height: 22),
      panel(
        'Relatórios detalhados e exportação',
        Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              'Os relatórios abaixo usam o mesmo período escolhido acima. Você pode salvar em PDF ou CSV.',
              style: TextStyle(color: mutedText),
            ),
            const SizedBox(height: 16),
            ...const {
              'overdue': 'Títulos vencidos e dias de atraso',
              'party': 'Histórico por cliente e fornecedor',
              'cost-center': 'Receitas e despesas por centro de custo',
              'account': 'Extrato por conta financeira',
              'cash-flow': 'Fluxo de caixa previsto e realizado',
            }.entries.map(
              (item) => ListTile(
                contentPadding: EdgeInsets.zero,
                leading: const Icon(Icons.description_outlined, color: teal),
                title: Text(item.value),
                trailing: Wrap(
                  spacing: 8,
                  children: [
                    OutlinedButton.icon(
                      onPressed: reportBusy
                          ? null
                          : () => saveAdvancedReport(item.key, false),
                      icon: const Icon(Icons.picture_as_pdf_outlined),
                      label: const Text('PDF'),
                    ),
                    OutlinedButton.icon(
                      onPressed: reportBusy
                          ? null
                          : () => saveAdvancedReport(item.key, true),
                      icon: const Icon(Icons.table_view_outlined),
                      label: const Text('CSV'),
                    ),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    ];
  }

  Widget reportDateField(String label, DateTime value, VoidCallback onTap) =>
      InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: InputDecorator(
          decoration: InputDecoration(
            labelText: label,
            prefixIcon: const Icon(Icons.calendar_month_outlined),
          ),
          child: Text(
            dateLabel(iso(value)),
            style: const TextStyle(fontWeight: FontWeight.w600),
          ),
        ),
      );

  Future<void> pickReportDate(bool initial) async {
    final current = initial ? reportStart : reportEnd;
    final picked = await showDatePicker(
      context: context,
      initialDate: current,
      firstDate: DateTime(2000),
      lastDate: DateTime(2100),
      helpText: initial ? 'DATA INICIAL' : 'DATA FINAL',
      cancelText: 'VOLTAR',
      confirmText: 'ESCOLHER',
    );
    if (picked == null || !mounted) return;
    setState(() {
      if (initial) {
        reportStart = DateUtils.dateOnly(picked);
        if (reportEnd.isBefore(reportStart)) reportEnd = reportStart;
      } else {
        reportEnd = DateUtils.dateOnly(picked);
        if (reportStart.isAfter(reportEnd)) reportStart = reportEnd;
      }
    });
  }

  Future<void> askToOpenPdf(
    Uint8List bytes,
    String fileName, {
    String? savedPath,
  }) async {
    if (!mounted) return;
    final openNow = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        icon: const Icon(Icons.picture_as_pdf_outlined, color: teal, size: 34),
        title: const Text('PDF GERADO COM SUCESSO'),
        content: Text(
          'O arquivo $fileName foi gerado. Deseja abrir o PDF agora?',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('Agora não'),
          ),
          FilledButton.icon(
            onPressed: () => Navigator.pop(dialogContext, true),
            icon: const Icon(Icons.open_in_new),
            label: const Text('Abrir PDF'),
          ),
        ],
      ),
    );
    if (openNow == true) {
      await openGeneratedPdf(bytes, fileName, savedPath: savedPath);
    }
  }

  Future<void> saveFinancialReport() async {
    if (reportBusy) return;
    setState(() => reportBusy = true);
    try {
      final uri = Uri.parse(
        '$serviceOrigin/api/companies/$company/reports/financial?start=${iso(reportStart)}&end=${iso(reportEnd)}',
      );
      final response = await api.client
          .get(
            uri,
            headers: {
              if (api.token != null) 'Authorization': 'Bearer ${api.token}',
            },
          )
          .timeout(const Duration(seconds: 30));
      if (response.statusCode != 200) {
        throw Exception('Não foi possível gerar o relatório.');
      }
      final periodName = reportStart == reportEnd
          ? iso(reportStart)
          : '${iso(reportStart)}-a-${iso(reportEnd)}';
      final fileName = 'relatorio-financeiro-$periodName.pdf';
      final file = XFile.fromData(
        response.bodyBytes,
        mimeType: 'application/pdf',
        name: fileName,
      );
      String? savedPath;
      if (kIsWeb) {
        await saveBrowserDownload(
          response.bodyBytes,
          fileName,
          'application/pdf',
        );
      } else {
        final location = await getSaveLocation(
          suggestedName: fileName,
          acceptedTypeGroups: const [
            XTypeGroup(label: 'Documento PDF', extensions: ['pdf']),
          ],
        );
        if (location == null) return;
        await file.saveTo(location.path);
        savedPath = location.path;
      }
      await askToOpenPdf(response.bodyBytes, fileName, savedPath: savedPath);
      message('Relatório em PDF gerado com sucesso.');
    } catch (error) {
      message(error.toString().replaceFirst('Exception: ', ''));
    } finally {
      if (mounted) setState(() => reportBusy = false);
    }
  }

  Future<void> saveAdvancedReport(String type, bool csv) async {
    if (reportBusy) return;
    setState(() => reportBusy = true);
    try {
      final route = csv ? 'exports/csv' : 'reports/advanced';
      final uri = Uri.parse(
        '$serviceOrigin/api/companies/$company/$route?type=$type&start=${iso(reportStart)}&end=${iso(reportEnd)}',
      );
      final response = await api.client
          .get(
            uri,
            headers: {
              if (api.token != null) 'Authorization': 'Bearer ${api.token}',
            },
          )
          .timeout(const Duration(seconds: 30));
      if (response.statusCode != 200) {
        throw Exception('Não foi possível gerar o arquivo.');
      }
      final extension = csv ? 'csv' : 'pdf';
      final fileName =
          '$type-${iso(reportStart)}-a-${iso(reportEnd)}.$extension';
      String? savedPath;
      if (kIsWeb) {
        await saveBrowserDownload(
          response.bodyBytes,
          fileName,
          csv ? 'text/csv;charset=utf-8' : 'application/pdf',
        );
      } else {
        final location = await getSaveLocation(
          suggestedName: fileName,
          acceptedTypeGroups: [
            XTypeGroup(
              label: csv ? 'Planilha CSV' : 'Documento PDF',
              extensions: [extension],
            ),
          ],
        );
        if (location == null) return;
        await XFile.fromData(
          response.bodyBytes,
          mimeType: csv ? 'text/csv' : 'application/pdf',
          name: fileName,
        ).saveTo(location.path);
        savedPath = location.path;
      }
      if (!csv) {
        await askToOpenPdf(response.bodyBytes, fileName, savedPath: savedPath);
      }
      message('${csv ? 'CSV' : 'PDF'} gerado com sucesso.');
    } catch (e) {
      message(e.toString().replaceFirst('Exception: ', ''));
    } finally {
      if (mounted) setState(() => reportBusy = false);
    }
  }

  String longMonthLabel(String value) {
    const names = [
      'JANEIRO',
      'FEVEREIRO',
      'MARÇO',
      'ABRIL',
      'MAIO',
      'JUNHO',
      'JULHO',
      'AGOSTO',
      'SETEMBRO',
      'OUTUBRO',
      'NOVEMBRO',
      'DEZEMBRO',
    ];
    final parts = value.split('-');
    return '${names[int.parse(parts[1]) - 1]} DE ${parts[0]}';
  }

  Future<void> showTitleReports(String kind) async {
    final receivable = kind == 'receivable';
    final options = <String, (String, String)>{
      receivable ? 'receipts' : 'payments': (
        receivable ? 'Extrato de recebimentos' : 'Extrato de pagamentos',
        receivable
            ? 'Tudo que foi efetivamente recebido no mês selecionado.'
            : 'Tudo que foi efetivamente pago no mês selecionado.',
      ),
      'by-plan': (
        receivable
            ? 'Contas a receber por plano de contas'
            : 'Contas a pagar por plano de contas',
        'Parcelas com saldo em aberto no mês, separadas pelo plano de contas.',
      ),
      'month': (
        receivable
            ? 'Extrato de contas a receber'
            : 'Extrato de contas a pagar',
        'Somente parcelas com saldo em aberto e vencimento no mês selecionado.',
      ),
      'future': (
        receivable
            ? 'Contas a receber dos próximos meses'
            : 'Contas dos próximos meses',
        'Parcelas com saldo em aberto após o mês selecionado, separadas por mês.',
      ),
    };
    await showDialog<void>(
      context: context,
      builder: (dialogContext) => SimpleDialog(
        title: Text(
          'RELATÓRIOS ${receivable ? 'A RECEBER' : 'A PAGAR'} • ${longMonthLabel(selectedMonth)}',
        ),
        children: options.entries
            .map(
              (option) => SimpleDialogOption(
                onPressed: () {
                  Navigator.pop(dialogContext);
                  saveTitleReport(kind, option.key);
                },
                padding: const EdgeInsets.symmetric(
                  horizontal: 24,
                  vertical: 14,
                ),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    const Icon(Icons.picture_as_pdf_outlined, color: teal),
                    const SizedBox(width: 13),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            option.value.$1,
                            style: const TextStyle(fontWeight: FontWeight.w600),
                          ),
                          const SizedBox(height: 4),
                          Text(
                            option.value.$2,
                            style: TextStyle(
                              color: mutedText,
                              fontSize: 12,
                              height: 1.35,
                            ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
            )
            .toList(),
      ),
    );
  }

  Future<void> saveTitleReport(String kind, String type) async {
    if (reportBusy) return;
    final receivable = kind == 'receivable';
    final slugs = receivable
        ? const {
            'receipts': 'extrato-recebimentos',
            'by-plan': 'contas-receber-por-plano',
            'month': 'extrato-contas-receber',
            'future': 'contas-receber-proximos-meses',
          }
        : const {
            'payments': 'extrato-pagamentos',
            'by-plan': 'contas-pagar-por-plano',
            'month': 'extrato-contas-pagar',
            'future': 'contas-pagar-proximos-meses',
          };
    setState(() => reportBusy = true);
    try {
      final uri = Uri.parse(
        '$serviceOrigin/api/companies/$company/reports/${receivable ? 'receivables' : 'payables'}?month=$selectedMonth&type=$type',
      );
      final response = await api.client
          .get(
            uri,
            headers: {
              if (api.token != null) 'Authorization': 'Bearer ${api.token}',
            },
          )
          .timeout(const Duration(seconds: 30));
      if (response.statusCode != 200) {
        throw Exception('Não foi possível gerar o relatório.');
      }
      final fileName = '${slugs[type]}-$selectedMonth.pdf';
      final file = XFile.fromData(
        response.bodyBytes,
        mimeType: 'application/pdf',
        name: fileName,
      );
      String? savedPath;
      if (kIsWeb) {
        await saveBrowserDownload(
          response.bodyBytes,
          fileName,
          'application/pdf',
        );
      } else {
        final location = await getSaveLocation(
          suggestedName: fileName,
          acceptedTypeGroups: const [
            XTypeGroup(label: 'Documento PDF', extensions: ['pdf']),
          ],
        );
        if (location == null) return;
        await file.saveTo(location.path);
        savedPath = location.path;
      }
      await askToOpenPdf(response.bodyBytes, fileName, savedPath: savedPath);
      message('Relatório em PDF gerado com sucesso.');
    } catch (error) {
      message(error.toString().replaceFirst('Exception: ', ''));
    } finally {
      if (mounted) setState(() => reportBusy = false);
    }
  }

  List<Widget> historyPage() {
    final filtered = audit.where((e) {
      final created = DateTime.tryParse(
        '${(e['created'] as String).replaceFirst(' ', 'T')}Z',
      )?.toLocal();
      final sameDay =
          historyDay == null || (created != null && iso(created) == historyDay);
      final text =
          '${e['action']} ${historyText(e, titles, accounts, settlements)}'
              .toLowerCase();
      return sameDay && text.contains(historySearch.toLowerCase());
    }).toList();
    return [
      Row(
        children: [
          Expanded(
            child: TextField(
              decoration: const InputDecoration(
                hintText: 'Pesquisar no histórico',
                prefixIcon: Icon(Icons.search),
              ),
              onChanged: (v) => setState(() => historySearch = v),
            ),
          ),
          const SizedBox(width: 12),
          OutlinedButton.icon(
            icon: const Icon(Icons.calendar_today_outlined),
            label: Text(
              historyDay == null ? 'Todas as datas' : dateLabel(historyDay!),
            ),
            onPressed: () async {
              final picked = await showDatePicker(
                context: context,
                initialDate: historyDay == null
                    ? DateTime.now()
                    : DateTime.parse(historyDay!),
                firstDate: DateTime(2000),
                lastDate: DateTime(2100),
              );
              if (picked != null) setState(() => historyDay = iso(picked));
            },
          ),
          if (historyDay != null)
            TextButton(
              onPressed: () => setState(() => historyDay = null),
              child: const Text('Todas'),
            ),
        ],
      ),
      const SizedBox(height: 20),
      panel(
        'Operações encontradas',
        filtered.isEmpty
            ? empty('Nenhuma operação encontrada para esta data e pesquisa.')
            : Column(
                children: filtered
                    .map(
                      (e) => ListTile(
                        leading: const Icon(
                          Icons.check_circle_outline,
                          color: teal,
                        ),
                        title: Text(e['action']),
                        subtitle: Text(
                          historyText(e, titles, accounts, settlements),
                        ),
                        trailing: SizedBox(
                          width: 140,
                          child: Text(
                            historyDate(e['created']),
                            textAlign: TextAlign.right,
                            style: const TextStyle(fontSize: 11),
                          ),
                        ),
                      ),
                    )
                    .toList(),
              ),
      ),
    ];
  }

  Widget panel(String title, Widget child, {Widget? action}) => Card(
    margin: EdgeInsets.zero,
    child: Padding(
      padding: const EdgeInsets.all(23),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  title,
                  style: TextStyle(
                    fontSize: 17,
                    fontWeight: FontWeight.w600,
                    color: headingColor,
                  ),
                ),
              ),
              ?action,
            ],
          ),
          const SizedBox(height: 18),
          child,
        ],
      ),
    ),
  );

  Widget registrySection({
    required String title,
    required String subtitle,
    required IconData icon,
    required Widget child,
    VoidCallback? onAdd,
    String? addLabel,
  }) => Card(
    margin: EdgeInsets.zero,
    clipBehavior: Clip.antiAlias,
    child: ExpansionTile(
      leading: Container(
        width: 44,
        height: 44,
        decoration: BoxDecoration(
          color: teal.withValues(alpha: darkMode ? .32 : .09),
          borderRadius: BorderRadius.circular(12),
        ),
        child: Icon(icon, color: darkMode ? Colors.white : teal),
      ),
      title: Text(
        title,
        style: TextStyle(fontWeight: FontWeight.w700, color: headingColor),
      ),
      subtitle: Text(subtitle),
      tilePadding: const EdgeInsets.symmetric(horizontal: 20, vertical: 7),
      childrenPadding: const EdgeInsets.fromLTRB(20, 0, 20, 20),
      expandedCrossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const Divider(height: 8),
        if (onAdd != null) ...[
          Align(
            alignment: Alignment.centerLeft,
            child: FilledButton.icon(
              onPressed: onAdd,
              icon: const Icon(Icons.add),
              label: Text(addLabel!),
            ),
          ),
          const SizedBox(height: 12),
        ],
        child,
      ],
    ),
  );
  Widget empty(String text) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 34),
    child: Column(
      children: [
        const Icon(Icons.inbox_outlined, color: Color(0xFF9AABA0), size: 35),
        const SizedBox(height: 12),
        Text(
          text,
          textAlign: TextAlign.center,
          style: TextStyle(color: mutedText),
        ),
      ],
    ),
  );
  Widget metric(
    String label,
    num value,
    IconData icon,
    Color color,
    String note,
  ) => Card(
    margin: const EdgeInsets.only(right: 10),
    child: Padding(
      padding: const EdgeInsets.all(20),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(icon, size: 19, color: color),
              const SizedBox(width: 8),
              Flexible(
                child: Text(
                  label,
                  style: TextStyle(color: mutedText, fontSize: 12),
                ),
              ),
            ],
          ),
          const SizedBox(height: 16),
          FittedBox(
            child: Text(
              money(value),
              style: TextStyle(
                fontSize: 25,
                fontWeight: FontWeight.w700,
                color: color,
                letterSpacing: -.5,
              ),
            ),
          ),
          const SizedBox(height: 8),
          Text(note, style: TextStyle(fontSize: 10, color: faintText)),
        ],
      ),
    ),
  );
  Widget metricGroup(List<Widget> cards) => LayoutBuilder(
    builder: (context, constraints) => constraints.maxWidth < 760
        ? Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: cards
                .map(
                  (card) => Padding(
                    padding: const EdgeInsets.only(bottom: 10),
                    child: card,
                  ),
                )
                .toList(),
          )
        : Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: cards
                .map(
                  (card) => Expanded(
                    child: Padding(
                      padding: const EdgeInsets.only(right: 10),
                      child: card,
                    ),
                  ),
                )
                .toList(),
          ),
  );

  Future<void> showReceivablesMonitor() async {
    final receivables = titles
        .where(
          (title) => title['kind'] == 'receivable' && title['cancelled'] == 0,
        )
        .toList();
    if (receivables.isEmpty) {
      message('AINDA NÃO HÁ CONTAS A RECEBER PARA MONITORAR.');
      return;
    }

    final today = DateUtils.dateOnly(DateTime.now());
    final monthStart = DateTime(today.year, today.month, 1);
    final nextMonth = DateTime(today.year, today.month + 1, 1);
    final grouped = <String, List<RecordData>>{};
    for (final title in receivables) {
      final partyId = title['partyId'];
      final name = '${title['partyName'] ?? 'SEM CLIENTE VINCULADO'}';
      final key = partyId == null ? 'SEM CLIENTE:$name' : 'CLIENTE:$partyId';
      grouped.putIfAbsent(key, () => []).add(title);
    }

    RecordData? partyFor(List<RecordData> clientTitles) {
      final partyId = clientTitles.first['partyId'];
      if (partyId == null) return null;
      for (final party in parties) {
        if (party['id'] == partyId) return party;
      }
      return null;
    }

    num cashValue(RecordData settlement) =>
        (settlement['principal'] as num) +
        (settlement['interest'] as num) +
        (settlement['penalty'] as num) +
        ((settlement['additional'] ?? 0) as num) -
        (settlement['discount'] as num);

    final clients =
        grouped.entries.map((entry) {
          final clientTitles = entry.value;
          final party = partyFor(clientTitles);
          final openTitles =
              clientTitles
                  .where((title) => (title['remaining'] as num) > 0)
                  .toList()
                ..sort((a, b) => '${a['due']}'.compareTo('${b['due']}'));
          final receivedTitles =
              clientTitles
                  .where((title) => (title['remaining'] as num) <= 0)
                  .toList()
                ..sort((a, b) => '${b['due']}'.compareTo('${a['due']}'));
          final ids = clientTitles.map((title) => title['id']).toSet();
          final clientSettlements = settlements
              .where(
                (settlement) =>
                    settlement['reversed'] != 1 &&
                    ids.contains(settlement['titleId']),
              )
              .toList();
          final overdueTitles = openTitles.where(
            (title) => DateTime.parse('${title['due']}').isBefore(today),
          );
          var maximumDelay = 0;
          for (final title in overdueTitles) {
            final days = today
                .difference(DateTime.parse('${title['due']}'))
                .inDays;
            if (days > maximumDelay) maximumDelay = days;
          }
          final openAmount = openTitles.fold<num>(
            0,
            (sum, title) => sum + (title['remaining'] as num),
          );
          final overdueAmount = overdueTitles.fold<num>(
            0,
            (sum, title) => sum + (title['remaining'] as num),
          );
          final receivedAmount = clientSettlements.fold<num>(
            0,
            (sum, settlement) => sum + cashValue(settlement),
          );
          final receivedThisMonth = clientSettlements
              .where((settlement) {
                final date = DateTime.parse('${settlement['date']}');
                return !date.isBefore(monthStart) && date.isBefore(nextMonth);
              })
              .fold<num>(0, (sum, settlement) => sum + cashValue(settlement));
          return <String, dynamic>{
            'key': entry.key,
            'name':
                '${clientTitles.first['partyName'] ?? 'SEM CLIENTE VINCULADO'}',
            'party': party,
            'titles': clientTitles,
            'openTitles': openTitles,
            'receivedTitles': receivedTitles,
            'settlements': clientSettlements,
            'openAmount': openAmount,
            'overdueAmount': overdueAmount,
            'receivedAmount': receivedAmount,
            'receivedThisMonth': receivedThisMonth,
            'maximumDelay': maximumDelay,
          };
        }).toList()..sort((a, b) {
          final byDelay = (b['maximumDelay'] as int).compareTo(
            a['maximumDelay'] as int,
          );
          if (byDelay != 0) return byDelay;
          return '${a['name']}'.compareTo('${b['name']}');
        });

    String searchText = '';
    String clientFilter = 'Todos';
    String detailView = 'open';
    String? selectedKey = clients.first['key'] as String;

    Color alertColor(int delay, num openAmount) {
      if (delay > 30) return const Color(0xFFB54235);
      if (delay > 0) return const Color(0xFFD28A20);
      if (openAmount > 0) return const Color(0xFF2E8B67);
      return const Color(0xFF73807D);
    }

    String alertLabel(int delay, num openAmount) {
      if (delay > 30) return 'ATRASO CRÍTICO';
      if (delay > 0) return 'EM ATRASO';
      if (openAmount > 0) return 'EM DIA';
      return 'RECEBIDO';
    }

    final selectedTitle = await showDialog<RecordData>(
      context: context,
      builder: (dialogContext) => StatefulBuilder(
        builder: (dialogContext, setLocal) {
          final query = searchText.trim().toLowerCase();
          final visible = clients.where((client) {
            final party = client['party'] as RecordData?;
            final searchable =
                '${client['name']} ${party?['document'] ?? ''} ${party?['phone'] ?? ''} ${party?['email'] ?? ''} ${(client['titles'] as List<RecordData>).map((title) => title['description']).join(' ')}'
                    .toLowerCase();
            final delay = client['maximumDelay'] as int;
            final openAmount = client['openAmount'] as num;
            final receivedTitles = client['receivedTitles'] as List<RecordData>;
            final matchesFilter = switch (clientFilter) {
              'Em atraso' => delay > 0,
              'Em dia' => delay == 0 && openAmount > 0,
              'Recebidos' => openAmount == 0 && receivedTitles.isNotEmpty,
              _ => true,
            };
            return matchesFilter &&
                (query.isEmpty || searchable.contains(query));
          }).toList();
          if (visible.isNotEmpty &&
              !visible.any((client) => client['key'] == selectedKey)) {
            selectedKey = visible.first['key'] as String;
          }
          final selected = visible
              .where((client) => client['key'] == selectedKey)
              .firstOrNull;

          Widget metricCard(
            String label,
            num value,
            IconData icon,
            Color color,
          ) => Container(
            constraints: const BoxConstraints(minWidth: 165),
            padding: const EdgeInsets.all(16),
            decoration: BoxDecoration(
              color: color.withValues(alpha: .08),
              borderRadius: BorderRadius.circular(16),
              border: Border.all(color: color.withValues(alpha: .18)),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Container(
                  width: 38,
                  height: 38,
                  decoration: BoxDecoration(
                    color: color.withValues(alpha: .13),
                    shape: BoxShape.circle,
                  ),
                  child: Icon(icon, color: color, size: 20),
                ),
                const SizedBox(width: 12),
                Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      label,
                      style: TextStyle(fontSize: 11, color: mutedText),
                    ),
                    const SizedBox(height: 3),
                    Text(
                      money(value),
                      style: TextStyle(
                        fontSize: 17,
                        fontWeight: FontWeight.w800,
                        color: color,
                      ),
                    ),
                  ],
                ),
              ],
            ),
          );

          Widget clientList() => Container(
            width: 365,
            decoration: BoxDecoration(
              color: Theme.of(dialogContext).colorScheme.surfaceContainerLow,
              borderRadius: BorderRadius.circular(18),
              border: Border.all(
                color: Theme.of(dialogContext).dividerColor
                    .withValues(alpha: .4),
              ),
            ),
            child: Column(
              children: [
                Padding(
                  padding: const EdgeInsets.all(14),
                  child: Column(
                    children: [
                      TextField(
                        decoration: const InputDecoration(
                          hintText: 'Buscar cliente, documento ou título',
                          prefixIcon: Icon(Icons.search),
                        ),
                        onChanged: (value) => setLocal(() {
                          searchText = value;
                        }),
                      ),
                      const SizedBox(height: 10),
                      DropdownButtonFormField<String>(
                        initialValue: clientFilter,
                        decoration: const InputDecoration(
                          labelText: 'Mostrar clientes',
                        ),
                        items: const [
                          DropdownMenuItem(
                            value: 'Todos',
                            child: Text('Todos'),
                          ),
                          DropdownMenuItem(
                            value: 'Em atraso',
                            child: Text('Em atraso'),
                          ),
                          DropdownMenuItem(
                            value: 'Em dia',
                            child: Text('Em dia'),
                          ),
                          DropdownMenuItem(
                            value: 'Recebidos',
                            child: Text('Recebidos'),
                          ),
                        ],
                        onChanged: (value) => setLocal(() {
                          clientFilter = value ?? 'Todos';
                        }),
                      ),
                    ],
                  ),
                ),
                Padding(
                  padding: const EdgeInsets.fromLTRB(16, 0, 16, 10),
                  child: Row(
                    children: [
                      Text(
                        '${visible.length} CLIENTE(S)',
                        style: TextStyle(
                          fontSize: 11,
                          fontWeight: FontWeight.w700,
                          color: mutedText,
                        ),
                      ),
                    ],
                  ),
                ),
                Expanded(
                  child: visible.isEmpty
                      ? Center(
                          child: Text(
                            'Nenhum cliente encontrado.',
                            style: TextStyle(color: mutedText),
                          ),
                        )
                      : ListView.separated(
                          padding: const EdgeInsets.fromLTRB(8, 0, 8, 10),
                          itemCount: visible.length,
                          separatorBuilder: (_, _) => const SizedBox(height: 6),
                          itemBuilder: (_, index) {
                            final client = visible[index];
                            final delay = client['maximumDelay'] as int;
                            final openAmount = client['openAmount'] as num;
                            final color = alertColor(delay, openAmount);
                            final active = client['key'] == selectedKey;
                            return Material(
                              color: active
                                  ? teal.withValues(alpha: .10)
                                  : Colors.transparent,
                              borderRadius: BorderRadius.circular(14),
                              child: InkWell(
                                borderRadius: BorderRadius.circular(14),
                                onTap: () => setLocal(() {
                                  selectedKey = client['key'] as String;
                                  detailView =
                                      (client['openTitles'] as List).isEmpty
                                      ? 'received'
                                      : 'open';
                                }),
                                child: Padding(
                                  padding: const EdgeInsets.all(13),
                                  child: Row(
                                    crossAxisAlignment:
                                        CrossAxisAlignment.start,
                                    children: [
                                      Container(
                                        width: 10,
                                        height: 10,
                                        margin: const EdgeInsets.only(top: 5),
                                        decoration: BoxDecoration(
                                          color: color,
                                          shape: BoxShape.circle,
                                        ),
                                      ),
                                      const SizedBox(width: 10),
                                      Expanded(
                                        child: Column(
                                          crossAxisAlignment:
                                              CrossAxisAlignment.start,
                                          children: [
                                            Text(
                                              '${client['name']}',
                                              maxLines: 1,
                                              overflow: TextOverflow.ellipsis,
                                              style: const TextStyle(
                                                fontWeight: FontWeight.w700,
                                              ),
                                            ),
                                            const SizedBox(height: 5),
                                            Text(
                                              alertLabel(delay, openAmount),
                                              style: TextStyle(
                                                color: color,
                                                fontSize: 10,
                                                fontWeight: FontWeight.w800,
                                              ),
                                            ),
                                          ],
                                        ),
                                      ),
                                      const SizedBox(width: 8),
                                      Column(
                                        crossAxisAlignment:
                                            CrossAxisAlignment.end,
                                        children: [
                                          Text(
                                            money(openAmount),
                                            style: const TextStyle(
                                              fontWeight: FontWeight.w700,
                                            ),
                                          ),
                                          if (delay > 0)
                                            Text(
                                              '$delay DIA(S)',
                                              style: TextStyle(
                                                color: color,
                                                fontSize: 10,
                                              ),
                                            ),
                                        ],
                                      ),
                                    ],
                                  ),
                                ),
                              ),
                            );
                          },
                        ),
                ),
              ],
            ),
          );

          Widget compactClientPicker() => Container(
            padding: const EdgeInsets.all(12),
            decoration: BoxDecoration(
              color: Theme.of(dialogContext).colorScheme.surfaceContainerLow,
              borderRadius: BorderRadius.circular(16),
              border: Border.all(
                color: Theme.of(dialogContext).dividerColor
                    .withValues(alpha: .4),
              ),
            ),
            child: Column(
              children: [
                Row(
                  children: [
                    Expanded(
                      child: TextField(
                        decoration: const InputDecoration(
                          hintText: 'Buscar cliente ou título',
                          prefixIcon: Icon(Icons.search),
                        ),
                        onChanged: (value) => setLocal(() {
                          searchText = value;
                        }),
                      ),
                    ),
                    const SizedBox(width: 10),
                    Expanded(
                      child: DropdownButtonFormField<String>(
                        initialValue: clientFilter,
                        decoration: const InputDecoration(
                          labelText: 'Situação',
                        ),
                        items: const [
                          DropdownMenuItem(
                            value: 'Todos',
                            child: Text('Todos'),
                          ),
                          DropdownMenuItem(
                            value: 'Em atraso',
                            child: Text('Em atraso'),
                          ),
                          DropdownMenuItem(
                            value: 'Em dia',
                            child: Text('Em dia'),
                          ),
                          DropdownMenuItem(
                            value: 'Recebidos',
                            child: Text('Recebidos'),
                          ),
                        ],
                        onChanged: (value) => setLocal(() {
                          clientFilter = value ?? 'Todos';
                        }),
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 10),
                DropdownButtonFormField<String>(
                  key: ValueKey('$selectedKey-${visible.length}'),
                  initialValue: selected == null ? null : selectedKey,
                  isExpanded: true,
                  decoration: InputDecoration(
                    labelText: visible.isEmpty
                        ? 'Nenhum cliente encontrado'
                        : 'Cliente (${visible.length})',
                    prefixIcon: const Icon(Icons.person_search_outlined),
                  ),
                  items: visible
                      .map(
                        (client) => DropdownMenuItem<String>(
                          value: client['key'] as String,
                          child: Text(
                            '${client['name']}  •  ${money(client['openAmount'] as num)}',
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                      )
                      .toList(),
                  onChanged: visible.isEmpty
                      ? null
                      : (value) => setLocal(() {
                          selectedKey = value;
                          final client = visible.firstWhere(
                            (item) => item['key'] == value,
                          );
                          detailView = (client['openTitles'] as List).isEmpty
                              ? 'received'
                              : 'open';
                        }),
                ),
              ],
            ),
          );

          Widget clientDetails() {
            if (selected == null) {
              return Expanded(
                child: Center(
                  child: Text(
                    'Selecione um cliente para ver os detalhes.',
                    style: TextStyle(color: mutedText),
                  ),
                ),
              );
            }
            final party = selected['party'] as RecordData?;
            final openTitles = selected['openTitles'] as List<RecordData>;
            final receivedTitles =
                selected['receivedTitles'] as List<RecordData>;
            final displayed = detailView == 'open'
                ? openTitles
                : receivedTitles;
            String latestReceipt(RecordData title) {
              final dates =
                  (selected['settlements'] as List<RecordData>)
                      .where(
                        (settlement) => settlement['titleId'] == title['id'],
                      )
                      .map((settlement) => '${settlement['date']}')
                      .toList()
                    ..sort();
              return dates.isEmpty ? '' : dates.last;
            }

            return Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Container(
                    padding: const EdgeInsets.all(18),
                    decoration: BoxDecoration(
                      color: teal.withValues(alpha: .06),
                      borderRadius: BorderRadius.circular(18),
                    ),
                    child: Row(
                      children: [
                        Container(
                          width: 48,
                          height: 48,
                          decoration: BoxDecoration(
                            color: teal.withValues(alpha: .13),
                            shape: BoxShape.circle,
                          ),
                          child: const Icon(Icons.person_outline, color: teal),
                        ),
                        const SizedBox(width: 14),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                '${selected['name']}',
                                style: const TextStyle(
                                  fontSize: 20,
                                  fontWeight: FontWeight.w800,
                                ),
                              ),
                              const SizedBox(height: 5),
                              Text(
                                [
                                      if ('${party?['document'] ?? ''}'
                                          .isNotEmpty)
                                        maskedDocument('${party?['document']}'),
                                      if ('${party?['phone'] ?? ''}'.isNotEmpty)
                                        '${party?['phone']}',
                                      if ('${party?['email'] ?? ''}'.isNotEmpty)
                                        '${party?['email']}',
                                    ].isEmpty
                                    ? 'SEM DADOS DE CONTATO CADASTRADOS'
                                    : [
                                        if ('${party?['document'] ?? ''}'
                                            .isNotEmpty)
                                          maskedDocument(
                                            '${party?['document']}',
                                          ),
                                        if ('${party?['phone'] ?? ''}'
                                            .isNotEmpty)
                                          '${party?['phone']}',
                                        if ('${party?['email'] ?? ''}'
                                            .isNotEmpty)
                                          '${party?['email']}',
                                      ].join('  •  '),
                                style: TextStyle(
                                  color: mutedText,
                                  fontSize: 12,
                                ),
                              ),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(height: 12),
                  Wrap(
                    spacing: 10,
                    runSpacing: 10,
                    children: [
                      metricCard(
                        'EM ABERTO',
                        selected['openAmount'] as num,
                        Icons.schedule_outlined,
                        teal,
                      ),
                      metricCard(
                        'VENCIDO',
                        selected['overdueAmount'] as num,
                        Icons.warning_amber_rounded,
                        const Color(0xFFB54235),
                      ),
                      metricCard(
                        'RECEBIDO NO MÊS',
                        selected['receivedThisMonth'] as num,
                        Icons.check_circle_outline,
                        const Color(0xFF2E8B67),
                      ),
                    ],
                  ),
                  const SizedBox(height: 14),
                  SegmentedButton<String>(
                    segments: [
                      ButtonSegment(
                        value: 'open',
                        icon: const Icon(Icons.schedule_outlined),
                        label: Text('Em aberto (${openTitles.length})'),
                      ),
                      ButtonSegment(
                        value: 'received',
                        icon: const Icon(Icons.check_circle_outline),
                        label: Text('Recebidas (${receivedTitles.length})'),
                      ),
                    ],
                    selected: {detailView},
                    onSelectionChanged: (value) => setLocal(() {
                      detailView = value.first;
                    }),
                  ),
                  const SizedBox(height: 10),
                  Expanded(
                    child: displayed.isEmpty
                        ? Center(
                            child: Text(
                              detailView == 'open'
                                  ? 'Este cliente não possui parcelas em aberto.'
                                  : 'Este cliente ainda não possui parcelas recebidas.',
                              style: TextStyle(color: mutedText),
                            ),
                          )
                        : ListView.separated(
                            itemCount: displayed.length,
                            separatorBuilder: (_, _) =>
                                const Divider(height: 1),
                            itemBuilder: (_, index) {
                              final title = displayed[index];
                              final due = DateTime.parse('${title['due']}');
                              final delay = today.difference(due).inDays;
                              final isOverdue =
                                  detailView == 'open' && delay > 0;
                              final receivedDate = latestReceipt(title);
                              return ListTile(
                                contentPadding: const EdgeInsets.symmetric(
                                  horizontal: 8,
                                  vertical: 5,
                                ),
                                leading: Container(
                                  width: 40,
                                  height: 40,
                                  decoration: BoxDecoration(
                                    color:
                                        (isOverdue
                                                ? const Color(0xFFB54235)
                                                : const Color(0xFF2E8B67))
                                            .withValues(alpha: .10),
                                    shape: BoxShape.circle,
                                  ),
                                  child: Icon(
                                    isOverdue
                                        ? Icons.priority_high_rounded
                                        : detailView == 'open'
                                        ? Icons.event_outlined
                                        : Icons.check_rounded,
                                    color: isOverdue
                                        ? const Color(0xFFB54235)
                                        : const Color(0xFF2E8B67),
                                  ),
                                ),
                                title: Text(
                                  '${title['description']}',
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: const TextStyle(
                                    fontWeight: FontWeight.w700,
                                  ),
                                ),
                                subtitle: Text(
                                  detailView == 'open'
                                      ? 'VENCIMENTO ${dateLabel('${title['due']}')}${isOverdue ? '  •  $delay DIA(S) EM ATRASO' : ''}'
                                      : 'RECEBIDO${receivedDate.isEmpty ? '' : ' EM ${dateLabel(receivedDate)}'}',
                                  style: TextStyle(
                                    color: isOverdue
                                        ? const Color(0xFFB54235)
                                        : mutedText,
                                    fontSize: 11,
                                  ),
                                ),
                                trailing: Row(
                                  mainAxisSize: MainAxisSize.min,
                                  children: [
                                    Text(
                                      money(
                                        detailView == 'open'
                                            ? title['remaining'] as num
                                            : title['amount'] as num,
                                      ),
                                      style: const TextStyle(
                                        fontWeight: FontWeight.w800,
                                      ),
                                    ),
                                    if (detailView == 'open') ...[
                                      const SizedBox(width: 10),
                                      FilledButton(
                                        onPressed: () =>
                                            Navigator.pop(dialogContext, title),
                                        child: const Text('Receber'),
                                      ),
                                    ],
                                  ],
                                ),
                              );
                            },
                          ),
                  ),
                ],
              ),
            );
          }

          return Dialog(
            insetPadding: const EdgeInsets.all(24),
            child: ConstrainedBox(
              constraints: const BoxConstraints(
                maxWidth: 1180,
                maxHeight: 760,
                minHeight: 560,
              ),
              child: Padding(
                padding: const EdgeInsets.all(22),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Row(
                      children: [
                        Container(
                          width: 46,
                          height: 46,
                          decoration: BoxDecoration(
                            color: teal.withValues(alpha: .12),
                            borderRadius: BorderRadius.circular(14),
                          ),
                          child: const Icon(
                            Icons.monitor_heart_outlined,
                            color: teal,
                          ),
                        ),
                        const SizedBox(width: 14),
                        const Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                'Monitoramento de contas a receber',
                                style: TextStyle(
                                  fontSize: 23,
                                  fontWeight: FontWeight.w800,
                                ),
                              ),
                              SizedBox(height: 3),
                              Text(
                                'Veja quem está em atraso e confira as parcelas de cada cliente.',
                              ),
                            ],
                          ),
                        ),
                        IconButton(
                          tooltip: 'Fechar',
                          onPressed: () => Navigator.pop(dialogContext),
                          icon: const Icon(Icons.close),
                        ),
                      ],
                    ),
                    const SizedBox(height: 16),
                    Wrap(
                      spacing: 16,
                      runSpacing: 8,
                      children: const [
                        MonitorLegend(
                          color: Color(0xFF2E8B67),
                          label: 'EM DIA',
                        ),
                        MonitorLegend(
                          color: Color(0xFFD28A20),
                          label: 'ATÉ 30 DIAS DE ATRASO',
                        ),
                        MonitorLegend(
                          color: Color(0xFFB54235),
                          label: 'MAIS DE 30 DIAS DE ATRASO',
                        ),
                      ],
                    ),
                    const SizedBox(height: 16),
                    Expanded(
                      child: LayoutBuilder(
                        builder: (context, constraints) {
                          if (constraints.maxWidth < 900) {
                            return Column(
                              children: [
                                compactClientPicker(),
                                const SizedBox(height: 12),
                                clientDetails(),
                              ],
                            );
                          }
                          return Row(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              clientList(),
                              const SizedBox(width: 16),
                              clientDetails(),
                            ],
                          );
                        },
                      ),
                    ),
                  ],
                ),
              ),
            ),
          );
        },
      ),
    );
    if (selectedTitle != null && mounted) settleForm(selectedTitle);
  }

  List<Widget> dashboard() {
    final now = DateTime.now();
    final today = iso(now);
    final monthStart = iso(DateTime(now.year, now.month, 1));
    final nextMonth = iso(DateTime(now.year, now.month + 1, 1));
    final payableToday = open.where(
      (e) => e['kind'] == 'payable' && e['due'] == today,
    );
    final receivableToday = open.where(
      (e) => e['kind'] == 'receivable' && e['due'] == today,
    );
    final payableMonth = open.where(
      (e) =>
          e['kind'] == 'payable' &&
          (e['due'] as String).compareTo(monthStart) >= 0 &&
          (e['due'] as String).compareTo(nextMonth) < 0,
    );
    final receivableMonth = open.where(
      (e) =>
          e['kind'] == 'receivable' &&
          (e['due'] as String).compareTo(monthStart) >= 0 &&
          (e['due'] as String).compareTo(nextMonth) < 0,
    );
    num receivedThisMonth = 0;
    num paidThisMonth = 0;
    for (final settlement in settlements) {
      final date = settlement['date'] as String;
      if (settlement['reversed'] == 1 ||
          date.compareTo(monthStart) < 0 ||
          date.compareTo(nextMonth) >= 0) {
        continue;
      }
      RecordData? title;
      for (final candidate in titles) {
        if (candidate['id'] == settlement['titleId']) {
          title = candidate;
          break;
        }
      }
      if (title == null) continue;
      final cash =
          (settlement['principal'] as num) +
          (settlement['interest'] as num) +
          (settlement['penalty'] as num) +
          ((settlement['additional'] ?? 0) as num) -
          (settlement['discount'] as num);
      if (title['kind'] == 'receivable') {
        receivedThisMonth += cash;
      } else if (title['kind'] == 'payable') {
        paidThisMonth += cash;
      }
    }
    final monthlyResult = receivedThisMonth - paidThisMonth;
    final availableBalance = total(accounts, 'balance');
    final nextWeek = iso(now.add(const Duration(days: 7)));
    final upcomingPayables = open.where(
      (e) =>
          e['kind'] == 'payable' &&
          (e['due'] as String).compareTo(today) > 0 &&
          (e['due'] as String).compareTo(nextWeek) <= 0,
    );
    final upcomingReceivables = open.where(
      (e) =>
          e['kind'] == 'receivable' &&
          (e['due'] as String).compareTo(today) > 0 &&
          (e['due'] as String).compareTo(nextWeek) <= 0,
    );
    final overduePayables = open
        .where(
          (e) =>
              e['kind'] == 'payable' &&
              (e['due'] as String).compareTo(today) < 0,
        )
        .toList();
    return [
      LayoutBuilder(
        builder: (context, constraints) => Container(
          constraints: const BoxConstraints(minHeight: 176),
          clipBehavior: Clip.antiAlias,
          decoration: BoxDecoration(
            color: darkMode ? null : const Color(0xFFEFE7D9),
            gradient: darkMode
                ? const LinearGradient(
                    colors: [Color(0xFF102D30), Color(0xFF174A4C)],
                    begin: Alignment.centerLeft,
                    end: Alignment.centerRight,
                  )
                : null,
            borderRadius: BorderRadius.circular(20),
            border: darkMode
                ? Border.all(color: const Color(0xFF2D6564))
                : null,
          ),
          child: Row(
            children: [
              Expanded(
                child: Padding(
                  padding: const EdgeInsets.all(27),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      Text(
                        'Mais controle.\nMais espaço para crescer.',
                        style: TextStyle(
                          fontSize: 25,
                          height: 1.15,
                          color: darkMode ? Colors.white : teal,
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                      const SizedBox(height: 12),
                      Text(
                        financialUser
                            ? 'CONTAS A PAGAR E LANÇAMENTOS DA FILIAL EM UM SÓ LUGAR'
                            : '${open.length} compromissos em aberto • operação financeira do supermercado',
                        style: TextStyle(
                          fontSize: 12,
                          color: darkMode
                              ? const Color(0xFFD8E8E5)
                              : const Color(0xFF58706A),
                        ),
                      ),
                    ],
                  ),
                ),
              ),
              if (constraints.maxWidth >= 650)
                darkMode
                    ? Padding(
                        padding: const EdgeInsets.only(right: 28),
                        child: Image.asset(
                          'assets/monalisa-logo-dark.png',
                          width: 330,
                          height: 105,
                          fit: BoxFit.contain,
                        ),
                      )
                    : Image.asset(
                        'assets/monalisa-transparent.png',
                        width: 225,
                        height: 176,
                        fit: BoxFit.cover,
                        alignment: const Alignment(0, -.1),
                      ),
            ],
          ),
        ),
      ),
      const SizedBox(height: 20),
      metricGroup([
        metric(
          'A pagar hoje',
          total(payableToday, 'remaining'),
          Icons.north_east,
          const Color(0xFFC16A28),
          '${payableToday.length} conta(s) com vencimento hoje',
        ),
        if (!financialUser)
          metric(
            'A receber hoje',
            total(receivableToday, 'remaining'),
            Icons.south_west,
            const Color(0xFF267458),
            '${receivableToday.length} conta(s) com vencimento hoje',
          ),
      ]),
      if (!financialUser) ...[
        const SizedBox(height: 12),
        metricGroup([
          metric(
            'Saldo disponível',
            availableBalance,
            Icons.account_balance_wallet_outlined,
            teal,
            accounts.isEmpty
                ? 'NENHUMA CONTA FINANCEIRA CADASTRADA'
                : accounts
                      .map(
                        (account) =>
                            '${account['name']}: ${money(account['balance'])}',
                      )
                      .join('  •  '),
          ),
          metric(
            'A pagar nos próximos 7 dias',
            total(upcomingPayables, 'remaining'),
            Icons.upcoming_outlined,
            const Color(0xFFC16A28),
            '${upcomingPayables.length} próximo(s) vencimento(s)',
          ),
          metric(
            'A receber nos próximos 7 dias',
            total(upcomingReceivables, 'remaining'),
            Icons.event_available_outlined,
            const Color(0xFF267458),
            '${upcomingReceivables.length} próximo(s) vencimento(s)',
          ),
        ]),
      ],
      const SizedBox(height: 12),
      metricGroup([
        metric(
          'A pagar neste mês',
          total(payableMonth, 'remaining'),
          Icons.calendar_month_outlined,
          const Color(0xFFC16A28),
          '${payableMonth.length} conta(s) de 01/${now.month.toString().padLeft(2, '0')} até o último dia do mês',
        ),
        if (!financialUser)
          metric(
            'A receber neste mês',
            total(receivableMonth, 'remaining'),
            Icons.event_available_outlined,
            const Color(0xFF267458),
            '${receivableMonth.length} conta(s) de 01/${now.month.toString().padLeft(2, '0')} até o último dia do mês',
          ),
      ]),
      const SizedBox(height: 22),
      panel(
        overduePayables.isEmpty
            ? 'Nenhuma conta vencida'
            : 'Atenção: contas a pagar vencidas',
        overduePayables.isEmpty
            ? empty(
                'Tudo certo. Não há contas a pagar com vencimento atrasado.',
              )
            : titleTable(overduePayables, colorRows: true),
        action: TextButton.icon(
          onPressed: () => setState(() {
            page = 1;
            filter = 'Vencido';
            search = '';
          }),
          icon: const Icon(Icons.arrow_forward, size: 17),
          label: Text(
            overduePayables.isEmpty
                ? 'Ver contas a pagar'
                : 'Ver ${overduePayables.length} vencida(s)',
          ),
        ),
      ),
      if (!financialUser) ...[
        const SizedBox(height: 22),
        panel(
          'Resultado das baixas do mês',
          Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                'Mostra somente o dinheiro que realmente entrou e saiu entre 01/${now.month.toString().padLeft(2, '0')} e o último dia do mês.',
                style: TextStyle(fontSize: 12, color: mutedText),
              ),
              const SizedBox(height: 16),
              metricGroup([
                metric(
                  'Recebido no mês',
                  receivedThisMonth,
                  Icons.arrow_downward_rounded,
                  const Color(0xFF267458),
                  'Soma das baixas em contas a receber',
                ),
                metric(
                  'Pago no mês',
                  paidThisMonth,
                  Icons.arrow_upward_rounded,
                  const Color(0xFFC16A28),
                  'Soma das baixas em contas a pagar',
                ),
                metric(
                  'Resultado do mês',
                  monthlyResult,
                  monthlyResult < 0
                      ? Icons.trending_down_rounded
                      : Icons.trending_up_rounded,
                  monthlyResult < 0
                      ? const Color(0xFFB23A3A)
                      : const Color(0xFF267458),
                  'Recebido menos pago',
                ),
              ]),
            ],
          ),
        ),
      ],
    ];
  }

  List<Widget> titlePage() {
    final kind = page == 1 ? 'payable' : 'receivable';
    final availableMonths = <String>{
      for (var i = 0; i < 12; i++)
        iso(DateTime(DateTime.now().year, DateTime.now().month + i, 1))
            .substring(0, 7),
      ...titles
          .where(
            (e) =>
                e['kind'] == kind && e['cancelled'] == 0 && e['remaining'] > 0,
          )
          .map((e) => (e['due'] as String).substring(0, 7)),
    }.toList()..sort();
    String monthLabel(String value) {
      const names = [
        'JAN',
        'FEV',
        'MAR',
        'ABR',
        'MAI',
        'JUN',
        'JUL',
        'AGO',
        'SET',
        'OUT',
        'NOV',
        'DEZ',
      ];
      final parts = value.split('-');
      return '${names[int.parse(parts[1]) - 1]}/${parts[0].substring(2)}';
    }

    final list =
        titles
            .where(
              (e) =>
                  e['kind'] == kind &&
                  (e['due'] as String).startsWith(selectedMonth) &&
                  '${e['description']} ${e['partyName'] ?? ''} ${e['category']} ${e['documentNumber'] ?? ''} ${e['branch'] ?? ''} ${e['costCenter'] ?? ''}'
                      .toLowerCase()
                      .contains(search.toLowerCase()) &&
                  (categoryFilter.isEmpty || e['category'] == categoryFilter) &&
                  (costCenterFilter.isEmpty ||
                      e['costCenter'] == costCenterFilter) &&
                  (filter == 'Todos' ||
                      (filter == 'Em aberto'
                          ? e['cancelled'] == 0 && e['remaining'] > 0
                          : status(e) == filter)),
            )
            .toList()
          ..sort((a, b) {
            if (sortTitles == 'value') {
              return (a['amount'] as num).compareTo(b['amount'] as num);
            }
            if (sortTitles == 'name') {
              return '${a['partyName'] ?? a['description']}'.compareTo(
                '${b['partyName'] ?? b['description']}',
              );
            }
            return (a['due'] as String).compareTo(b['due'] as String);
          });
    return [
      Row(
        children: [
          Expanded(
            child: TextField(
              key: ValueKey(page),
              decoration: const InputDecoration(
                hintText: 'Buscar descrição, pessoa ou categoria',
                prefixIcon: Icon(Icons.search),
              ),
              onChanged: (v) => setState(() => search = v),
            ),
          ),
          const SizedBox(width: 15),
          SizedBox(
            width: 185,
            child: DropdownButtonFormField<String>(
              initialValue: filter,
              decoration: const InputDecoration(labelText: 'Situação'),
              items: [
                'Todos',
                'Em aberto',
                'Parcial',
                'Vencido',
                'Pago',
                'Cancelado',
              ].map((s) => DropdownMenuItem(value: s, child: Text(s))).toList(),
              onChanged: (v) => setState(() => filter = v!),
            ),
          ),
        ],
      ),
      const SizedBox(height: 12),
      Wrap(
        spacing: 12,
        runSpacing: 12,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          SizedBox(
            width: 230,
            child: DropdownButtonFormField<String>(
              key: ValueKey('category-$categoryFilter'),
              initialValue: categoryFilter,
              decoration: const InputDecoration(labelText: 'Plano de contas'),
              items: [
                const DropdownMenuItem(value: '', child: Text('Todos')),
                ...plans.map(
                  (p) => DropdownMenuItem<String>(
                    value: '${p['name']}',
                    child: Text(
                      '${p['name']}',
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                ),
              ],
              onChanged: (value) =>
                  setState(() => categoryFilter = value ?? ''),
            ),
          ),
          SizedBox(
            width: 220,
            child: DropdownButtonFormField<String>(
              key: ValueKey('cost-$costCenterFilter'),
              initialValue: costCenterFilter,
              decoration: const InputDecoration(labelText: 'Centro de custo'),
              items: [
                const DropdownMenuItem(value: '', child: Text('Todos')),
                ...costCenters.map(
                  (p) => DropdownMenuItem<String>(
                    value: '${p['name']}',
                    child: Text(
                      '${p['name']}',
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                ),
              ],
              onChanged: (value) =>
                  setState(() => costCenterFilter = value ?? ''),
            ),
          ),
          SizedBox(
            width: 190,
            child: DropdownButtonFormField<String>(
              key: ValueKey('sort-$sortTitles'),
              initialValue: sortTitles,
              decoration: const InputDecoration(labelText: 'Ordenar por'),
              items: const [
                DropdownMenuItem(value: 'due', child: Text('Vencimento')),
                DropdownMenuItem(value: 'value', child: Text('Valor')),
                DropdownMenuItem(value: 'name', child: Text('Nome')),
              ],
              onChanged: (value) => setState(() => sortTitles = value ?? 'due'),
            ),
          ),
          if (selectedTitleIds.length >= 2)
            FilledButton(
              onPressed: bulkSettleForm,
              style: FilledButton.styleFrom(
                backgroundColor: page == 1 ? const Color(0xFF0A5559) : teal,
                foregroundColor: Colors.white,
                elevation: 3,
                shadowColor: teal.withValues(alpha: .28),
                padding: const EdgeInsets.fromLTRB(18, 13, 12, 13),
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(15),
                ),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  const Icon(Icons.playlist_add_check_circle_outlined),
                  const SizedBox(width: 10),
                  Text(
                    page == 1
                        ? 'Baixar parcelas selecionadas'
                        : 'Receber parcelas selecionadas',
                    style: const TextStyle(fontWeight: FontWeight.w700),
                  ),
                  const SizedBox(width: 10),
                  Container(
                    constraints: const BoxConstraints(minWidth: 30),
                    padding: const EdgeInsets.symmetric(
                      horizontal: 8,
                      vertical: 5,
                    ),
                    decoration: BoxDecoration(
                      color: Colors.white.withValues(alpha: .18),
                      borderRadius: BorderRadius.circular(20),
                    ),
                    alignment: Alignment.center,
                    child: Text(
                      '${selectedTitleIds.length}',
                      style: const TextStyle(fontWeight: FontWeight.w800),
                    ),
                  ),
                ],
              ),
            ),
        ],
      ),
      if (page == 1) ...[
        const SizedBox(height: 14),
        Wrap(
          spacing: 18,
          runSpacing: 8,
          children: ['Em aberto', 'Vencido', 'Parcial', 'Pago', 'Cancelado']
              .map(
                (s) => Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Container(
                      width: 10,
                      height: 10,
                      decoration: BoxDecoration(
                        color: statusColor(s),
                        shape: BoxShape.circle,
                      ),
                    ),
                    const SizedBox(width: 6),
                    Text(s, style: TextStyle(fontSize: 12, color: mutedText)),
                  ],
                ),
              )
              .toList(),
        ),
      ],
      const SizedBox(height: 20),
      panel(
        '${list.length} lançamentos',
        titleTable(list, colorRows: page == 1),
        action: Text(
          'Em aberto: ${money(total(list.where((e) => e['cancelled'] == 0), 'remaining'))}',
          style: const TextStyle(fontWeight: FontWeight.w600, color: teal),
        ),
      ),
      const SizedBox(height: 16),
      SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: SegmentedButton<String>(
          segments: availableMonths
              .map((m) => ButtonSegment(value: m, label: Text(monthLabel(m))))
              .toList(),
          selected: {selectedMonth},
          onSelectionChanged: (value) =>
              setState(() => selectedMonth = value.first),
        ),
      ),
    ];
  }

  Widget titleTable(List<RecordData> list, {bool colorRows = false}) {
    if (list.isEmpty) {
      return empty('Nenhum lançamento por aqui. Comece pelo botão acima.');
    }
    final eligibleIds = selectableTitleIds(list);
    final selectAllValue = selectAllState(eligibleIds, selectedTitleIds);
    return LayoutBuilder(
      builder: (context, constraints) => SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: ConstrainedBox(
          constraints: BoxConstraints(minWidth: constraints.maxWidth),
          child: DataTable(
            horizontalMargin: 0,
            columnSpacing: 23,
            headingRowHeight: 38,
            dataRowMinHeight: 65,
            dataRowMaxHeight: 65,
            columns: [
              DataColumn(
                label: Tooltip(
                  message: selectAllValue == true
                      ? 'Desmarcar todas as contas exibidas'
                      : 'Selecionar todas as contas em aberto exibidas',
                  child: Checkbox(
                    tristate: true,
                    value: selectAllValue,
                    onChanged: eligibleIds.isEmpty
                        ? null
                        : (checked) => setState(() {
                            if (checked == true) {
                              selectedTitleIds.addAll(eligibleIds);
                            } else {
                              selectedTitleIds.removeAll(eligibleIds);
                            }
                          }),
                  ),
                ),
              ),
              for (final label in [
                'Descrição / pessoa',
                'Vencimento',
                'Valor',
                'Em aberto',
                'Situação',
                '',
              ])
                DataColumn(
                  label: Text(
                    label,
                    style: TextStyle(color: mutedText, fontSize: 12),
                  ),
                ),
            ],
            rows: list
                .map(
                  (t) => DataRow(
                    color: colorRows
                        ? WidgetStatePropertyAll(
                            statusColor(status(t)).withValues(alpha: .035),
                          )
                        : null,
                    cells: [
                      DataCell(
                        Checkbox(
                          value: selectedTitleIds.contains(t['id']),
                          onChanged: t['cancelled'] == 0 && t['remaining'] > 0
                              ? (checked) => setState(() {
                                  if (checked == true) {
                                    selectedTitleIds.add(t['id']);
                                  } else {
                                    selectedTitleIds.remove(t['id']);
                                  }
                                })
                              : null,
                        ),
                      ),
                      DataCell(
                        SizedBox(
                          width: 215,
                          child: Column(
                            mainAxisAlignment: MainAxisAlignment.center,
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                t['description'],
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                                style: TextStyle(
                                  fontWeight: FontWeight.w600,
                                  fontSize: 13,
                                ),
                              ),
                              const SizedBox(height: 4),
                              Text(
                                [
                                  t['partyName'] ?? 'SEM PESSOA VINCULADA',
                                  if ((t['documentNumber'] ?? '')
                                      .toString()
                                      .isNotEmpty)
                                    t['documentNumber'],
                                  if ((t['costCenter'] ?? '')
                                      .toString()
                                      .isNotEmpty)
                                    t['costCenter'],
                                  if ((t['branch'] ?? '').toString().isNotEmpty)
                                    t['branch'],
                                ].join(' • '),
                                style: TextStyle(
                                  fontSize: 11,
                                  color: faintText,
                                ),
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                              ),
                            ],
                          ),
                        ),
                      ),
                      DataCell(
                        Text(
                          dateLabel(t['due']),
                          style: const TextStyle(fontSize: 12),
                        ),
                      ),
                      DataCell(
                        Text(
                          money(t['amount']),
                          style: const TextStyle(fontSize: 12),
                        ),
                      ),
                      DataCell(
                        Text(
                          money(t['remaining']),
                          style: const TextStyle(
                            fontSize: 12,
                            fontWeight: FontWeight.w600,
                          ),
                        ),
                      ),
                      DataCell(badge(status(t))),
                      DataCell(
                        Row(
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            if (t['cancelled'] == 0 && t['remaining'] > 0)
                              TextButton(
                                onPressed: () => settleForm(t),
                                child: Text(
                                  t['kind'] == 'payable' ? 'Pagar' : 'Receber',
                                ),
                              ),
                            IconButton(
                              tooltip: 'Detalhes e histórico',
                              icon: const Icon(Icons.more_horiz),
                              onPressed: () => details(t),
                            ),
                          ],
                        ),
                      ),
                    ],
                  ),
                )
                .toList(),
          ),
        ),
      ),
    );
  }

  Widget badge(String text) {
    final color = statusColor(text);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
      decoration: BoxDecoration(
        color: color.withValues(alpha: .09),
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        text,
        style: TextStyle(
          color: color,
          fontSize: 11,
          fontWeight: FontWeight.w600,
        ),
      ),
    );
  }

  Color statusColor(String text) => switch (text) {
    'Vencido' => const Color(0xFFB54235),
    'Pago' => const Color(0xFF23734D),
    'Parcial' => const Color(0xFF276BA6),
    'Cancelado' => const Color(0xFF70757A),
    _ => const Color(0xFFC06A16),
  };

  List<Widget> cashPage() {
    final movementQuery = cashMovementSearch.trim().toLowerCase();
    final filteredMovements = movements.where((movement) {
      final amount = movement['amount'] as num;
      final matchesType =
          cashMovementType == 'all' ||
          (cashMovementType == 'in' && amount >= 0) ||
          (cashMovementType == 'out' && amount < 0);
      final matchesAccount =
          cashMovementAccount.isEmpty ||
          '${movement['accountId']}' == cashMovementAccount;
      final searchable =
          '${movement['description']} ${movement['accountName']} ${movement['date']} ${dateLabel('${movement['date']}')} ${money(amount)}'
              .toLowerCase();
      return matchesType &&
          matchesAccount &&
          (movementQuery.isEmpty || searchable.contains(movementQuery));
    }).toList();
    num dailyProjected = total(accounts, 'balance');
    final dailyForecast = <DataRow>[];
    for (var i = 0; i < 14; i++) {
      final day = DateUtils.dateOnly(DateTime.now().add(Duration(days: i)));
      final key = iso(day);
      final ts = open.where(
        (t) =>
            i == 0 ? (t['due'] as String).compareTo(key) <= 0 : t['due'] == key,
      );
      final incoming = total(
        ts.where((t) => t['kind'] == 'receivable'),
        'remaining',
      );
      final outgoing = total(
        ts.where((t) => t['kind'] == 'payable'),
        'remaining',
      );
      dailyProjected += incoming - outgoing;
      dailyForecast.add(
        DataRow(
          cells: [
            DataCell(Text('${dateLabel(key)}${i == 0 ? ' + vencidos' : ''}')),
            DataCell(Text(money(incoming))),
            DataCell(Text(money(outgoing))),
            DataCell(
              Text(
                money(dailyProjected),
                style: TextStyle(
                  color: dailyProjected < 0 ? Colors.red.shade700 : teal,
                  fontWeight: FontWeight.bold,
                ),
              ),
            ),
          ],
        ),
      );
    }
    final months = List.generate(
      6,
      (i) => DateTime(DateTime.now().year, DateTime.now().month + i),
    );
    num projected = total(accounts, 'balance');
    final forecast = <DataRow>[];
    for (var i = 0; i < months.length; i++) {
      final end = iso(DateTime(months[i].year, months[i].month + 1));
      final start = iso(months[i]);
      final ts = open.where(
        (t) =>
            (i == 0 || (t['due'] as String).compareTo(start) >= 0) &&
            (t['due'] as String).compareTo(end) < 0,
      );
      final incoming = total(
        ts.where((t) => t['kind'] == 'receivable'),
        'remaining',
      );
      final outgoing = total(
        ts.where((t) => t['kind'] == 'payable'),
        'remaining',
      );
      projected += incoming - outgoing;
      forecast.add(
        DataRow(
          cells: [
            DataCell(
              Text(
                '${months[i].month.toString().padLeft(2, '0')}/${months[i].year}${i == 0 ? ' + vencidos' : ''}',
              ),
            ),
            DataCell(Text(money(incoming))),
            DataCell(Text(money(outgoing))),
            DataCell(
              Text(
                money(projected),
                style: TextStyle(
                  color: projected < 0 ? Colors.red.shade700 : teal,
                  fontWeight: FontWeight.bold,
                ),
              ),
            ),
          ],
        ),
      );
    }
    return [
      panel(
        'Como usar esta tela',
        const Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              '1. O saldo de hoje é o dinheiro já registrado nas suas contas.',
            ),
            SizedBox(height: 8),
            Text(
              '2. “Vai entrar” soma as contas a receber que ainda estão abertas.',
            ),
            SizedBox(height: 8),
            Text(
              '3. “Vai sair” soma as contas a pagar que ainda estão abertas.',
            ),
            SizedBox(height: 8),
            Text(
              '4. “Deve sobrar” é uma estimativa: saldo atual + entradas − saídas.',
            ),
          ],
        ),
      ),
      const SizedBox(height: 20),
      panel(
        'Previsão diária dos próximos 14 dias',
        SingleChildScrollView(
          scrollDirection: Axis.horizontal,
          child: DataTable(
            columns: [
              'Dia',
              'Vai entrar',
              'Vai sair',
              'Deve sobrar',
            ].map((s) => DataColumn(label: Text(s))).toList(),
            rows: dailyForecast,
          ),
        ),
      ),
      const SizedBox(height: 20),
      panel(
        'Quanto pode sobrar nos próximos meses',
        Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            const Text(
              'Esta tabela é uma previsão. Ela não paga nem recebe nada automaticamente. O mês atual também considera as contas atrasadas.',
              style: TextStyle(
                fontSize: 13,
                height: 1.5,
                color: Color(0xFF58706A),
              ),
            ),
            const SizedBox(height: 16),
            SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: DataTable(
                columns: [
                  'Período',
                  'Vai entrar',
                  'Vai sair',
                  'Deve sobrar',
                ].map((s) => DataColumn(label: Text(s))).toList(),
                rows: forecast,
              ),
            ),
          ],
        ),
      ),
      const SizedBox(height: 20),
      panel(
        'Dinheiro que já entrou e saiu (${filteredMovements.length})',
        Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Wrap(
              spacing: 12,
              runSpacing: 12,
              children: [
                SizedBox(
                  width: 360,
                  child: TextField(
                    decoration: const InputDecoration(
                      labelText: 'Pesquisar movimentações',
                      hintText: 'Descrição, conta, data ou valor',
                      prefixIcon: Icon(Icons.search),
                    ),
                    onChanged: (value) =>
                        setState(() => cashMovementSearch = value),
                  ),
                ),
                SizedBox(
                  width: 190,
                  child: DropdownButtonFormField<String>(
                    initialValue: cashMovementType,
                    decoration: const InputDecoration(labelText: 'Movimento'),
                    items: const [
                      DropdownMenuItem(value: 'all', child: Text('Todos')),
                      DropdownMenuItem(value: 'in', child: Text('Entradas')),
                      DropdownMenuItem(value: 'out', child: Text('Saídas')),
                    ],
                    onChanged: (value) =>
                        setState(() => cashMovementType = value ?? 'all'),
                  ),
                ),
                SizedBox(
                  width: 250,
                  child: DropdownButtonFormField<String>(
                    key: ValueKey('cash-account-$cashMovementAccount'),
                    initialValue: cashMovementAccount,
                    decoration: const InputDecoration(labelText: 'Conta'),
                    items: [
                      const DropdownMenuItem(
                        value: '',
                        child: Text('Todas as contas'),
                      ),
                      ...accounts.map(
                        (account) => DropdownMenuItem(
                          value: '${account['id']}',
                          child: Text(
                            '${account['name']}',
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                      ),
                    ],
                    onChanged: (value) =>
                        setState(() => cashMovementAccount = value ?? ''),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 14),
            if (filteredMovements.isEmpty)
              empty(
                movements.isEmpty
                    ? 'As baixas e transferências aparecem aqui.'
                    : 'Nenhuma movimentação corresponde aos filtros.',
              )
            else
              ...filteredMovements.map(
                (m) => ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: CircleAvatar(
                    backgroundColor: const Color(0xFFF1F5EF),
                    child: Icon(
                      m['amount'] >= 0 ? Icons.south_west : Icons.north_east,
                      color: m['amount'] >= 0 ? teal : orange,
                      size: 18,
                    ),
                  ),
                  title: Text(
                    m['description'],
                    style: const TextStyle(fontSize: 13),
                  ),
                  subtitle: Text(
                    '${dateLabel(m['date'])} • ${m['accountName']}',
                    style: const TextStyle(fontSize: 11),
                  ),
                  trailing: Text(
                    money(m['amount']),
                    style: TextStyle(
                      fontWeight: FontWeight.w600,
                      color: m['amount'] >= 0 ? teal : const Color(0xFFB35734),
                    ),
                  ),
                ),
              ),
          ],
        ),
        action: TextButton.icon(
          onPressed: transferForm,
          icon: const Icon(Icons.swap_horiz),
          label: const Text('Transferir'),
        ),
      ),
    ];
  }

  Widget _partyList(String type) {
    final list = parties
        .where(
          (p) =>
              (p['type'] == type || p['type'] == 'both') &&
              (p['name'] as String).toLowerCase().contains(
                registrySearch.toLowerCase(),
              ),
        )
        .toList();
    if (list.isEmpty) {
      return empty(
        type == 'customer'
            ? 'Nenhum cliente encontrado.'
            : 'Nenhum fornecedor encontrado.',
      );
    }
    return Column(
      children: list
          .map(
            (p) => ListTile(
              contentPadding: EdgeInsets.zero,
              leading: CircleAvatar(
                backgroundColor: cream,
                child: Icon(
                  type == 'customer'
                      ? Icons.person_outline
                      : Icons.store_outlined,
                  color: teal,
                ),
              ),
              title: Text(p['name']),
              subtitle: Text(
                [
                  p['document'] == ''
                      ? 'Sem documento'
                      : maskedDocument(p['document']),
                  if ((p['phone'] ?? '') != '') p['phone'],
                  if ((p['email'] ?? '') != '') p['email'],
                ].join(' • '),
              ),
              trailing: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  IconButton(
                    tooltip: 'Editar cadastro',
                    icon: const Icon(Icons.edit_outlined),
                    onPressed: () => partyForm(p),
                  ),
                  IconButton(
                    tooltip: 'Excluir cadastro',
                    icon: const Icon(Icons.delete_outline),
                    onPressed: () => deleteRegistry('party', p),
                  ),
                ],
              ),
            ),
          )
          .toList(),
    );
  }

  String planRule(RecordData plan, String prefix, String label) {
    final mode = plan['${prefix}Mode'] ?? 'percentage';
    final value = mode == 'value'
        ? money(plan['${prefix}Value'] ?? 0)
        : '${plan['${prefix}Rate'] ?? 0}%';
    return '$label: $value';
  }

  Widget planList(String kind) {
    final filtered = plans.where((plan) => plan['kind'] == kind).toList();
    if (filtered.isEmpty) {
      return const Padding(
        padding: EdgeInsets.symmetric(vertical: 12),
        child: Text('NENHUM PLANO CADASTRADO NESTE GRUPO.'),
      );
    }
    return Column(
      children: filtered
          .map(
            (p) => ListTile(
              contentPadding: EdgeInsets.zero,
              leading: Icon(
                kind == 'payable'
                    ? Icons.arrow_upward_rounded
                    : Icons.arrow_downward_rounded,
                color: kind == 'payable' ? Colors.deepOrange : Colors.green,
              ),
              title: Text(p['name']),
              subtitle: Text(
                [
                  planRule(p, 'interest', 'JUROS'),
                  planRule(p, 'penalty', 'MULTA'),
                  planRule(
                    p,
                    'discount',
                    kind == 'receivable' ? 'TAXA / DESCONTO' : 'DESCONTO',
                  ),
                ].join('  •  '),
              ),
              trailing: canManagePlanRates
                  ? Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        IconButton(
                          tooltip: 'Editar plano',
                          icon: const Icon(Icons.edit_outlined),
                          onPressed: () => planForm(p),
                        ),
                        IconButton(
                          tooltip: 'Excluir plano',
                          icon: const Icon(Icons.delete_outline),
                          onPressed: () => deleteRegistry('plan', p),
                        ),
                      ],
                    )
                  : const Chip(label: Text('SOMENTE CONSULTA')),
            ),
          )
          .toList(),
    );
  }

  List<Widget> registryPage() => [
    TextField(
      decoration: const InputDecoration(
        hintText: 'Pesquisar cliente ou fornecedor',
        prefixIcon: Icon(Icons.search),
      ),
      onChanged: (value) => setState(() => registrySearch = value),
    ),
    const SizedBox(height: 16),
    registrySection(
      title: 'Clientes',
      subtitle:
          '${parties.where((item) => item['type'] == 'customer').length} CADASTROS',
      icon: Icons.people_alt_outlined,
      onAdd: partyForm,
      addLabel: 'Adicionar cliente / fornecedor',
      child: _partyList('customer'),
    ),
    const SizedBox(height: 12),
    registrySection(
      title: 'Fornecedores',
      subtitle:
          '${parties.where((item) => item['type'] == 'supplier').length} CADASTROS',
      icon: Icons.local_shipping_outlined,
      onAdd: partyForm,
      addLabel: 'Adicionar cliente / fornecedor',
      child: _partyList('supplier'),
    ),
    const SizedBox(height: 12),
    registrySection(
      title: 'Planos de contas — A pagar',
      subtitle:
          '${plans.where((item) => item['kind'] == 'payable').length} PLANOS DE DESPESA',
      icon: Icons.arrow_upward_rounded,
      onAdd: canManagePlanRates ? () => planForm(null, 'payable') : null,
      addLabel: 'Adicionar plano a pagar',
      child: planList('payable'),
    ),
    const SizedBox(height: 12),
    registrySection(
      title: 'Planos de contas — A receber',
      subtitle:
          '${plans.where((item) => item['kind'] == 'receivable').length} PLANOS DE RECEITA',
      icon: Icons.arrow_downward_rounded,
      onAdd: canManagePlanRates ? () => planForm(null, 'receivable') : null,
      addLabel: 'Adicionar plano a receber',
      child: planList('receivable'),
    ),
    const SizedBox(height: 12),
    registrySection(
      title: 'Contas financeiras',
      subtitle: '${accounts.length} CONTAS CADASTRADAS',
      icon: Icons.account_balance_wallet_outlined,
      onAdd: accountForm,
      addLabel: 'Adicionar conta financeira',
      child: Column(
        children: accounts
            .map(
              (a) => ListTile(
                contentPadding: EdgeInsets.zero,
                leading: const Icon(
                  Icons.account_balance_wallet_outlined,
                  color: teal,
                ),
                title: Text(a['name']),
                subtitle: Text(
                  '${a['type'] == 'bank'
                      ? 'BANCO'
                      : a['type'] == 'wallet'
                      ? 'CARTEIRA / OUTROS'
                      : 'CAIXA'} • Saldo inicial: ${money(a['opening'])}',
                ),
                trailing: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      money(a['balance']),
                      style: const TextStyle(
                        fontWeight: FontWeight.w700,
                        color: teal,
                      ),
                    ),
                    IconButton(
                      tooltip: 'Ajustar saldo',
                      icon: const Icon(Icons.tune),
                      onPressed: () => adjustmentForm(a),
                    ),
                    IconButton(
                      tooltip: 'Editar conta',
                      icon: const Icon(Icons.edit_outlined),
                      onPressed: () => accountForm(a),
                    ),
                    IconButton(
                      tooltip: 'Excluir conta',
                      icon: const Icon(Icons.delete_outline),
                      onPressed: () => deleteRegistry('account', a),
                    ),
                  ],
                ),
              ),
            )
            .toList(),
      ),
    ),
    const SizedBox(height: 12),
    registrySection(
      title: 'Formas de pagamento e recebimento',
      subtitle: '${paymentMethods.length} FORMAS CADASTRADAS',
      icon: Icons.payments_outlined,
      onAdd: paymentMethodForm,
      addLabel: 'Adicionar forma de pagamento',
      child: Column(
        children: paymentMethods
            .map(
              (item) => ListTile(
                contentPadding: EdgeInsets.zero,
                leading: Container(
                  width: 40,
                  height: 40,
                  decoration: BoxDecoration(
                    color: teal.withValues(alpha: .09),
                    borderRadius: BorderRadius.circular(10),
                  ),
                  child: const Icon(Icons.payments_outlined, color: teal),
                ),
                title: Text(item['name']),
                subtitle: Text(
                  item['kind'] == 'both'
                      ? 'PAGAMENTOS E RECEBIMENTOS'
                      : item['kind'] == 'payable'
                      ? 'SOMENTE PAGAMENTOS'
                      : 'SOMENTE RECEBIMENTOS',
                ),
                trailing: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    IconButton(
                      tooltip: 'Editar',
                      icon: const Icon(Icons.edit_outlined),
                      onPressed: () => paymentMethodForm(item),
                    ),
                    IconButton(
                      tooltip: 'Excluir',
                      icon: const Icon(Icons.delete_outline),
                      onPressed: () => deleteRegistry('paymentMethod', item),
                    ),
                  ],
                ),
              ),
            )
            .toList(),
      ),
    ),
    if (!financialUser) ...[
      const SizedBox(height: 12),
      registrySection(
        title: 'Centros de custo',
        subtitle: '${costCenters.length} CENTROS CADASTRADOS',
        icon: Icons.schema_outlined,
        onAdd: costCenterForm,
        addLabel: 'Adicionar centro de custo',
        child: Column(
          children: costCenters
              .map(
                (item) => ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: Container(
                    width: 40,
                    height: 40,
                    decoration: BoxDecoration(
                      color: orange.withValues(alpha: .12),
                      borderRadius: BorderRadius.circular(10),
                    ),
                    child: const Icon(Icons.schema_outlined, color: orange),
                  ),
                  title: Text(item['name']),
                  trailing: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      IconButton(
                        tooltip: 'Editar',
                        icon: const Icon(Icons.edit_outlined),
                        onPressed: () => costCenterForm(item),
                      ),
                      IconButton(
                        tooltip: 'Excluir',
                        icon: const Icon(Icons.delete_outline),
                        onPressed: () => deleteRegistry('costCenter', item),
                      ),
                    ],
                  ),
                ),
              )
              .toList(),
        ),
      ),
    ],
  ];

  Future<void> deleteRegistry(String type, RecordData item) async {
    final labels = {
      'party': 'cadastro',
      'plan': 'plano de contas',
      'account': 'conta financeira',
      'paymentMethod': 'forma de pagamento',
      'costCenter': 'centro de custo',
    };
    final paths = {
      'party': 'parties',
      'plan': 'plans',
      'account': 'accounts',
      'paymentMethod': 'payment-methods',
      'costCenter': 'cost-centers',
    };
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text('Excluir ${labels[type]}?'),
        content: Text(
          'Deseja excluir “${item['name']}”? O sistema impedirá a exclusão se este item já estiver sendo usado.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(c, false),
            child: const Text('Voltar'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(c, true),
            child: const Text('Excluir'),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    try {
      await api.request('$base/${paths[type]}/${item['id']}/delete', {});
      await load();
      message('Cadastro excluído.');
    } catch (e) {
      message(e.toString().replaceFirst('Exception: ', ''));
    }
  }

  Future<void> form(
    String title,
    List<FormFieldSpec> fields,
    Future<void> Function(Map<String, String>) save, {
    String? note,
  }) async {
    final saved = await showDialog<bool>(
      context: context,
      barrierDismissible: false,
      builder: (_) =>
          EntryDialog(title: title, fields: fields, save: save, note: note),
    );
    if (saved == true) {
      await load();
      message('Operação salva com sucesso.');
    }
  }

  List<MapEntry<String, String>> options(List<RecordData> list) =>
      list.map((e) => MapEntry('${e['id']}', '${e['name']}')).toList();

  Future<void> quickTitleConversation(String kind) async {
    final receivable = kind == 'receivable';
    final saved = await showDialog<bool>(
      context: context,
      barrierDismissible: false,
      builder: (_) => ExpenseConversationDialog(
        receivable: receivable,
        save: (description, amount, due) async {
          await api.request('$base/titles', {
            'kind': kind,
            'description': description,
            'amount': amount,
            'due': due,
            'partyId': null,
            'category': receivable ? 'RECEITAS DE SERVIÇOS' : 'DESPESAS GERAIS',
            'notes': '',
            'installments': 1,
          });
        },
      ),
    );
    if (saved == true) {
      await load();
      message(
        receivable
            ? 'Recebimento criado com sucesso.'
            : 'Despesa criada com sucesso.',
      );
    }
  }

  void titleForm(String kind) {
    final path = '$base/titles';
    form(
      kind == 'payable' ? 'Nova conta a pagar' : 'Nova conta a receber',
      [
        FormFieldSpec(
          'description',
          kind == 'payable'
              ? 'O que você vai pagar?'
              : 'O que você vai receber?',
          required: true,
          hint: kind == 'payable'
              ? 'Ex.: aluguel da loja'
              : 'Ex.: serviço prestado ao cliente',
        ),
        FormFieldSpec(
          'amount',
          'Valor (R\$)',
          required: true,
          money: true,
          helper: kind == 'payable'
              ? 'Ao dividir, este é o total. Ao repetir, este valor será usado em cada lançamento.'
              : 'Ao dividir, este é o total. Ao repetir, este valor será usado em cada recebimento.',
        ),
        FormFieldSpec(
          'due',
          'Primeiro vencimento',
          date: true,
          required: true,
          initial: iso(DateTime.now()),
        ),
        FormFieldSpec(
          'documentNumber',
          kind == 'payable'
              ? 'Nota fiscal ou documento'
              : 'Documento ou controle',
          hint: kind == 'payable' ? 'Ex.: NF 45872' : 'Ex.: LOTE CARTÃO 2309',
          helper:
              'O sistema evita repetir o mesmo documento para a mesma pessoa.',
        ),
        FormFieldSpec('issueDate', 'Data de emissão (opcional)', date: true),
        FormFieldSpec(
          'partyName',
          kind == 'payable' ? 'Fornecedor (opcional)' : 'Cliente (opcional)',
          hint: kind == 'payable'
              ? 'Digite o nome do fornecedor'
              : 'Digite o nome do cliente',
          helper: 'Se o nome ainda não existir, ele será cadastrado automaticamente ao salvar.',
          suggestions: parties
              .where(
                (p) =>
                    p['type'] == 'both' ||
                    p['type'] == (kind == 'payable' ? 'supplier' : 'customer'),
              )
              .map((p) => p['name'] as String)
              .toList(),
        ),
        FormFieldSpec(
          'category',
          'Plano de contas',
          initial: '',
          required: true,
          hint: 'Digite para buscar ou criar um plano',
          suggestions: plans
              .where((p) => p['kind'] == kind)
              .map((p) => p['name'] as String)
              .toList(),
        ),
        if (hasMatrixOrBranch)
          FormFieldSpec(
            'branch',
            'Matriz ou filial',
            initial: currentCompanyName,
            required: true,
            options: branchOptions(),
            helper: 'Esta opção aparece porque existem CNPJs de matriz e filial com a mesma raiz cadastrados no seu acesso.',
          ),
        financialUser
            ? FormFieldSpec(
                'costCenter',
                'Setor ou centro de custo',
                initial: '',
                options: [
                  const MapEntry('', 'NÃO INFORMADO'),
                  ...costCenters.map(
                    (item) => MapEntry(
                      item['name'] as String,
                      item['name'] as String,
                    ),
                  ),
                ],
              )
            : FormFieldSpec(
                'costCenter',
                'Setor ou centro de custo',
                hint: 'Ex.: AÇOUGUE, PADARIA OU ADMINISTRAÇÃO',
                suggestions: costCenters
                    .map((item) => item['name'] as String)
                    .toList(),
              ),
        FormFieldSpec(
          'paymentMethodId',
          kind == 'payable'
              ? 'Forma de pagamento prevista'
              : 'Forma de recebimento prevista',
          initial: '',
          options: [
            const MapEntry('', 'NÃO INFORMADA'),
            ...options(
              paymentMethods
                  .where((m) => m['kind'] == 'both' || m['kind'] == kind)
                  .toList(),
            ),
          ],
        ),
        FormFieldSpec(
          'repeatMode',
          kind == 'payable'
              ? 'Como deseja lançar esta despesa?'
              : 'Como deseja lançar este recebimento?',
          initial: 'single',
          required: true,
          options: const [
            MapEntry('single', 'CONTA ÚNICA'),
            MapEntry('monthly', 'DIVIDIR O TOTAL EM PARCELAS MENSAIS'),
            MapEntry('monthly_repeat', 'REPETIR O MESMO VALOR TODO MÊS'),
            MapEntry('weekly', 'REPETIR O MESMO VALOR A CADA 7 DIAS'),
            MapEntry('custom', 'REPETIR EM UM INTERVALO DE DIAS'),
          ],
          helper: 'Antes de salvar, você poderá conferir e alterar cada valor e vencimento.',
        ),
        FormFieldSpec(
          'installments',
          kind == 'payable'
              ? 'Quantidade de lançamentos'
              : 'Quantidade de parcelas mensais',
          initial: '1',
          required: true,
          helper: kind == 'payable'
              ? 'Informe quantas vezes a despesa deve aparecer.'
              : 'Use 1 para uma conta única, ou informe quantas parcelas mensais deseja.',
          visibleWhenKey: 'repeatMode',
          visibleWhenValues: const {
            'monthly',
            'monthly_repeat',
            'weekly',
            'custom',
          },
        ),
        FormFieldSpec(
          'intervalDays',
          'Intervalo em dias',
          initial: '15',
          required: true,
          visibleWhenKey: 'repeatMode',
          visibleWhenValues: const {'custom'},
          helper: 'Ex.: 15 para repetir a cada quinze dias.',
        ),
        FormFieldSpec(
          'notes',
          'Alguma observação?',
          hint: 'Ex.: combinar o pagamento com o fornecedor',
        ),
      ],
      (v) async {
        final repeatMode = v['repeatMode'] ?? 'single';
        final installmentCount = repeatMode == 'single'
            ? 1
            : int.tryParse(v['installments']!) ?? 0;
        final enteredAmount = parseMoney(v['amount']!);
        final repeating = repeatsEachTitle(repeatMode);
        final intervalDays = repeatMode == 'weekly'
            ? 7
            : repeatMode == 'custom'
            ? int.tryParse(v['intervalDays'] ?? '') ?? 0
            : 0;
        if (repeatMode == 'custom' &&
            (intervalDays < 1 || intervalDays > 365)) {
          throw Exception('Informe um intervalo entre 1 e 365 dias.');
        }
        final totalAmount = generatedSeriesTotal(
          enteredAmount,
          installmentCount,
          repeatMode,
        );
        final schedule = installmentCount > 1
            ? await installmentSchedule(
                totalAmount,
                installmentCount,
                v['due']!,
                intervalDays: intervalDays,
                repeatedAmount: repeating ? enteredAmount : null,
              )
            : null;
        if (installmentCount > 1 && schedule == null) {
          throw Exception('A configuração das parcelas não foi concluída.');
        }
        int? partyId;
        final partyName = (v['partyName'] ?? '').trim();
        if (partyName.isNotEmpty) {
          final existing = parties
              .where(
                (p) =>
                    (p['name'] as String).toLowerCase() ==
                    partyName.toLowerCase(),
              )
              .toList();
          if (existing.isNotEmpty) {
            partyId = existing.first['id'];
          } else {
            final created = await api.request('$base/parties', {
              'name': partyName,
              'document': '',
              'type': kind == 'payable' ? 'supplier' : 'customer',
            });
            partyId = created['id'];
          }
        }
        final possibleDuplicate = titles.any(
          (item) =>
              item['kind'] == kind &&
              '${item['description']}'
                      .replaceFirst(RegExp(r' \(\d+/\d+\)$'), '')
                      .toUpperCase() ==
                  v['description']!.trim().toUpperCase() &&
              item['amount'] ==
                  (repeating
                      ? enteredAmount
                      : installmentCount > 1
                      ? schedule?.first['amount']
                      : enteredAmount) &&
              item['due'] == v['due'] &&
              item['partyId'] == partyId &&
              item['cancelled'] == 0,
        );
        if (possibleDuplicate && mounted) {
          final continueSave = await showDialog<bool>(
            context: context,
            builder: (c) => AlertDialog(
              title: const Text('Possível lançamento duplicado'),
              content: const Text(
                'Já existe uma conta com a mesma descrição, pessoa, valor e vencimento. Deseja criar outra mesmo assim?',
              ),
              actions: [
                TextButton(
                  onPressed: () => Navigator.pop(c, false),
                  child: const Text('Voltar'),
                ),
                FilledButton(
                  onPressed: () => Navigator.pop(c, true),
                  child: const Text('Criar mesmo assim'),
                ),
              ],
            ),
          );
          if (continueSave != true) {
            throw Exception('Revise o lançamento antes de salvar.');
          }
        }
        await api.request(path, {
          'kind': kind,
          'description': v['description'],
          'amount': totalAmount,
          'due': v['due'],
          'partyId': partyId,
          'category': v['category'],
          'notes': v['notes'],
          'documentNumber': v['documentNumber'],
          'issueDate': v['issueDate'],
          'branch': hasMatrixOrBranch ? v['branch'] : '',
          'costCenter': v['costCenter'],
          'paymentMethodId': int.tryParse(v['paymentMethodId'] ?? ''),
          'installments': installmentCount,
          'installmentAmounts': schedule?.map((e) => e['amount']).toList(),
          'installmentDues': schedule?.map((e) => e['due']).toList(),
          'installmentIntervalDays': intervalDays,
        });
      },
      note: 'Você pode criar uma conta única, dividir o total ou repetir o mesmo valor mensalmente, semanalmente ou em um intervalo escolhido. O saldo só muda quando houver baixa.',
    );
  }

  Future<List<RecordData>?> installmentSchedule(
    int totalAmount,
    int count,
    String firstDue, {
    int intervalDays = 0,
    int? repeatedAmount,
  }) async {
    final amountValues = scheduledAmounts(
      repeatedAmount ?? totalAmount,
      count,
      repeatEach: repeatedAmount != null,
    );
    final dueValues = scheduledDues(
      firstDue,
      count,
      intervalDays: intervalDays,
    );
    final amounts = amountValues
        .map((value) => TextEditingController(text: inputMoney(value)))
        .toList();
    final dues = dueValues
        .map((value) => TextEditingController(text: dateLabel(value)))
        .toList();
    String? errorText;
    final result = await showDialog<List<RecordData>>(
      context: context,
      barrierDismissible: false,
      builder: (dialogContext) => StatefulBuilder(
        builder: (context, setDialogState) => AlertDialog(
          title: const Text('Configure as parcelas'),
          content: SizedBox(
            width: 620,
            height: 430,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(
                  repeatedAmount != null
                      ? '$count lançamentos de ${money(repeatedAmount)} ${intervalDays == 0
                            ? 'todo mês'
                            : intervalDays == 7
                            ? 'a cada 7 dias'
                            : 'a cada $intervalDays dias'}. Total: ${money(totalAmount)}. Você pode ajustar valores e vencimentos.'
                      : 'Valor total: ${money(totalAmount)}. Ajuste valores e vencimentos; a soma deve fechar o total.',
                ),
                if (errorText != null)
                  Padding(
                    padding: const EdgeInsets.only(top: 10),
                    child: Text(
                      errorText!,
                      style: TextStyle(color: Colors.red.shade700),
                    ),
                  ),
                const SizedBox(height: 12),
                Expanded(
                  child: ListView.separated(
                    itemCount: count,
                    separatorBuilder: (_, _) => const SizedBox(height: 10),
                    itemBuilder: (_, i) => Row(
                      children: [
                        SizedBox(
                          width: 72,
                          child: Text(
                            '${i + 1}/$count',
                            style: const TextStyle(fontWeight: FontWeight.w600),
                          ),
                        ),
                        Expanded(
                          child: TextField(
                            controller: amounts[i],
                            decoration: const InputDecoration(
                              labelText: 'Valor (R\$)',
                            ),
                          ),
                        ),
                        const SizedBox(width: 12),
                        Expanded(
                          child: TextField(
                            controller: dues[i],
                            decoration: const InputDecoration(
                              labelText: 'Vencimento',
                              hintText: 'dd/mm/aaaa',
                            ),
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ],
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(dialogContext),
              child: const Text('Voltar'),
            ),
            FilledButton(
              onPressed: () {
                try {
                  final parsedAmounts = amounts
                      .map((c) => parseMoney(c.text))
                      .toList();
                  final parsedDues = dues
                      .map((c) => parseFriendlyDate(c.text))
                      .toList();
                  if (parsedAmounts.any((v) => v <= 0) ||
                      parsedAmounts.fold<int>(0, (a, b) => a + b) !=
                          totalAmount) {
                    throw const FormatException(
                      'A soma dos valores precisa ser igual ao valor total.',
                    );
                  }
                  if (parsedDues.any((d) => d == null)) {
                    throw const FormatException(
                      'Confira os vencimentos das parcelas.',
                    );
                  }
                  Navigator.pop(
                    dialogContext,
                    List.generate(
                      count,
                      (i) => {
                        'amount': parsedAmounts[i],
                        'due': iso(parsedDues[i]!),
                      },
                    ),
                  );
                } catch (e) {
                  setDialogState(
                    () => errorText = e is FormatException
                        ? e.message
                        : 'Confira os valores e vencimentos.',
                  );
                }
              },
              child: const Text('Confirmar parcelas'),
            ),
          ],
        ),
      ),
    );
    for (final c in [...amounts, ...dues]) {
      c.dispose();
    }
    return result;
  }

  Future<void> openFinanceSandbox() async {
    RecordData? snapshot;
    var busy = false;
    String? error;
    await showDialog<void>(
      context: context,
      builder: (c) => StatefulBuilder(
        builder: (c, update) {
          Future<void> consult({bool connect = false}) async {
            update(() {
              busy = true;
              error = null;
            });
            try {
              if (connect) {
                await api.request('$base/open-finance/sandbox/connect', {});
              }
              final value = RecordData.from(
                await api.request('$base/open-finance/sandbox'),
              );
              if (c.mounted) update(() => snapshot = value);
            } catch (e) {
              if (c.mounted) {
                update(
                  () => error = e.toString().replaceFirst('Exception: ', ''),
                );
              }
            } finally {
              if (c.mounted) update(() => busy = false);
            }
          }

          final bankAccounts = snapshot == null
              ? <RecordData>[]
              : rows(snapshot!['accounts']);
          return AlertDialog(
            title: const Text('OPEN FINANCE — SANDBOX'),
            content: SizedBox(
              width: 800,
              height: MediaQuery.sizeOf(c).height * .6,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  const Text(
                    'BANCO FICTÍCIO PLUGGY BANK. ESTE TESTE NÃO CONECTA O ITAÚ E NÃO ALTERA SEUS LANÇAMENTOS OU SALDOS.',
                  ),
                  const SizedBox(height: 12),
                  if (busy) const LinearProgressIndicator(),
                  if (error != null)
                    Text(error!, style: const TextStyle(color: Colors.red)),
                  if (snapshot != null && snapshot!['ready'] != true)
                    Text(
                      snapshot!['connected'] == true
                          ? 'PROCESSANDO. AGUARDE ALGUNS SEGUNDOS E CLIQUE EM CONSULTAR.'
                          : 'CLIQUE EM CONECTAR BANCO FICTÍCIO PARA COMEÇAR.',
                    ),
                  Expanded(
                    child: ListView(
                      children: [
                        for (final a in bankAccounts) ...[
                          Padding(
                            padding: const EdgeInsets.symmetric(vertical: 14),
                            child: Text(
                              '${a['name']} • SALDO FICTÍCIO: ${money(a['balance'])} ${a['currency']}',
                              style: const TextStyle(
                                fontWeight: FontWeight.bold,
                              ),
                            ),
                          ),
                          for (final t in rows(a['transactions']))
                            ListTile(
                              contentPadding: EdgeInsets.zero,
                              title: Text('${t['description']}'),
                              subtitle: Text(
                                '${dateLabel('${t['date']}')} • ${t['status'] == 'POSTED' ? 'REALIZADO' : 'PENDENTE'}',
                              ),
                              trailing: Text(money(t['amount'])),
                            ),
                        ],
                      ],
                    ),
                  ),
                ],
              ),
            ),
            actions: [
              TextButton(
                onPressed: busy ? null : () => Navigator.pop(c),
                child: const Text('FECHAR'),
              ),
              TextButton(
                onPressed: busy ? null : () => consult(),
                child: const Text('CONSULTAR'),
              ),
              if (snapshot?['connected'] != true)
                FilledButton(
                  onPressed: busy ? null : () => consult(connect: true),
                  child: const Text('CONECTAR BANCO FICTÍCIO'),
                ),
            ],
          );
        },
      ),
    );
  }

  Future<void> importOfxPayables() async {
    if (accounts.isEmpty) {
      message('Cadastre uma conta financeira primeiro.');
      return;
    }
    final file = await openFile(
      acceptedTypeGroups: const [
        XTypeGroup(label: 'Extrato OFX', extensions: ['ofx']),
      ],
    );
    if (file == null || !mounted) return;
    var accountId = (accounts.first['id'] as num).toInt();
    final chosen = await showDialog<int>(
      context: context,
      builder: (c) => AlertDialog(
        title: const Text('CONTA DO EXTRATO'),
        content: DropdownButtonFormField<int>(
          initialValue: accountId,
          isExpanded: true,
          items: accounts
              .map(
                (a) => DropdownMenuItem<int>(
                  value: (a['id'] as num).toInt(),
                  child: Text('${a['name']}'),
                ),
              )
              .toList(),
          onChanged: (v) {
            if (v != null) accountId = v;
          },
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(c),
            child: const Text('CANCELAR'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(c, accountId),
            child: const Text('LER EXTRATO'),
          ),
        ],
      ),
    );
    if (chosen == null || !mounted) return;
    try {
      final preview = RecordData.from(
        await api.request('$base/ofx/preview', {
          'accountId': chosen,
          'fileName': file.name,
          'dataBase64': base64Encode(await file.readAsBytes()),
        }),
      );
      if (!mounted) return;
      final transactions = rows(preview['transactions']);
      final matches = <int, int>{};
      final selected = <int>{};
      final categories = <int, String>{};
      var updateBalance = false;
      for (final t in transactions) {
        final id = (t['id'] as num).toInt();
        if (t['duplicate'] == true || t['future'] == true) continue;
        if (t['existingSettlementId'] is num) {
          matches[id] = -(t['existingSettlementId'] as num).toInt();
        } else if (t['suggestedTitleId'] is num) {
          matches[id] = (t['suggestedTitleId'] as num).toInt();
        }
        if (matches.containsKey(id)) selected.add(id);
      }
      final confirmed = await showDialog<bool>(
        context: context,
        builder: (c) => StatefulBuilder(
          builder: (c, setLocal) => AlertDialog(
            title: const Text('CONFERIR EXTRATO OFX'),
            content: SizedBox(
              width: 950,
              height: MediaQuery.sizeOf(c).height * .65,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Text(
                    '${preview['accountName']} • ${preview['total']} MOVIMENTAÇÕES • ${preview['duplicates']} CONCILIADAS',
                  ),
                  const Text(
                    'Confira entradas e saídas. Para o que falta, escolha CRIAR NOVO. Valores iguais são sugestões e precisam ser conferidos.',
                  ),
                  if (preview['bankBalance'] != null &&
                      '${preview['balanceDate']}'.isNotEmpty)
                    CheckboxListTile(
                      contentPadding: EdgeInsets.zero,
                      value: updateBalance,
                      onChanged: (v) =>
                          setLocal(() => updateBalance = v == true),
                      title: Text(
                        'CONCILIAR SALDO: ${money(preview['bankBalance'])} EM ${dateLabel(preview['balanceDate'])}',
                      ),
                      subtitle: Text(
                        'SALDO NO SISTEMA: ${money(preview['currentBalance'])}. A diferença será registrada como ajuste identificado na data do banco.',
                      ),
                    ),
                  TextButton(
                    onPressed: () => setLocal(() {
                      for (final t in transactions) {
                        if (t['duplicate'] == true || t['future'] == true) {
                          continue;
                        }
                        final id = (t['id'] as num).toInt();
                        matches.putIfAbsent(id, () => 0);
                        selected.add(id);
                      }
                    }),
                    child: const Text('SELECIONAR TODAS / CRIAR AS QUE FALTAM'),
                  ),
                  Expanded(
                    child: ListView.builder(
                      itemCount: transactions.length,
                      itemBuilder: (_, index) {
                        final t = transactions[index];
                        final id = (t['id'] as num).toInt();
                        final blocked =
                            t['duplicate'] == true || t['future'] == true;
                        final candidates = titles
                            .where(
                              (v) =>
                                  v['kind'] == t['kind'] &&
                                  v['cancelled'] == 0 &&
                                  (v['remaining'] as num) > 0,
                            )
                            .toList();
                        final planOptions = plans
                            .where((p) => p['kind'] == t['kind'])
                            .toList();
                        return Card(
                          child: Padding(
                            padding: const EdgeInsets.all(12),
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.stretch,
                              children: [
                                CheckboxListTile(
                                  contentPadding: EdgeInsets.zero,
                                  value: selected.contains(id),
                                  onChanged: blocked
                                      ? null
                                      : (v) => setLocal(() {
                                          if (v == true) {
                                            matches.putIfAbsent(id, () => 0);
                                            selected.add(id);
                                          } else {
                                            selected.remove(id);
                                          }
                                        }),
                                  title: Text(
                                    '${t['kind'] == 'payable' ? 'SAÍDA' : 'ENTRADA'} • ${money(t['amount'])} • ${dateLabel(t['date'])}',
                                  ),
                                  subtitle: Text(
                                    '${t['memo']}${t['future'] == true
                                        ? '\nDATA FUTURA: AGUARDAR REALIZAÇÃO'
                                        : t['duplicate'] == true
                                        ? '\nJÁ CONCILIADA'
                                        : ''}',
                                  ),
                                ),
                                if (!blocked)
                                  DropdownButtonFormField<int>(
                                    key: ValueKey('ofx-$id-${matches[id]}'),
                                    initialValue: matches[id],
                                    isExpanded: true,
                                    decoration: const InputDecoration(
                                      labelText:
                                          'O QUE FAZER COM ESTA MOVIMENTAÇÃO?',
                                    ),
                                    items: [
                                      const DropdownMenuItem<int>(
                                        value: 0,
                                        child: Text(
                                          'CRIAR NOVO LANÇAMENTO E BAIXAR',
                                        ),
                                      ),
                                      if (t['existingSettlementId'] is num)
                                        DropdownMenuItem<int>(
                                          value:
                                              -(t['existingSettlementId']
                                                      as num)
                                                  .toInt(),
                                          child: Text(
                                            'VINCULAR BAIXA EXISTENTE: ${t['existingSettlementDescription']}',
                                            overflow: TextOverflow.ellipsis,
                                          ),
                                        ),
                                      ...candidates.map(
                                        (v) => DropdownMenuItem<int>(
                                          value: (v['id'] as num).toInt(),
                                          child: Text(
                                            '${v['description']} • ${money(v['remaining'])}',
                                            overflow: TextOverflow.ellipsis,
                                          ),
                                        ),
                                      ),
                                    ],
                                    onChanged: (v) => setLocal(() {
                                      if (v != null) {
                                        matches[id] = v;
                                        selected.add(id);
                                      }
                                    }),
                                  ),
                                if (!blocked &&
                                    matches[id] == 0 &&
                                    planOptions.isNotEmpty)
                                  Padding(
                                    padding: const EdgeInsets.only(top: 8),
                                    child: DropdownButtonFormField<String>(
                                      isExpanded: true,
                                      initialValue: categories[id],
                                      decoration: const InputDecoration(
                                        labelText: 'PLANO DE CONTAS (OPCIONAL)',
                                      ),
                                      items: planOptions
                                          .map(
                                            (p) => DropdownMenuItem<String>(
                                              value: '${p['name']}',
                                              child: Text('${p['name']}'),
                                            ),
                                          )
                                          .toList(),
                                      onChanged: (v) {
                                        if (v != null) categories[id] = v;
                                      },
                                    ),
                                  ),
                                if (!blocked && (matches[id] ?? 0) < 0)
                                  const Text(
                                    'A BAIXA JÁ EXISTE. VINCULAR NÃO MOVIMENTARÁ O SALDO NOVAMENTE.',
                                  ),
                              ],
                            ),
                          ),
                        );
                      },
                    ),
                  ),
                ],
              ),
            ),
            actions: [
              TextButton(
                onPressed: () => Navigator.pop(c, false),
                child: const Text('CANCELAR'),
              ),
              FilledButton(
                onPressed: selected.isEmpty && !updateBalance
                    ? null
                    : () => Navigator.pop(c, true),
                child: Text('CONFIRMAR ${selected.length} MOVIMENTAÇÕES'),
              ),
            ],
          ),
        ),
      );
      if (confirmed != true) return;
      final result = await api.request('$base/ofx/confirm', {
        'importId': preview['importId'],
        'updateBalance': updateBalance,
        'items': selected
            .map(
              (id) => {
                'transactionId': id,
                'titleId': (matches[id] ?? 0) > 0 ? matches[id] : 0,
                'create': matches[id] == 0,
                'category': categories[id] ?? '',
                'existingSettlementId': (matches[id] ?? 0) < 0
                    ? -matches[id]!
                    : null,
              },
            )
            .toList(),
      });
      await load();
      message(
        '${result['count']} MOVIMENTAÇÕES CONCILIADAS. AJUSTE DE SALDO: ${money(result['balanceAdjustment'] ?? 0)}.',
      );
    } catch (e) {
      message(e.toString().replaceFirst('Exception: ', ''));
    }
  }

  void settleForm(RecordData t) {
    final path = '$base/titles/${t['id']}/settle';
    int suggested(String prefix) {
      if ((t['${prefix}Mode'] ?? 'percentage') == 'value') {
        return (t['${prefix}Value'] ?? 0) as int;
      }
      return ((t['remaining'] as num) *
              ((t['${prefix}Rate'] ?? 0) as num) /
              100)
          .round();
    }

    final receivable = t['kind'] == 'receivable';
    form(
      t['kind'] == 'payable' ? 'Registrar pagamento' : 'Registrar recebimento',
      [
        FormFieldSpec(
          'accountId',
          'Conta financeira',
          required: true,
          options: options(accounts),
          initial: '${accounts.first['id']}',
        ),
        FormFieldSpec(
          'principal',
          'Valor da conta que será baixado (R\$)',
          initial: inputMoney(t['remaining']),
          required: true,
          money: true,
          helper: 'Para pagar ou receber só uma parte, informe um valor menor.',
        ),
        FormFieldSpec(
          'date',
          'Data da baixa',
          date: true,
          required: true,
          pastOnly: true,
          initial: iso(DateTime.now()),
        ),
        FormFieldSpec(
          'paymentMethodId',
          t['kind'] == 'payable'
              ? 'Forma de pagamento'
              : 'Forma de recebimento',
          initial: '${t['paymentMethodId'] ?? ''}',
          options: [
            const MapEntry('', 'Não informada'),
            ...options(
              paymentMethods
                  .where((m) => m['kind'] == 'both' || m['kind'] == t['kind'])
                  .toList(),
            ),
          ],
        ),
        FormFieldSpec(
          'interest',
          'Juros — acréscimo (R\$)',
          initial: inputMoney(suggested('interest')),
          money: true,
          advanced: true,
        ),
        FormFieldSpec(
          'penalty',
          'Multa — acréscimo (R\$)',
          initial: inputMoney(suggested('penalty')),
          money: true,
          advanced: true,
        ),
        FormFieldSpec(
          'discount',
          receivable
              ? 'Taxa da administradora ou desconto — reduz o recebimento (R\$)'
              : 'Desconto — reduz o pagamento (R\$)',
          initial: inputMoney(suggested('discount')),
          money: true,
          advanced: true,
        ),
        FormFieldSpec(
          'additional',
          'Outros acréscimos (R\$)',
          initial: '0,00',
          money: true,
          advanced: true,
        ),
      ],
      (v) async {
        await api.request(path, {
          'accountId': int.parse(v['accountId']!),
          'principal': parseMoney(v['principal']!),
          'interest': parseMoney(v['interest']!),
          'penalty': parseMoney(v['penalty']!),
          'discount': parseMoney(v['discount']!),
          'additional': parseMoney(v['additional']!),
          'paymentMethodId': int.tryParse(v['paymentMethodId'] ?? ''),
          'date': v['date'],
        });
      },
      note:
          '${t['description']} • Em aberto: ${money(t['remaining'])}\n${receivable ? 'Valor que entra na conta' : 'Valor que sai da conta'} = principal + juros + multa + outros acréscimos − ${receivable ? 'taxa/desconto' : 'desconto'}. Para baixa parcial, reduza o principal.',
    );
  }

  void bulkSettleForm() {
    final chosen = titles
        .where(
          (t) =>
              selectedTitleIds.contains(t['id']) &&
              t['cancelled'] == 0 &&
              t['remaining'] > 0,
        )
        .toList();
    if (chosen.length < 2) {
      message('Selecione pelo menos dois lançamentos em aberto.');
      return;
    }
    final kind = chosen.first['kind'];
    if (chosen.any((t) => t['kind'] != kind)) {
      message('Selecione somente contas do mesmo tipo.');
      return;
    }
    final totalValue = total(chosen, 'remaining');
    form(
      kind == 'payable'
          ? 'Pagar títulos selecionados'
          : 'Receber títulos selecionados',
      [
        FormFieldSpec(
          'accountId',
          'Conta financeira',
          required: true,
          options: options(accounts),
          initial: '${accounts.first['id']}',
        ),
        FormFieldSpec(
          'date',
          'Data da baixa',
          date: true,
          required: true,
          pastOnly: true,
          initial: iso(DateTime.now()),
        ),
        FormFieldSpec(
          'paymentMethodId',
          kind == 'payable' ? 'Forma de pagamento' : 'Forma de recebimento',
          initial: '',
          options: [
            const MapEntry('', 'Não informada'),
            ...options(
              paymentMethods
                  .where((m) => m['kind'] == 'both' || m['kind'] == kind)
                  .toList(),
            ),
          ],
        ),
      ],
      (v) async {
        await api.request('$base/settlements/bulk', {
          'accountId': int.parse(v['accountId']!),
          'date': v['date'],
          'paymentMethodId': int.tryParse(v['paymentMethodId'] ?? ''),
          'items': chosen
              .map((t) => {'titleId': t['id'], 'principal': t['remaining']})
              .toList(),
        });
      },
      note:
          '${chosen.length} lançamentos serão baixados integralmente. Total conferido: ${money(totalValue)}. A operação será gravada individualmente em cada título.',
    );
  }

  void partyForm([RecordData? party]) {
    final path = party == null
        ? '$base/parties'
        : '$base/parties/${party['id']}/edit';
    form(
      party == null
          ? 'Adicionar cliente ou fornecedor'
          : 'Editar cliente ou fornecedor',
      [
        FormFieldSpec(
          'document',
          'CPF / CNPJ',
          initial: party?['document'] ?? '',
          document: 'document',
          hint: 'Digite o documento; a formatação é automática',
          helper: 'Se for CNPJ, deixe o nome vazio para buscar a empresa automaticamente.',
        ),
        FormFieldSpec(
          'name',
          'Nome da pessoa ou empresa',
          initial: party?['name'] ?? '',
          hint: 'Ex.: Ana Silva ou Papelaria Central',
        ),
        FormFieldSpec(
          'personType',
          'Pessoa física ou jurídica',
          initial: party?['personType'] ?? '',
          options: const [
            MapEntry('', 'IDENTIFICAR PELO DOCUMENTO'),
            MapEntry('individual', 'PESSOA FÍSICA'),
            MapEntry('company', 'PESSOA JURÍDICA'),
          ],
        ),
        FormFieldSpec(
          'type',
          'Tipo',
          initial: party?['type'] ?? 'both',
          options: const [
            MapEntry('both', 'Cliente e fornecedor'),
            MapEntry('customer', 'Cliente'),
            MapEntry('supplier', 'Fornecedor'),
          ],
        ),
        FormFieldSpec(
          'phone',
          'Telefone (opcional)',
          initial: party?['phone'] ?? '',
          hint: 'Ex.: (11) 99999-9999',
        ),
        FormFieldSpec(
          'email',
          'E-mail (opcional)',
          initial: party?['email'] ?? '',
          hint: 'Ex.: contato@empresa.com.br',
        ),
      ],
      (v) async {
        var name = (v['name'] ?? '').trim();
        final document = (v['document'] ?? '').replaceAll(
          RegExp('[^A-Za-z0-9]'),
          '',
        );
        if (name.isEmpty && document.length == 14) {
          final lookup = await api.request('/cnpj/$document');
          name = (lookup['tradeName'] as String).trim().isNotEmpty
              ? lookup['tradeName']
              : lookup['name'];
        }
        if (name.isEmpty) {
          throw Exception('Informe o nome ou um CNPJ válido para buscar.');
        }
        await api.request(path, {...v, 'name': name});
      },
      note: 'Telefone e e-mail são opcionais. A consulta de CNPJ usa dados públicos e precisa de internet.',
    );
  }

  void planForm([RecordData? plan, String defaultKind = 'payable']) {
    if (!canManagePlanRates) {
      message(
        'O ADMINISTRADOR DA EMPRESA BLOQUEOU A ALTERAÇÃO DE JUROS, MULTA E DESCONTO.',
      );
      return;
    }
    form(
      plan == null ? 'Novo plano de contas' : 'Editar plano de contas',
      [
        FormFieldSpec(
          'name',
          'Nome do plano',
          initial: plan?['name'] ?? '',
          required: true,
          hint: 'Ex.: Energia elétrica',
        ),
        FormFieldSpec(
          'kind',
          'Usar este plano em',
          initial: plan?['kind'] ?? defaultKind,
          required: true,
          options: const [
            MapEntry('payable', 'CONTAS A PAGAR'),
            MapEntry('receivable', 'CONTAS A RECEBER'),
          ],
        ),
        FormFieldSpec(
          'interestMode',
          'Como calcular os juros?',
          initial: plan?['interestMode'] ?? 'percentage',
          options: const [
            MapEntry('percentage', 'PORCENTAGEM (%)'),
            MapEntry('value', 'VALOR FIXO (R\$)'),
          ],
        ),
        FormFieldSpec(
          'interestRate',
          'Juros padrão (%)',
          initial: '${plan?['interestRate'] ?? 0}',
          visibleWhenKey: 'interestMode',
          visibleWhenValues: const {'percentage'},
        ),
        FormFieldSpec(
          'interestValue',
          'Juros padrão (R\$)',
          initial: inputMoney(plan?['interestValue'] ?? 0),
          money: true,
          visibleWhenKey: 'interestMode',
          visibleWhenValues: const {'value'},
        ),
        FormFieldSpec(
          'penaltyMode',
          'Como calcular a multa?',
          initial: plan?['penaltyMode'] ?? 'percentage',
          options: const [
            MapEntry('percentage', 'PORCENTAGEM (%)'),
            MapEntry('value', 'VALOR FIXO (R\$)'),
          ],
        ),
        FormFieldSpec(
          'penaltyRate',
          'Multa padrão (%)',
          initial: '${plan?['penaltyRate'] ?? 0}',
          visibleWhenKey: 'penaltyMode',
          visibleWhenValues: const {'percentage'},
        ),
        FormFieldSpec(
          'penaltyValue',
          'Multa padrão (R\$)',
          initial: inputMoney(plan?['penaltyValue'] ?? 0),
          money: true,
          visibleWhenKey: 'penaltyMode',
          visibleWhenValues: const {'value'},
        ),
        FormFieldSpec(
          'discountMode',
          'Como calcular a taxa ou desconto?',
          initial: plan?['discountMode'] ?? 'percentage',
          options: const [
            MapEntry('percentage', 'PORCENTAGEM (%)'),
            MapEntry('value', 'VALOR FIXO (R\$)'),
          ],
        ),
        FormFieldSpec(
          'discountRate',
          'Taxa ou desconto padrão (%)',
          initial: '${plan?['discountRate'] ?? 0}',
          visibleWhenKey: 'discountMode',
          visibleWhenValues: const {'percentage'},
        ),
        FormFieldSpec(
          'discountValue',
          'Taxa ou desconto padrão (R\$)',
          initial: inputMoney(plan?['discountValue'] ?? 0),
          money: true,
          visibleWhenKey: 'discountMode',
          visibleWhenValues: const {'value'},
        ),
      ],
      (v) async {
        double rate(String key) =>
            double.tryParse((v[key] ?? '0').replaceAll(',', '.')) ?? 0;
        await api.request(
          plan == null ? '$base/plans' : '$base/plans/${plan['id']}/edit',
          {
            'name': v['name'],
            'kind': v['kind'],
            'interestRate': rate('interestRate'),
            'interestMode': v['interestMode'],
            'interestValue': parseMoney(v['interestValue'] ?? '0'),
            'penaltyRate': rate('penaltyRate'),
            'penaltyMode': v['penaltyMode'],
            'penaltyValue': parseMoney(v['penaltyValue'] ?? '0'),
            'discountRate': rate('discountRate'),
            'discountMode': v['discountMode'],
            'discountValue': parseMoney(v['discountValue'] ?? '0'),
          },
        );
      },
      note: 'Você pode usar porcentagem ou valor fixo. Em contas a receber, cadastre a taxa da administradora do cartão em TAXA OU DESCONTO: ela será subtraída do valor que entra na conta. Juros e multa continuam sendo acréscimos.',
    );
  }

  void accountForm([RecordData? account]) {
    final path = account == null
        ? '$base/accounts'
        : '$base/accounts/${account['id']}/edit';
    form(
      account == null ? 'Nova conta financeira' : 'Editar conta financeira',
      [
        FormFieldSpec(
          'name',
          'Como você quer chamar esta conta?',
          initial: account?['name'] ?? '',
          required: true,
          hint: 'Ex.: Caixa da loja ou Banco principal',
        ),
        FormFieldSpec(
          'type',
          'Tipo da conta',
          initial: account?['type'] ?? 'cash',
          options: const [
            MapEntry('cash', 'CAIXA'),
            MapEntry('bank', 'BANCO'),
            MapEntry('wallet', 'CARTEIRA / OUTROS'),
          ],
        ),
        FormFieldSpec(
          'opening',
          'Saldo inicial (R\$)',
          initial: inputMoney(account?['opening'] ?? 0),
          money: true,
          helper: 'Quanto já existe nesta conta antes dos lançamentos? Se estiver vazia, deixe zero.',
        ),
      ],
      (v) async {
        await api.request(path, {
          'name': v['name'],
          'opening': parseMoney(v['opening']!),
          'type': v['type'],
        });
      },
    );
  }

  void paymentMethodForm([RecordData? item]) {
    form(
      item == null
          ? 'Nova forma de pagamento ou recebimento'
          : 'Editar forma de pagamento ou recebimento',
      [
        FormFieldSpec(
          'name',
          'Nome',
          initial: item?['name'] ?? '',
          required: true,
          hint: 'Ex.: PIX, BOLETO OU CARTÃO',
        ),
        FormFieldSpec(
          'kind',
          'Disponível em',
          initial: item?['kind'] ?? 'both',
          options: const [
            MapEntry('both', 'PAGAMENTOS E RECEBIMENTOS'),
            MapEntry('payable', 'SOMENTE PAGAMENTOS'),
            MapEntry('receivable', 'SOMENTE RECEBIMENTOS'),
          ],
        ),
      ],
      (v) => api.request(
        item == null
            ? '$base/payment-methods'
            : '$base/payment-methods/${item['id']}/edit',
        v,
      ),
    );
  }

  void costCenterForm([RecordData? item]) {
    if (financialUser) {
      message('O USUÁRIO FINANCEIRO NÃO PODE ADMINISTRAR CENTROS DE CUSTO.');
      return;
    }
    form(
      item == null ? 'Novo centro de custo' : 'Editar centro de custo',
      [
        FormFieldSpec(
          'name',
          'Nome do setor ou atividade',
          initial: item?['name'] ?? '',
          required: true,
          hint: 'Ex.: PADARIA OU ADMINISTRAÇÃO',
        ),
      ],
      (v) => api.request(
        item == null
            ? '$base/cost-centers'
            : '$base/cost-centers/${item['id']}/edit',
        v,
      ),
    );
  }

  void adjustmentForm(RecordData account) {
    form(
      'Ajustar saldo de ${account['name']}',
      [
        FormFieldSpec(
          'amount',
          'Valor do ajuste (R\$)',
          required: true,
          money: true,
          helper:
              'Use valor positivo para acrescentar e negativo para retirar.',
        ),
        FormFieldSpec(
          'date',
          'Data',
          required: true,
          date: true,
          pastOnly: true,
          initial: iso(DateTime.now()),
        ),
        FormFieldSpec(
          'reason',
          'Justificativa',
          required: true,
          hint: 'Ex.: correção após conferência de caixa',
        ),
      ],
      (v) => api.request('$base/accounts/${account['id']}/adjust', {
        'amount': parseMoney(v['amount']!),
        'date': v['date'],
        'reason': v['reason'],
      }),
      note: 'O ajuste movimenta somente esta conta e ficará identificado no histórico.',
    );
  }

  void companyForm() {
    form(
      'Adicionar empresa / CNPJ',
      [
        FormFieldSpec(
          'cnpj',
          'CNPJ da empresa',
          required: true,
          document: 'cnpj',
          hint: '29.775.093/0001-98',
          helper: 'Ao salvar, consultaremos os dados públicos da empresa pela internet.',
        ),
        FormFieldSpec(
          'name',
          'Nome da empresa (opcional)',
          hint: 'Será preenchido pela consulta do CNPJ',
          helper: 'Se preferir outro nome de exibição, informe aqui.',
        ),
      ],
      (v) async {
        var name = (v['name'] ?? '').trim();
        if (name.isEmpty) {
          final cleanCnpj = v['cnpj']!.replaceAll(RegExp('[^A-Za-z0-9]'), '');
          final lookup = await api.request('/cnpj/$cleanCnpj');
          name = (lookup['tradeName'] as String).trim().isNotEmpty
              ? lookup['tradeName']
              : lookup['name'];
        }
        final result = await api.request('/companies', {
          'name': name,
          'cnpj': v['cnpj'],
        });
        company = result['id'];
        companyUsers = [];
      },
      note: 'A consulta utiliza dados públicos da BrasilAPI. Se a internet estiver indisponível, informe o nome manualmente para continuar.',
    );
  }

  void transferForm() {
    if (accounts.length < 2) {
      message('Cadastre ao menos duas contas financeiras.');
      return;
    }
    final path = '$base/transfers';
    form(
      'Transferência entre contas',
      [
        FormFieldSpec(
          'fromId',
          'Conta de origem',
          options: options(accounts),
          initial: '${accounts[0]['id']}',
        ),
        FormFieldSpec(
          'toId',
          'Conta de destino',
          options: options(accounts),
          initial: '${accounts[1]['id']}',
        ),
        FormFieldSpec('amount', 'Valor (R\$)', required: true, money: true),
        FormFieldSpec(
          'date',
          'Data da transferência',
          date: true,
          required: true,
          pastOnly: true,
          initial: iso(DateTime.now()),
        ),
      ],
      (v) async {
        await api.request(path, {
          'fromId': int.parse(v['fromId']!),
          'toId': int.parse(v['toId']!),
          'amount': parseMoney(v['amount']!),
          'date': v['date'],
        });
      },
      note: 'A transferência movimenta as duas contas sem gerar receita ou despesa.',
    );
  }

  Future<void> backup() async {
    if (financialUser) {
      message('O USUÁRIO FINANCEIRO NÃO PODE CRIAR BACKUP.');
      return;
    }
    try {
      final result = await api.request(
        '/api/backup'.replaceFirst('/api', ''),
        {},
      );
      if (mounted) {
        await showDialog(
          context: context,
          builder: (c) => AlertDialog(
            title: const Text('Backup criado'),
            content: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Text(
                  'O backup é uma cópia completa dos dados financeiros. Ele permite recuperar lançamentos, cadastros, baixas e histórico se o arquivo principal for danificado ou a máquina apresentar problema.',
                ),
                const SizedBox(height: 14),
                const Text(
                  'Arquivo criado em:',
                  style: TextStyle(fontWeight: FontWeight.w600),
                ),
                const SizedBox(height: 6),
                SelectableText(result['path']),
              ],
            ),
            actions: [
              TextButton(
                onPressed: () => Navigator.pop(c),
                child: const Text('Fechar'),
              ),
            ],
          ),
        );
      }
    } catch (e) {
      message(e.toString());
    }
  }

  void showBackupTools() {
    if (financialUser) {
      message('O USUÁRIO FINANCEIRO NÃO TEM ACESSO AO BACKUP.');
      return;
    }
    showDialog<void>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Row(
          children: [
            Icon(Icons.shield_outlined),
            SizedBox(width: 10),
            Text('Ferramentas de backup'),
          ],
        ),
        content: const Text(
          'Escolha a operação. A restauração fica disponível somente para o administrador geral.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext),
            child: const Text('Fechar'),
          ),
          FilledButton.icon(
            onPressed: () {
              Navigator.pop(dialogContext);
              backup();
            },
            icon: const Icon(Icons.save_outlined),
            label: const Text('Criar backup'),
          ),
          if (globalAdmin)
            FilledButton.icon(
              onPressed: () {
                Navigator.pop(dialogContext);
                restoreBackup();
              },
              icon: const Icon(Icons.restore_outlined),
              label: const Text('Restaurar backup'),
            ),
          if (globalAdmin)
            FilledButton.icon(
              onPressed: () {
                Navigator.pop(dialogContext);
                recoverGestoresBackup();
              },
              icon: const Icon(Icons.sync_alt_outlined),
              label: const Text('Recuperar backup do Gestores'),
            ),
        ],
      ),
    );
  }

  Future<void> recoverGestoresBackup() async {
    final file = await openFile(
      acceptedTypeGroups: const [
        XTypeGroup(label: 'Backup do Gestores', extensions: ['zip', 'backup']),
      ],
    );
    if (file == null || !mounted) return;
    final passwordController = TextEditingController();
    if (file.name.toLowerCase().endsWith('.zip')) {
      final proceed = await showDialog<bool>(
        context: context,
        builder: (c) => AlertDialog(
          title: const Text('Abrir backup do Gestores'),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Text(
                'Se este ZIP foi criado nesta máquina, deixe a senha em branco. Se veio de outra máquina, informe a senha usada para proteger o backup.',
              ),
              const SizedBox(height: 16),
              TextField(
                controller: passwordController,
                obscureText: true,
                decoration: const InputDecoration(
                  labelText: 'Senha do backup (opcional)',
                  prefixIcon: Icon(Icons.lock_outline),
                ),
              ),
            ],
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(c, false),
              child: const Text('Voltar'),
            ),
            FilledButton(
              onPressed: () => Navigator.pop(c, true),
              child: const Text('Conferir backup'),
            ),
          ],
        ),
      );
      if (proceed != true) {
        passwordController.dispose();
        return;
      }
    }
    var progressOpen = false;
    void showProgress(String text) {
      progressOpen = true;
      showDialog<void>(
        context: context,
        barrierDismissible: false,
        builder: (_) => AlertDialog(
          content: Row(
            children: [
              const SizedBox(
                width: 28,
                height: 28,
                child: CircularProgressIndicator(strokeWidth: 3),
              ),
              const SizedBox(width: 18),
              Expanded(child: Text(text)),
            ],
          ),
        ),
      );
    }

    void closeProgress() {
      if (progressOpen && mounted) {
        Navigator.of(context, rootNavigator: true).pop();
        progressOpen = false;
      }
    }

    try {
      final bytes = await file.readAsBytes();
      showProgress(
        'Lendo e conferindo o backup do Gestores. Isso pode levar alguns minutos.',
      );
      final preview = await api.request('/import/gestores/prepare', {
        'name': file.name,
        'dataBase64': base64Encode(bytes),
        'password': passwordController.text,
      });
      closeProgress();
      if (!mounted) return;
      final confirmed = await showDialog<bool>(
        context: context,
        builder: (c) => AlertDialog(
          title: const Text('Dados encontrados no Gestores'),
          content: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  '${preview['company']}',
                  style: const TextStyle(fontWeight: FontWeight.w700),
                ),
                const SizedBox(height: 14),
                Text('${preview['parties']} clientes e fornecedores'),
                Text('${preview['plans']} planos de contas'),
                Text('${preview['accounts']} contas financeiras'),
                Text(
                  '${preview['payables']} contas a pagar • ${money(preview['payablesTotal'])}',
                ),
                Text(
                  '${preview['receivables']} contas a receber • ${money(preview['receivablesTotal'])}',
                ),
                Text(
                  '${preview['settlements']} baixas • ${money(preview['settlementsTotal'])}',
                ),
                const SizedBox(height: 16),
                const Text(
                  'Ao continuar, esses dados substituirão o financeiro atual. Antes disso, o Monalisa criará automaticamente uma cópia de segurança.',
                ),
              ],
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(c, false),
              child: const Text('Cancelar'),
            ),
            FilledButton.icon(
              onPressed: () => Navigator.pop(c, true),
              icon: const Icon(Icons.restore_outlined),
              label: const Text('Recuperar estes dados'),
            ),
          ],
        ),
      );
      if (confirmed != true) return;
      showProgress('Criando a cópia de segurança e recuperando os dados.');
      final result = await api.request('/import/gestores/commit', {
        'token': preview['token'],
      });
      closeProgress();
      await load();
      message(
        'Dados do Gestores recuperados. Cópia anterior salva em ${result['safetyBackup']}.',
      );
    } catch (e) {
      closeProgress();
      message(e.toString().replaceFirst('Exception: ', ''));
    } finally {
      passwordController.dispose();
    }
  }

  Future<void> restoreBackup() async {
    final file = await openFile(
      acceptedTypeGroups: const [
        XTypeGroup(label: 'Backup SQLite', extensions: ['db']),
      ],
    );
    if (file == null || !mounted) return;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: const Text('Restaurar este backup?'),
        content: Text(
          'O arquivo “${file.name}” substituirá os dados atuais. Antes disso, o sistema criará automaticamente uma cópia de segurança do banco atual.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(c, false),
            child: const Text('Voltar'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(c, true),
            child: const Text('Restaurar'),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    try {
      final bytes = await file.readAsBytes();
      final result = await api.request('/restore', {
        'name': file.name,
        'dataBase64': base64Encode(bytes),
      });
      await load();
      message(
        'Backup restaurado. Cópia anterior salva em ${result['safetyBackup']}.',
      );
    } catch (e) {
      message(e.toString().replaceFirst('Exception: ', ''));
    }
  }

  Future<void> attachFile({int? titleId, int? settlementId}) async {
    final file = await openFile();
    if (file == null) return;
    try {
      final bytes = await file.readAsBytes();
      if (bytes.length > 10 * 1024 * 1024) {
        throw Exception('O anexo deve ter até 10 MB.');
      }
      await api.request('$base/attachments', {
        'titleId': titleId,
        'settlementId': settlementId,
        'name': file.name,
        'mimeType': file.mimeType ?? 'application/octet-stream',
        'dataBase64': base64Encode(bytes),
      });
      await load();
      message('Anexo salvo com sucesso.');
    } catch (e) {
      message(e.toString().replaceFirst('Exception: ', ''));
    }
  }

  Future<void> saveAttachment(RecordData attachment) async {
    try {
      final response = await api.client.get(
        Uri.parse(
          '$serviceOrigin/api/companies/$company/attachments/${attachment['id']}',
        ),
        headers: {
          if (api.token != null) 'Authorization': 'Bearer ${api.token}',
        },
      );
      if (response.statusCode != 200) {
        throw Exception('Não foi possível abrir o anexo.');
      }
      if (kIsWeb) {
        await saveBrowserDownload(
          response.bodyBytes,
          attachment['name'],
          attachment['mimeType'],
        );
        message('DOWNLOAD DO ANEXO INICIADO.');
      } else {
        final location = await getSaveLocation(
          suggestedName: attachment['name'],
        );
        if (location == null) return;
        await XFile.fromData(
          response.bodyBytes,
          mimeType: attachment['mimeType'],
          name: attachment['name'],
        ).saveTo(location.path);
        message('Anexo salvo em ${location.path}.');
      }
    } catch (e) {
      message(e.toString().replaceFirst('Exception: ', ''));
    }
  }

  Future<void> details(RecordData t) async {
    final history = settlements.where((s) => s['titleId'] == t['id']).toList();
    final settlementIds = history.map((s) => s['id']).toSet();
    final titleAttachments = attachments
        .where(
          (a) =>
              a['titleId'] == t['id'] ||
              settlementIds.contains(a['settlementId']),
        )
        .toList();
    final action = await showDialog<String>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(t['description']),
        content: SizedBox(
          width: 560,
          child: SingleChildScrollView(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(
                  'Valor original: ${money(t['amount'])}\nEm aberto: ${money(t['remaining'])}\nPlano de contas: ${t['category']}${(t['documentNumber'] ?? '').toString().isEmpty ? '' : '\nDocumento: ${t['documentNumber']}'}${(t['issueDate'] ?? '').toString().isEmpty ? '' : '\nEmissão: ${dateLabel(t['issueDate'])}'}${(t['branch'] ?? '').toString().isEmpty ? '' : '\nFilial: ${t['branch']}'}${(t['costCenter'] ?? '').toString().isEmpty ? '' : '\nSetor: ${t['costCenter']}'}${(t['notes'] as String).trim().isEmpty ? '' : '\nObservações: ${t['notes']}'}',
                ),
                const SizedBox(height: 16),
                Row(
                  children: [
                    OutlinedButton.icon(
                      onPressed: () => Navigator.pop(c, 'attach/title'),
                      icon: const Icon(Icons.attach_file),
                      label: const Text('Anexar documento'),
                    ),
                  ],
                ),
                if (titleAttachments.isNotEmpty) ...[
                  const SizedBox(height: 10),
                  ...titleAttachments.map(
                    (a) => ListTile(
                      dense: true,
                      contentPadding: EdgeInsets.zero,
                      leading: const Icon(Icons.description_outlined),
                      title: Text(a['name']),
                      subtitle: Text(
                        '${((a['size'] as num) / 1024).toStringAsFixed(1)} KB',
                      ),
                      trailing: IconButton(
                        icon: const Icon(Icons.download_outlined),
                        onPressed: () => saveAttachment(a),
                      ),
                    ),
                  ),
                ],
                const SizedBox(height: 20),
                const Text(
                  'Histórico de baixas',
                  style: TextStyle(fontWeight: FontWeight.bold),
                ),
                if (history.isEmpty)
                  const Padding(
                    padding: EdgeInsets.all(15),
                    child: Text('Nenhuma baixa registrada.'),
                  ),
                ...history.map(
                  (s) => ListTile(
                    contentPadding: EdgeInsets.zero,
                    title: Text(
                      '${dateLabel(s['date'])} • ${money(s['principal'])}',
                    ),
                    subtitle: Text(
                      '${s['accountName']} • caixa: ${money(s['principal'] + s['interest'] + s['penalty'] - s['discount'])}${s['reversed'] == 1 ? '\nEstornado: ${s['reason']}' : ''}',
                    ),
                    trailing: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        IconButton(
                          tooltip: 'Anexar comprovante',
                          icon: const Icon(Icons.attach_file),
                          onPressed: () =>
                              Navigator.pop(c, 'attach/settlement/${s['id']}'),
                        ),
                        if (s['reversed'] != 1)
                          TextButton(
                            onPressed: () =>
                                Navigator.pop(c, 'reverse/${s['id']}'),
                            child: const Text('Estornar'),
                          ),
                      ],
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
        actions: [
          if (t['cancelled'] == 0 && t['settled'] == 0)
            TextButton(
              onPressed: () => Navigator.pop(c, 'edit'),
              child: const Text('Editar'),
            ),
          if (t['settled'] == 0)
            TextButton(
              onPressed: () => Navigator.pop(c, 'delete'),
              style: TextButton.styleFrom(foregroundColor: Colors.red.shade700),
              child: const Text('Excluir'),
            ),
          if (t['cancelled'] == 0 && t['settled'] == 0)
            TextButton(
              onPressed: () => Navigator.pop(c, 'cancel'),
              child: const Text('Cancelar título'),
            ),
          TextButton(
            onPressed: () => Navigator.pop(c, 'duplicate'),
            child: const Text('Duplicar'),
          ),
          TextButton(
            onPressed: () => Navigator.pop(c),
            child: const Text('Fechar'),
          ),
        ],
      ),
    );
    if (action == null || !mounted) return;
    if (action == 'attach/title') {
      await attachFile(titleId: t['id']);
      return;
    }
    if (action.startsWith('attach/settlement/')) {
      await attachFile(settlementId: int.parse(action.split('/').last));
      return;
    }
    if (action == 'duplicate') {
      form(
        'Duplicar lançamento',
        [
          FormFieldSpec(
            'due',
            'Vencimento da cópia',
            required: true,
            date: true,
            initial: t['due'],
          ),
        ],
        (v) async =>
            api.request('$base/titles/${t['id']}/duplicate', {'due': v['due']}),
        note: 'Será criada uma nova conta sem baixas e sem número de documento, preservando os demais dados.',
      );
      return;
    }
    if (action == 'edit') {
      editTitleForm(t);
      return;
    }
    if (action == 'delete') {
      deleteTitleForm(t);
      return;
    }
    final path = action == 'cancel'
        ? '$base/titles/${t['id']}/cancel'
        : '$base/settlements/${action.split('/')[1]}/reverse';
    final linked = t['installmentGroup'] == null
        ? <RecordData>[]
        : titles
              .where(
                (item) =>
                    item['installmentGroup'] == t['installmentGroup'] &&
                    item['cancelled'] == 0,
              )
              .toList();
    final cancellable = linked.where((item) => item['settled'] == 0).length;
    form(
      action == 'cancel' ? 'Cancelar lançamento' : 'Estornar baixa',
      [
        if (action == 'cancel' && t['installmentGroup'] != null)
          FormFieldSpec(
            'scope',
            'Quais parcelas deseja cancelar?',
            initial: 'single',
            required: true,
            options: [
              const MapEntry('single', 'Somente esta parcela'),
              MapEntry(
                'all_open',
                'Todas as parcelas sem baixa ($cancellable)',
              ),
            ],
            helper: 'Parcelas pagas, recebidas ou parcialmente baixadas serão mantidas.',
          ),
        FormFieldSpec(
          'reason',
          'Por que deseja fazer isso?',
          required: true,
          hint: 'Ex.: lançamento duplicado ou compra cancelada',
        ),
      ],
      (v) async {
        await api.request(path, v);
      },
      note: action == 'cancel' && t['installmentGroup'] != null
          ? 'Esta é a parcela ${t['installmentNumber']} de ${t['installmentCount']}. Você pode cancelar só ela ou todas as $cancellable parcelas sem baixa deste mesmo lançamento. ${linked.length - cancellable} parcela(s) com baixa serão preservadas. O cancelamento ficará no histórico.'
          : 'A operação ficará registrada no histórico, junto com o motivo informado.',
    );
  }

  void editTitleForm(RecordData t) {
    final linked = t['installmentGroup'] == null
        ? <RecordData>[]
        : titles
              .where(
                (item) => item['installmentGroup'] == t['installmentGroup'],
              )
              .toList();
    final editable = linked
        .where((item) => item['cancelled'] == 0 && item['settled'] == 0)
        .length;
    final description = (t['description'] as String).replaceFirst(
      RegExp(r' \(\d+/\d+\)$'),
      '',
    );
    form(
      'Editar lançamento',
      [
        FormFieldSpec(
          'description',
          'Descrição',
          initial: description,
          required: true,
        ),
        FormFieldSpec(
          'amount',
          'Valor desta parcela (R\$)',
          initial: inputMoney(t['amount']),
          required: true,
          money: true,
        ),
        FormFieldSpec(
          'due',
          'Vencimento',
          initial: t['due'],
          required: true,
          date: true,
        ),
        FormFieldSpec(
          'documentNumber',
          'Nota fiscal ou documento',
          initial: t['documentNumber'] ?? '',
        ),
        FormFieldSpec(
          'issueDate',
          'Data de emissão (opcional)',
          initial: t['issueDate'] ?? '',
          date: true,
        ),
        FormFieldSpec(
          'partyId',
          t['kind'] == 'payable'
              ? 'Fornecedor (opcional)'
              : 'Cliente (opcional)',
          initial: '${t['partyId'] ?? ''}',
          options: [
            const MapEntry('', 'Sem vínculo'),
            ...options(
              parties
                  .where(
                    (p) =>
                        p['type'] == 'both' ||
                        p['type'] ==
                            (t['kind'] == 'payable' ? 'supplier' : 'customer'),
                  )
                  .toList(),
            ),
          ],
        ),
        FormFieldSpec(
          'category',
          'Plano de contas',
          initial: t['category'] ?? '',
          suggestions: plans
              .where((plan) => plan['kind'] == t['kind'])
              .map((plan) => plan['name'] as String)
              .toList(),
        ),
        if (hasMatrixOrBranch)
          FormFieldSpec(
            'branch',
            'Matriz ou filial',
            initial: (t['branch'] ?? '').toString().isEmpty
                ? currentCompanyName
                : '${t['branch']}',
            required: true,
            options: branchOptions('${t['branch'] ?? ''}'),
          ),
        financialUser
            ? FormFieldSpec(
                'costCenter',
                'Setor ou centro de custo',
                initial: t['costCenter'] ?? '',
                options: [
                  const MapEntry('', 'NÃO INFORMADO'),
                  ...costCenters.map(
                    (item) => MapEntry(
                      item['name'] as String,
                      item['name'] as String,
                    ),
                  ),
                ],
              )
            : FormFieldSpec(
                'costCenter',
                'Setor ou centro de custo',
                initial: t['costCenter'] ?? '',
                suggestions: costCenters
                    .map((item) => item['name'] as String)
                    .toList(),
              ),
        FormFieldSpec(
          'paymentMethodId',
          t['kind'] == 'payable'
              ? 'Forma de pagamento prevista'
              : 'Forma de recebimento prevista',
          initial: '${t['paymentMethodId'] ?? ''}',
          options: [
            const MapEntry('', 'NÃO INFORMADA'),
            ...options(
              paymentMethods
                  .where((m) => m['kind'] == 'both' || m['kind'] == t['kind'])
                  .toList(),
            ),
          ],
        ),
        FormFieldSpec('notes', 'Observações', initial: t['notes'] ?? ''),
        if (t['installmentGroup'] != null)
          FormFieldSpec(
            'scope',
            'Quais parcelas deseja editar?',
            initial: 'single',
            required: true,
            options: [
              const MapEntry('single', 'Somente esta parcela'),
              MapEntry('all_open', 'Todas as parcelas sem baixa ($editable)'),
            ],
            helper: 'Ao editar todas, o valor informado será aplicado a cada parcela e os vencimentos mensais serão mantidos.',
          ),
      ],
      (v) async {
        await api.request('$base/titles/${t['id']}/edit', {
          'description': v['description'],
          'amount': parseMoney(v['amount']!),
          'due': v['due'],
          'partyId': int.tryParse(v['partyId'] ?? ''),
          'category': v['category'],
          'notes': v['notes'],
          'documentNumber': v['documentNumber'],
          'issueDate': v['issueDate'],
          'branch': hasMatrixOrBranch ? v['branch'] : t['branch'],
          'costCenter': v['costCenter'],
          'paymentMethodId': int.tryParse(v['paymentMethodId'] ?? ''),
          'scope': v['scope'] ?? 'single',
        });
      },
      note: t['installmentGroup'] == null
          ? 'A alteração ficará registrada no histórico.'
          : 'Esta é a parcela ${t['installmentNumber']} de ${t['installmentCount']}. Parcelas com baixa ou canceladas serão preservadas.',
    );
  }

  void deleteTitleForm(RecordData t) {
    final linked = t['installmentGroup'] == null
        ? <RecordData>[]
        : titles
              .where(
                (item) =>
                    item['installmentGroup'] == t['installmentGroup'] &&
                    item['settled'] == 0,
              )
              .toList();
    form(
      'Excluir lançamento',
      [
        if (t['installmentGroup'] != null)
          FormFieldSpec(
            'scope',
            'Quais parcelas deseja excluir?',
            initial: 'single',
            required: true,
            options: [
              const MapEntry('single', 'Somente esta parcela'),
              MapEntry(
                'all_open',
                'Todas as parcelas sem histórico de baixa (${linked.length})',
              ),
            ],
          ),
        FormFieldSpec(
          'reason',
          'Motivo da exclusão',
          required: true,
          hint: 'Ex.: lançamento criado por engano',
        ),
      ],
      (v) async {
        await api.request('$base/titles/${t['id']}/delete', {
          'reason': v['reason'],
          'scope': v['scope'] ?? 'single',
        });
      },
      note: t['cancelled'] == 1
          ? 'Este lançamento já foi cancelado. Ao excluir, ele sairá das listas, mas o cancelamento e a exclusão continuarão registrados no histórico.'
          : 'O lançamento sairá das listas e a exclusão ficará registrada no histórico. Se houve baixa, ela precisa estar estornada antes da exclusão.',
    );
  }

  String creditCardInvoiceStatus(RecordData invoice) {
    if ('${invoice['closedAt'] ?? ''}'.isEmpty) return 'Aberta';
    if ((invoice['amount'] as num) == 0) return 'Sem compras';
    if (invoice['cancelled'] == 1) return 'Cancelada';
    if ((invoice['remaining'] as num) == 0) return 'Paga';
    return 'Fechada';
  }

  Widget creditMetric(String label, String value, IconData icon) => SizedBox(
    width: 245,
    child: Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.all(18),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(icon, size: 20, color: teal),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    label,
                    style: TextStyle(color: mutedText, fontSize: 12),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 12),
            Text(
              value,
              style: const TextStyle(
                color: teal,
                fontSize: 23,
                fontWeight: FontWeight.w800,
              ),
            ),
          ],
        ),
      ),
    ),
  );

  List<Widget> creditCardsPage() {
    final openInvoices = creditCardInvoices.where(
      (invoice) =>
          invoice['cancelled'] == 0 && (invoice['remaining'] as num) > 0,
    );
    final openTotal = total(openInvoices, 'remaining');
    final currentReference = iso(DateTime.now()).substring(0, 7);
    final currentTotal = total(
      openInvoices.where(
        (invoice) => invoice['referenceMonth'] == currentReference,
      ),
      'remaining',
    );
    return [
      Wrap(
        spacing: 14,
        runSpacing: 14,
        children: [
          creditMetric(
            'Faturas em aberto',
            money(openTotal),
            Icons.receipt_long_outlined,
          ),
          creditMetric(
            'Fatura deste mês',
            money(currentTotal),
            Icons.calendar_month_outlined,
          ),
          creditMetric(
            'Cartões cadastrados',
            '${creditCards.length}',
            Icons.credit_card_outlined,
          ),
        ],
      ),
      const SizedBox(height: 18),
      if (creditCards.isEmpty)
        panel(
          'Seus cartões',
          const Padding(
            padding: EdgeInsets.symmetric(vertical: 35),
            child: Column(
              children: [
                Icon(Icons.add_card_outlined, size: 42, color: teal),
                SizedBox(height: 12),
                Text(
                  'Cadastre o primeiro cartão para lançar compras e formar as faturas automaticamente.',
                  textAlign: TextAlign.center,
                ),
              ],
            ),
          ),
          action: FilledButton.icon(
            onPressed: creditCardForm,
            icon: const Icon(Icons.add),
            label: const Text('Cadastrar cartão'),
          ),
        )
      else
        ...creditCards.map((card) {
          final lastFour = '${card['lastFour'] ?? ''}'.trim();
          final invoices =
              creditCardInvoices
                  .where((invoice) => invoice['cardId'] == card['id'])
                  .toList()
                ..sort((a, b) {
                  final aOpen = '${a['closedAt'] ?? ''}'.isEmpty;
                  final bOpen = '${b['closedAt'] ?? ''}'.isEmpty;
                  if (aOpen != bOpen) return aOpen ? -1 : 1;
                  return aOpen
                      ? '${a['dueDate']}'.compareTo('${b['dueDate']}')
                      : '${b['dueDate']}'.compareTo('${a['dueDate']}');
                });
          final recurringCharges = creditCardRecurringCharges
              .where((charge) => charge['cardId'] == card['id'])
              .toList();
          return Padding(
            padding: const EdgeInsets.only(bottom: 16),
            child: panel(
              '${card['name']}${lastFour.isEmpty ? '' : ' • FINAL $lastFour'}',
              Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Wrap(
                    spacing: 22,
                    runSpacing: 8,
                    children: [
                      Text('FECHA DIA ${card['closingDay']}'),
                      Text('VENCE DIA ${card['dueDay']}'),
                      if ((card['accountName'] ?? '').toString().isNotEmpty)
                        Text('PAGAMENTO: ${card['accountName']}'),
                    ],
                  ),
                  const SizedBox(height: 14),
                  if (recurringCharges.isNotEmpty) ...[
                    Container(
                      padding: const EdgeInsets.all(14),
                      decoration: BoxDecoration(
                        color: darkMode
                            ? const Color(0xFF1B2423)
                            : const Color(0xFFF1F6F5),
                        borderRadius: BorderRadius.circular(14),
                        border: Border.all(color: teal.withValues(alpha: .16)),
                      ),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          const Text(
                            'COBRANÇAS RECORRENTES',
                            style: TextStyle(fontWeight: FontWeight.w800),
                          ),
                          const SizedBox(height: 8),
                          ...recurringCharges.map(
                            (charge) => ListTile(
                              contentPadding: EdgeInsets.zero,
                              dense: true,
                              leading: Icon(
                                charge['active'] == 1
                                    ? Icons.autorenew
                                    : Icons.stop_circle_outlined,
                                color: charge['active'] == 1 ? teal : mutedText,
                              ),
                              title: Text('${charge['description']}'),
                              subtitle: Text(
                                '${money(charge['amount'])} TODO MÊS • DESDE ${dateLabel('${charge['startDate']}')}',
                              ),
                              trailing: charge['active'] == 1
                                  ? TextButton(
                                      onPressed: () =>
                                          stopCreditCardRecurrence(charge),
                                      child: const Text('Encerrar'),
                                    )
                                  : const Text(
                                      'ENCERRADA',
                                      style: TextStyle(
                                        fontSize: 11,
                                        fontWeight: FontWeight.w700,
                                      ),
                                    ),
                            ),
                          ),
                        ],
                      ),
                    ),
                    const SizedBox(height: 14),
                  ],
                  if (invoices.isEmpty)
                    Text(
                      'Nenhuma compra lançada neste cartão.',
                      style: TextStyle(color: mutedText),
                    )
                  else
                    ...invoices.map((invoice) {
                      final purchases = creditCardPurchases
                          .where(
                            (purchase) =>
                                purchase['invoiceId'] == invoice['id'],
                          )
                          .toList();
                      final title = titles
                          .where((item) => item['id'] == invoice['titleId'])
                          .firstOrNull;
                      final invoiceStatus = creditCardInvoiceStatus(invoice);
                      return Card(
                        color: darkMode
                            ? const Color(0xFF252C2B)
                            : const Color(0xFFF8FAF9),
                        elevation: 0,
                        margin: const EdgeInsets.only(bottom: 10),
                        child: ExpansionTile(
                          leading: const Icon(
                            Icons.receipt_long_outlined,
                            color: teal,
                          ),
                          title: Text(
                            'FATURA ${invoice['referenceMonth'].toString().substring(5)}/${invoice['referenceMonth'].toString().substring(0, 4)}',
                            style: const TextStyle(fontWeight: FontWeight.w700),
                          ),
                          subtitle: Text(
                            'FECHAMENTO ${dateLabel('${invoice['closingDate']}')} • VENCIMENTO ${dateLabel('${invoice['dueDate']}')} • ${purchases.length} ${purchases.length == 1 ? 'ITEM' : 'ITENS'}',
                          ),
                          trailing: SizedBox(
                            width: 235,
                            child: Row(
                              mainAxisAlignment: MainAxisAlignment.end,
                              children: [
                                Column(
                                  mainAxisAlignment: MainAxisAlignment.center,
                                  crossAxisAlignment: CrossAxisAlignment.end,
                                  children: [
                                    Text(
                                      money(invoice['amount']),
                                      style: const TextStyle(
                                        fontWeight: FontWeight.w800,
                                      ),
                                    ),
                                    Text(
                                      invoiceStatus,
                                      style: TextStyle(
                                        color: invoiceStatus == 'Paga'
                                            ? const Color(0xFF23734D)
                                            : invoiceStatus == 'Fechada'
                                            ? const Color(0xFFC06A16)
                                            : teal,
                                        fontSize: 11,
                                        fontWeight: FontWeight.w700,
                                      ),
                                    ),
                                  ],
                                ),
                                if (title != null &&
                                    title['cancelled'] == 0 &&
                                    title['remaining'] > 0) ...[
                                  const SizedBox(width: 10),
                                  TextButton(
                                    onPressed: () => settleForm(title),
                                    child: const Text('Pagar'),
                                  ),
                                ],
                              ],
                            ),
                          ),
                          children: purchases
                              .map(
                                (purchase) => ListTile(
                                  leading: const Icon(
                                    Icons.shopping_bag_outlined,
                                    size: 20,
                                  ),
                                  title: Text('${purchase['description']}'),
                                  subtitle: Text(
                                    '${dateLabel('${purchase['purchaseDate']}')} • ${purchase['category']}${purchase['recurringChargeId'] != null
                                        ? ' • RECORRENTE'
                                        : purchase['installmentCount'] > 1
                                        ? ' • PARCELA ${purchase['installmentNumber']}/${purchase['installmentCount']}'
                                        : ''}',
                                  ),
                                  trailing: Row(
                                    mainAxisSize: MainAxisSize.min,
                                    children: [
                                      Text(
                                        money(purchase['amount']),
                                        style: const TextStyle(
                                          fontWeight: FontWeight.w700,
                                        ),
                                      ),
                                      if ('${invoice['closedAt'] ?? ''}'
                                          .isEmpty)
                                        IconButton(
                                          tooltip: 'Excluir esta compra e suas parcelas',
                                          onPressed: () =>
                                              deleteCreditCardPurchase(
                                                purchase,
                                              ),
                                          icon: const Icon(
                                            Icons.delete_outline,
                                            size: 20,
                                          ),
                                        ),
                                    ],
                                  ),
                                ),
                              )
                              .toList(),
                        ),
                      );
                    }),
                ],
              ),
              action: Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  FilledButton.icon(
                    onPressed: () => creditCardForm(card),
                    icon: const Icon(Icons.edit_outlined, size: 18),
                    label: const Text('Editar cartão'),
                  ),
                  FilledButton.icon(
                    onPressed: () => creditCardPurchaseForm(card),
                    icon: const Icon(Icons.add_shopping_cart, size: 18),
                    label: const Text('Lançar compra'),
                  ),
                ],
              ),
            ),
          );
        }),
    ];
  }

  void creditCardForm([RecordData? card]) {
    form(
      card == null ? 'Cadastrar cartão de crédito' : 'Editar cartão de crédito',
      [
        FormFieldSpec(
          'name',
          'Nome do cartão',
          initial: '${card?['name'] ?? ''}',
          required: true,
          hint: 'Ex.: ITAÚ EMPRESARIAL',
        ),
        FormFieldSpec(
          'lastFour',
          'Final do cartão (opcional)',
          initial: '${card?['lastFour'] ?? ''}',
          hint: 'Ex.: 1234',
          helper: 'Use somente os quatro últimos dígitos.',
        ),
        FormFieldSpec(
          'closingDay',
          'Dia de fechamento',
          initial: '${card?['closingDay'] ?? ''}',
          required: true,
          hint: 'Ex.: 10',
        ),
        FormFieldSpec(
          'dueDay',
          'Dia de vencimento',
          initial: '${card?['dueDay'] ?? ''}',
          required: true,
          hint: 'Ex.: 17',
        ),
        FormFieldSpec(
          'accountId',
          'Conta usada para pagar (opcional)',
          initial: '${card?['accountId'] ?? ''}',
          options: [
            const MapEntry('', 'DEFINIR NO MOMENTO DA BAIXA'),
            ...options(accounts),
          ],
        ),
        FormFieldSpec(
          'notes',
          'Observações',
          initial: '${card?['notes'] ?? ''}',
        ),
      ],
      (values) async {
        final path = card == null
            ? '$base/credit-cards'
            : '$base/credit-cards/${card['id']}/edit';
        await api.request(path, {
          'name': values['name'],
          'lastFour': values['lastFour'],
          'closingDay': int.tryParse(values['closingDay'] ?? ''),
          'dueDay': int.tryParse(values['dueDay'] ?? ''),
          'accountId': int.tryParse(values['accountId'] ?? ''),
          'notes': values['notes'],
        });
      },
      note: card == null
          ? 'O fechamento define em qual fatura cada compra entra. A fatura aparecerá automaticamente no Contas a Pagar.'
          : 'As compras e faturas existentes continuarão vinculadas a este cartão.',
    );
  }

  void creditCardPurchaseForm([RecordData? selectedCard]) {
    final payablePlans = plans
        .where((plan) => plan['kind'] == 'payable')
        .toList();
    form(
      'Lançar compra no cartão',
      [
        FormFieldSpec(
          'cardId',
          'Cartão',
          initial: '${selectedCard?['id'] ?? creditCards.first['id']}',
          required: true,
          options: options(creditCards),
        ),
        FormFieldSpec('description', 'O que foi comprado?', required: true),
        FormFieldSpec(
          'amount',
          'Valor total (R\$)',
          required: true,
          money: true,
        ),
        FormFieldSpec(
          'purchaseDate',
          'Data da compra',
          initial: iso(DateTime.now()),
          required: true,
          date: true,
        ),
        FormFieldSpec(
          'mode',
          'Como deseja lançar?',
          initial: 'single',
          required: true,
          options: const [
            MapEntry('single', 'COMPRA ÚNICA'),
            MapEntry('installments', 'COMPRA PARCELADA'),
            MapEntry('recurring', 'COBRANÇA RECORRENTE TODO MÊS'),
          ],
        ),
        FormFieldSpec(
          'installments',
          'Quantidade de parcelas (somente para compra parcelada)',
          initial: '1',
        ),
        FormFieldSpec(
          'category',
          'Plano de contas',
          initial:
              payablePlans.any((plan) => plan['name'] == 'CARTÃO DE CRÉDITO')
              ? 'CARTÃO DE CRÉDITO'
              : '${payablePlans.firstOrNull?['name'] ?? ''}',
          required: true,
          options: payablePlans
              .map((plan) => MapEntry('${plan['name']}', '${plan['name']}'))
              .toList(),
        ),
        FormFieldSpec('notes', 'Observações'),
      ],
      (values) async {
        await api.request(
          '$base/credit-cards/${int.parse(values['cardId']!)}/purchases',
          {
            'description': values['description'],
            'amount': parseMoney(values['amount']!),
            'purchaseDate': values['purchaseDate'],
            'mode': values['mode'],
            'installments': int.tryParse(values['installments'] ?? ''),
            'category': values['category'],
            'notes': values['notes'],
          },
        );
      },
      note: 'Na compra parcelada, o valor total é dividido. Na cobrança recorrente, o mesmo valor entra todo mês até você encerrar.',
    );
  }

  Future<void> stopCreditCardRecurrence(RecordData recurrence) async {
    final reasonController = TextEditingController();
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Encerrar cobrança recorrente'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              '${recurrence['description']} deixará de entrar nas próximas faturas. As faturas já fechadas ou pagas serão preservadas.',
            ),
            const SizedBox(height: 16),
            TextField(
              controller: reasonController,
              textCapitalization: TextCapitalization.characters,
              decoration: const InputDecoration(
                labelText: 'Motivo do encerramento',
              ),
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('Voltar'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: const Text('Encerrar cobrança'),
          ),
        ],
      ),
    );
    if (confirmed != true) {
      reasonController.dispose();
      return;
    }
    try {
      await api.request(
        '$base/credit-card-recurrences/${recurrence['id']}/stop',
        {'reason': reasonController.text},
      );
      await load();
      message('Cobrança recorrente encerrada.');
    } catch (e) {
      message(e.toString().replaceFirst('Exception: ', ''));
    } finally {
      reasonController.dispose();
    }
  }

  Future<void> deleteCreditCardPurchase(RecordData purchase) async {
    final reasonController = TextEditingController();
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Excluir compra do cartão'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              purchase['recurringChargeId'] != null
                  ? 'Somente esta cobrança mensal será retirada da fatura. A recorrência continuará nos próximos meses.'
                  : purchase['installmentCount'] > 1
                  ? 'Todas as ${purchase['installmentCount']} parcelas desta compra serão retiradas das faturas.'
                  : 'Esta compra será retirada da fatura.',
            ),
            const SizedBox(height: 16),
            TextField(
              controller: reasonController,
              textCapitalization: TextCapitalization.characters,
              decoration: const InputDecoration(
                labelText: 'Motivo da exclusão',
              ),
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('Voltar'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: const Text('Excluir compra'),
          ),
        ],
      ),
    );
    if (confirmed != true) {
      reasonController.dispose();
      return;
    }
    try {
      await api.request(
        '$base/credit-card-purchases/${purchase['purchaseGroup']}/delete',
        {'reason': reasonController.text},
      );
      await load();
      message('Compra excluída e faturas atualizadas.');
    } catch (e) {
      message(e.toString().replaceFirst('Exception: ', ''));
    } finally {
      reasonController.dispose();
    }
  }
}

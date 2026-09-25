import 'package:file_selector/file_selector.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';

import 'finance.dart';
import 'entry_form.dart';
import 'access.dart';

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
      builder: (api, user, companyId, logout) => Workspace(
        api: api,
        user: user,
        initialCompany: companyId,
        onLogout: logout,
      ),
    ),
  );
}

class _ReportFeature extends StatelessWidget {
  final IconData icon;
  final String title;
  final String text;

  const _ReportFeature({
    required this.icon,
    required this.title,
    required this.text,
  });

  @override
  Widget build(BuildContext context) => SizedBox(
    width: 230,
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(icon, color: teal, size: 21),
        const SizedBox(width: 10),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(title, style: const TextStyle(fontWeight: FontWeight.w600)),
              const SizedBox(height: 4),
              Text(text, style: const TextStyle(fontSize: 12, height: 1.35)),
            ],
          ),
        ),
      ],
    ),
  );
}

class Workspace extends StatefulWidget {
  final FinanceApi api;
  final RecordData user;
  final int initialCompany;
  final VoidCallback onLogout;
  const Workspace({
    super.key,
    required this.api,
    required this.user,
    required this.initialCompany,
    required this.onLogout,
  });
  @override
  State<Workspace> createState() => _WorkspaceState();
}

class _WorkspaceState extends State<Workspace> {
  late final FinanceApi api = widget.api;
  List<RecordData> companies = [],
      titles = [],
      parties = [],
      accounts = [],
      settlements = [],
      movements = [],
      audit = [],
      plans = [],
      companyUsers = [],
      allUsers = [];
  late int company = widget.initialCompany;
  int page = 0, generation = 0;
  bool loading = true;
  bool reportBusy = false;
  double fontScale = 1;
  bool darkMode = false;
  String? error;
  String search = '', filter = 'Todos';
  String registrySearch = '';
  String historySearch = '';
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
  bool pageAllowed(int index) =>
      !financialUser || (index != 3 && index != 5 && index != 7);
  bool get canManageAccess {
    if (globalAdmin) return true;
    return companies.any(
      (item) => item['id'] == company && item['role'] == 'admin',
    );
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
      final cs = rows(await api.request('/companies'));
      final selected = cs.any((e) => e['id'] == company)
          ? company
          : cs.first['id'] as int;
      final data = await api.request('/companies/$selected/state');
      if (!mounted || request != generation) return;
      setState(() {
        companies = cs;
        company = selected;
        if (roleForCompany(selected) == 'user' && !pageAllowed(page)) page = 0;
        titles = rows(data['titles']);
        parties = rows(data['parties']);
        accounts = rows(data['accounts']);
        settlements = rows(data['settlements']);
        movements = rows(data['movements']);
        audit = rows(data['audit']);
        plans = rows(data['plans']);
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
      child: Scaffold(
        body: Row(
          children: [
            SizedBox(
              width: 224,
              child: ColoredBox(
                color: teal,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    const Padding(
                      padding: EdgeInsets.fromLTRB(25, 33, 20, 5),
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
                      padding: EdgeInsets.fromLTRB(27, 0, 20, 36),
                      child: Text(
                        'F I N A N C E I R O',
                        style: TextStyle(
                          color: Color(0xFFFFBE67),
                          fontSize: 11,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                    ),
                    for (int i = 0; i < labels.length; i++)
                      if (pageAllowed(i) && (i != 7 || canManageAccess))
                        Padding(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 12,
                            vertical: 4,
                          ),
                          child: ListTile(
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
                                color: page == i
                                    ? Colors.white
                                    : Colors.white70,
                                fontSize: 14,
                                fontWeight: page == i
                                    ? FontWeight.w600
                                    : FontWeight.normal,
                              ),
                            ),
                            onTap: () {
                              setState(() {
                                page = i;
                                search = '';
                                filter = i == 1 || i == 2
                                    ? 'Em aberto'
                                    : 'Todos';
                                if (i == 1 || i == 2) {
                                  selectedMonth = iso(DateTime.now())
                                      .substring(0, 7);
                                }
                              });
                              if (i == 7) loadAdminData();
                            },
                          ),
                        ),
                    const Spacer(),
                    Padding(
                      padding: const EdgeInsets.all(20),
                      child: Container(
                        padding: const EdgeInsets.all(14),
                        decoration: BoxDecoration(
                          color: Colors.white.withValues(alpha: .07),
                          borderRadius: BorderRadius.circular(12),
                        ),
                        child: const Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Row(
                              children: [
                                Icon(
                                  Icons.circle,
                                  size: 7,
                                  color: Color(0xFFBFD4AF),
                                ),
                                SizedBox(width: 8),
                                Text(
                                  'Ambiente de teste',
                                  style: TextStyle(
                                    color: Colors.white,
                                    fontSize: 12,
                                  ),
                                ),
                              ],
                            ),
                            SizedBox(height: 6),
                            Text(
                              'Dados salvos nesta máquina',
                              style: TextStyle(
                                color: Colors.white54,
                                fontSize: 11,
                              ),
                            ),
                          ],
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ),
            Expanded(
              child: Column(
                children: [
                  Container(
                    height: 82,
                    padding: const EdgeInsets.symmetric(horizontal: 30),
                    decoration: BoxDecoration(
                      color: darkMode ? const Color(0xFF1E1E1E) : Colors.white,
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
                        if (companies.isNotEmpty)
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
                                          setState(() {
                                            company = v;
                                            if (roleForCompany(v) == 'user' &&
                                                !pageAllowed(page)) {
                                              page = 0;
                                            }
                                          });
                                          load().then((_) {
                                            if (page == 7) loadAdminData();
                                          });
                                        }
                                      },
                              ),
                            ),
                          ),
                        const Spacer(),
                        IconButton(
                          tooltip: 'Diminuir letras',
                          onPressed: fontScale <= .9
                              ? null
                              : () => setState(
                                  () => fontScale = (fontScale - .1).clamp(
                                    .9,
                                    1.4,
                                  ),
                                ),
                          icon: const Icon(Icons.text_decrease, size: 20),
                        ),
                        Text(
                          '${(fontScale * 100).round()}%',
                          style: TextStyle(fontSize: 11, color: mutedText),
                        ),
                        IconButton(
                          tooltip: 'Aumentar letras',
                          onPressed: fontScale >= 1.4
                              ? null
                              : () => setState(
                                  () => fontScale = (fontScale + .1).clamp(
                                    .9,
                                    1.4,
                                  ),
                                ),
                          icon: const Icon(Icons.text_increase, size: 20),
                        ),
                        IconButton(
                          tooltip: darkMode
                              ? 'Usar modo claro'
                              : 'Usar modo escuro',
                          onPressed: () => setState(() => darkMode = !darkMode),
                          icon: Icon(
                            darkMode
                                ? Icons.light_mode_outlined
                                : Icons.dark_mode_outlined,
                            size: 20,
                          ),
                        ),
                        Text(
                          dateLabel(iso(DateTime.now())),
                          style: TextStyle(color: mutedText, fontSize: 12),
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
                            padding: const EdgeInsets.all(30),
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.stretch,
                              children: [
                                Wrap(
                                  alignment: WrapAlignment.spaceBetween,
                                  crossAxisAlignment: WrapCrossAlignment.start,
                                  spacing: 16,
                                  runSpacing: 16,
                                  children: [
                                    SizedBox(
                                      width: 330,
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
                                            label: const Text('Criar despesa'),
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
                                                Icons.picture_as_pdf_outlined,
                                                size: 19,
                                              ),
                                              label: const Text('Relatórios'),
                                            ),
                                        ],
                                      ),
                                  ],
                                ),
                                const SizedBox(height: 25),
                                if (page == 0) ...dashboard(),
                                if (page == 1 || page == 2) ...titlePage(),
                                if (page == 3 && !financialUser) ...cashPage(),
                                if (page == 4) ...registryPage(),
                                if (page == 5 && !financialUser)
                                  ...historyPage(),
                                if (page == 6) ...reportsPage(),
                                if (page == 7 && canManageAccess)
                                  ...adminPage(),
                                const SizedBox(height: 25),
                                Text(
                                  'MONALISA SISTEMAS  /  Financeiro local • versão inicial',
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
                ],
              ),
            ),
          ],
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
              return ListTile(
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
                  icon: Icon(active ? Icons.lock_outline : Icons.lock_open),
                  label: Text(active ? 'BLOQUEAR' : 'LIBERAR'),
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
      ],
      (values) async {
        await api.request('$base/users/${values['userId']}/link', {
          'role': values['role'],
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
      });
      await loadCompanyUsers();
      message(active ? 'ACESSO LIBERADO.' : 'ACESSO BLOQUEADO.');
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
                _ReportFeature(
                  icon: Icons.assessment_outlined,
                  title: 'Resumo do período',
                  text: 'Recebido, pago, resultado e valores em aberto.',
                ),
                _ReportFeature(
                  icon: Icons.payments_outlined,
                  title: 'Baixas realizadas',
                  text: 'Pagamentos e recebimentos que movimentaram o caixa.',
                ),
                _ReportFeature(
                  icon: Icons.list_alt_outlined,
                  title: 'Lançamentos',
                  text: 'Contas do período com valor, saldo e situação.',
                ),
              ],
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

  Future<void> saveFinancialReport() async {
    if (reportBusy) return;
    setState(() => reportBusy = true);
    try {
      final uri = Uri.parse(
        '$apiBase/api/companies/$company/reports/financial?start=${iso(reportStart)}&end=${iso(reportEnd)}',
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
      if (kIsWeb) {
        await file.saveTo(fileName);
      } else {
        final location = await getSaveLocation(
          suggestedName: fileName,
          acceptedTypeGroups: const [
            XTypeGroup(label: 'Documento PDF', extensions: ['pdf']),
          ],
        );
        if (location == null) return;
        await file.saveTo(location.path);
      }
      message('Relatório em PDF gerado com sucesso.');
    } catch (error) {
      message(error.toString().replaceFirst('Exception: ', ''));
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
        'Lançamentos do mês separados pelo plano de contas.',
      ),
      'month': (
        receivable
            ? 'Extrato de contas a receber'
            : 'Extrato de contas a pagar',
        'Todas as contas com vencimento no mês selecionado.',
      ),
      'future': (
        receivable
            ? 'Contas a receber dos próximos meses'
            : 'Contas dos próximos meses',
        'Contas posteriores ao mês selecionado, separadas por mês.',
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
        '$apiBase/api/companies/$company/reports/${receivable ? 'receivables' : 'payables'}?month=$selectedMonth&type=$type',
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
      if (kIsWeb) {
        await file.saveTo(fileName);
      } else {
        final location = await getSaveLocation(
          suggestedName: fileName,
          acceptedTypeGroups: const [
            XTypeGroup(label: 'Documento PDF', extensions: ['pdf']),
          ],
        );
        if (location == null) return;
        await file.saveTo(location.path);
      }
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
          (settlement['penalty'] as num) -
          (settlement['discount'] as num);
      if (title['kind'] == 'receivable') {
        receivedThisMonth += cash;
      } else if (title['kind'] == 'payable') {
        paidThisMonth += cash;
      }
    }
    final monthlyResult = receivedThisMonth - paidThisMonth;
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
                  (filter == 'Todos' ||
                      (filter == 'Em aberto'
                          ? e['cancelled'] == 0 && e['remaining'] > 0
                          : status(e) == filter)),
            )
            .toList()
          ..sort((a, b) => (a['due'] as String).compareTo(b['due'] as String));
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

  Widget titleTable(List<RecordData> list, {bool colorRows = false}) =>
      list.isEmpty
      ? empty('Nenhum lançamento por aqui. Comece pelo botão acima.')
      : LayoutBuilder(
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
                columns:
                    [
                          'Descrição / pessoa',
                          'Vencimento',
                          'Valor',
                          'Em aberto',
                          'Situação',
                          '',
                        ]
                        .map(
                          (s) => DataColumn(
                            label: Text(
                              s,
                              style: TextStyle(color: mutedText, fontSize: 12),
                            ),
                          ),
                        )
                        .toList(),
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
                                      if ((t['branch'] ?? '')
                                          .toString()
                                          .isNotEmpty)
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
                                      t['kind'] == 'payable'
                                          ? 'Pagar'
                                          : 'Receber',
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
        'Dinheiro que já entrou e saiu',
        movements.isEmpty
            ? empty('As baixas e transferências aparecem aqui.')
            : Column(
                children: movements
                    .map(
                      (m) => ListTile(
                        contentPadding: EdgeInsets.zero,
                        leading: CircleAvatar(
                          backgroundColor: const Color(0xFFF1F5EF),
                          child: Icon(
                            m['amount'] >= 0
                                ? Icons.south_west
                                : Icons.north_east,
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
                            color: m['amount'] >= 0
                                ? teal
                                : const Color(0xFFB35734),
                          ),
                        ),
                      ),
                    )
                    .toList(),
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

  List<Widget> registryPage() => [
    Wrap(
      spacing: 10,
      runSpacing: 10,
      children: [
        FilledButton.icon(
          onPressed: partyForm,
          icon: const Icon(Icons.person_add_alt),
          label: const Text('Adicionar cliente / fornecedor'),
        ),
        OutlinedButton.icon(
          onPressed: accountForm,
          icon: const Icon(Icons.account_balance_outlined),
          label: const Text('Conta financeira'),
        ),
        OutlinedButton.icon(
          onPressed: planForm,
          icon: const Icon(Icons.account_tree_outlined),
          label: const Text('Plano de contas'),
        ),
        OutlinedButton.icon(
          onPressed: backup,
          icon: const Icon(Icons.save_outlined),
          label: const Text('Criar backup'),
        ),
      ],
    ),
    const SizedBox(height: 22),
    TextField(
      decoration: const InputDecoration(
        hintText: 'Pesquisar cliente ou fornecedor',
        prefixIcon: Icon(Icons.search),
      ),
      onChanged: (value) => setState(() => registrySearch = value),
    ),
    const SizedBox(height: 22),
    panel('Clientes', _partyList('customer')),
    const SizedBox(height: 22),
    panel('Fornecedores', _partyList('supplier')),
    const SizedBox(height: 22),
    panel(
      'Planos de contas',
      Column(
        children: plans
            .map(
              (p) => ListTile(
                contentPadding: EdgeInsets.zero,
                leading: const Icon(Icons.account_tree_outlined, color: teal),
                title: Text(p['name']),
                subtitle: Text(
                  'Juros: ${p['interestRate']}%  •  Multa: ${p['penaltyRate']}%  •  Desconto: ${p['discountRate']}%',
                ),
                trailing: Row(
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
                ),
              ),
            )
            .toList(),
      ),
    ),
    const SizedBox(height: 22),
    panel(
      'Contas financeiras',
      Column(
        children: accounts
            .map(
              (a) => ListTile(
                contentPadding: EdgeInsets.zero,
                leading: const Icon(
                  Icons.account_balance_wallet_outlined,
                  color: teal,
                ),
                title: Text(a['name']),
                subtitle: Text('Saldo inicial: ${money(a['opening'])}'),
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
  ];

  Future<void> deleteRegistry(String type, RecordData item) async {
    final labels = {
      'party': 'cadastro',
      'plan': 'plano de contas',
      'account': 'conta financeira',
    };
    final paths = {'party': 'parties', 'plan': 'plans', 'account': 'accounts'};
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
          'Valor total (R\$)',
          required: true,
          money: true,
          helper: 'Informe o valor completo. Se houver parcelas, vamos dividir este total.',
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
          suggestions: plans.map((p) => p['name'] as String).toList(),
        ),
        FormFieldSpec(
          'branch',
          'Filial ou unidade',
          initial: 'LOJA MATRIZ',
          suggestions: const [
            'LOJA MATRIZ',
            'LOJA 02',
            'CENTRO DE DISTRIBUIÇÃO',
          ],
        ),
        FormFieldSpec(
          'costCenter',
          'Setor ou centro de custo',
          hint: 'Ex.: AÇOUGUE, PADARIA OU ADMINISTRAÇÃO',
          suggestions: const [
            'ADMINISTRAÇÃO',
            'ESTOQUE',
            'AÇOUGUE',
            'HORTIFRÚTI',
            'PADARIA',
            'FRENTE DE CAIXA',
            'MANUTENÇÃO',
          ],
        ),
        FormFieldSpec(
          'installments',
          'Quantidade de parcelas mensais',
          initial: '1',
          required: true,
          helper: 'Use 1 para uma conta única, ou informe quantas parcelas mensais deseja.',
        ),
        FormFieldSpec(
          'notes',
          'Alguma observação?',
          hint: 'Ex.: combinar o pagamento com o fornecedor',
        ),
      ],
      (v) async {
        final installmentCount = int.tryParse(v['installments']!) ?? 0;
        final totalAmount = parseMoney(v['amount']!);
        final schedule = installmentCount > 1
            ? await installmentSchedule(
                totalAmount,
                installmentCount,
                v['due']!,
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
          'branch': v['branch'],
          'costCenter': v['costCenter'],
          'installments': installmentCount,
          'installmentAmounts': schedule?.map((e) => e['amount']).toList(),
          'installmentDues': schedule?.map((e) => e['due']).toList(),
        });
      },
      note: 'O valor total será dividido entre as parcelas. O saldo da conta só muda quando houver baixa.',
    );
  }

  Future<List<RecordData>?> installmentSchedule(
    int totalAmount,
    int count,
    String firstDue,
  ) async {
    final first = DateTime.parse(firstDue);
    final amounts = List.generate(
      count,
      (i) => TextEditingController(
        text: inputMoney(
          totalAmount ~/ count + (i < totalAmount % count ? 1 : 0),
        ),
      ),
    );
    final dues = List.generate(count, (i) {
      final month = DateTime(first.year, first.month + i, 1);
      final lastDay = DateTime(month.year, month.month + 1, 0).day;
      final due = DateTime(
        month.year,
        month.month,
        first.day > lastDay ? lastDay : first.day,
      );
      return TextEditingController(text: dateLabel(iso(due)));
    });
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
                  'Valor total: ${money(totalAmount)}. Ajuste valores e vencimentos; a soma deve fechar o total.',
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

  void settleForm(RecordData t) {
    final path = '$base/titles/${t['id']}/settle';
    int suggested(String key) =>
        ((t['remaining'] as num) * ((t[key] ?? 0) as num) / 100).round();
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
          'interest',
          'Juros (R\$)',
          initial: inputMoney(suggested('interestRate')),
          money: true,
          advanced: true,
        ),
        FormFieldSpec(
          'penalty',
          'Multa (R\$)',
          initial: inputMoney(suggested('penaltyRate')),
          money: true,
          advanced: true,
        ),
        FormFieldSpec(
          'discount',
          'Desconto (R\$)',
          initial: inputMoney(suggested('discountRate')),
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
          'date': v['date'],
        });
      },
      note:
          '${t['description']} • Em aberto: ${money(t['remaining'])}\nMovimento no caixa = principal + juros + multa − desconto. Para baixa parcial, reduza o principal.',
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

  void planForm([RecordData? plan]) {
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
          'interestRate',
          'Juros padrão (%)',
          initial: '${plan?['interestRate'] ?? 0}',
        ),
        FormFieldSpec(
          'penaltyRate',
          'Multa padrão (%)',
          initial: '${plan?['penaltyRate'] ?? 0}',
        ),
        FormFieldSpec(
          'discountRate',
          'Desconto padrão (%)',
          initial: '${plan?['discountRate'] ?? 0}',
        ),
      ],
      (v) async {
        double rate(String key) => double.parse(v[key]!.replaceAll(',', '.'));
        await api.request(
          plan == null ? '$base/plans' : '$base/plans/${plan['id']}/edit',
          {
            'name': v['name'],
            'interestRate': rate('interestRate'),
            'penaltyRate': rate('penaltyRate'),
            'discountRate': rate('discountRate'),
          },
        );
      },
      note: 'Esses percentuais serão sugeridos automaticamente ao registrar a baixa de lançamentos vinculados a este plano.',
    );
  }

  void accountForm() {
    final path = '$base/accounts';
    form(
      'Nova conta financeira',
      [
        FormFieldSpec(
          'name',
          'Como você quer chamar esta conta?',
          required: true,
          hint: 'Ex.: Caixa da loja ou Banco principal',
        ),
        FormFieldSpec(
          'opening',
          'Saldo inicial (R\$)',
          initial: '0,00',
          money: true,
          helper: 'Quanto já existe nesta conta antes dos lançamentos? Se estiver vazia, deixe zero.',
        ),
      ],
      (v) async {
        await api.request(path, {
          'name': v['name'],
          'opening': parseMoney(v['opening']!),
        });
      },
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

  Future<void> details(RecordData t) async {
    final history = settlements.where((s) => s['titleId'] == t['id']).toList();
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
                    trailing: s['reversed'] == 1
                        ? null
                        : TextButton(
                            onPressed: () =>
                                Navigator.pop(c, 'reverse/${s['id']}'),
                            child: const Text('Estornar'),
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
            onPressed: () => Navigator.pop(c),
            child: const Text('Fechar'),
          ),
        ],
      ),
    );
    if (action == null || !mounted) return;
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
        ),
        FormFieldSpec(
          'branch',
          'Filial ou unidade',
          initial: t['branch'] ?? '',
        ),
        FormFieldSpec(
          'costCenter',
          'Setor ou centro de custo',
          initial: t['costCenter'] ?? '',
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
          'branch': v['branch'],
          'costCenter': v['costCenter'],
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
}

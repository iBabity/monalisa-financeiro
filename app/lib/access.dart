import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'entry_form.dart';
import 'finance.dart';

const _teal = Color(0xFF073F43);
const _cream = Color(0xFFF7F2EA);

typedef WorkspaceBuilder = Widget Function(
  FinanceApi api,
  RecordData user,
  int companyId,
  VoidCallback logout,
);

class AccessGate extends StatefulWidget {
  final WorkspaceBuilder builder;
  const AccessGate({super.key, required this.builder});

  @override
  State<AccessGate> createState() => _AccessGateState();
}

class _AccessGateState extends State<AccessGate> {
  final api = FinanceApi();
  final name = TextEditingController();
  final username = TextEditingController();
  final pin = TextEditingController();
  final confirmPin = TextEditingController();
  bool loading = true;
  bool busy = false;
  bool needsSetup = false;
  bool hidePin = true;
  bool darkMode = false;
  double fontScale = 1;
  String? error;
  RecordData? user;
  List<RecordData> companies = [];
  int? selectedCompany;

  @override
  void initState() {
    super.initState();
    checkSetup();
  }

  @override
  void dispose() {
    name.dispose();
    username.dispose();
    pin.dispose();
    confirmPin.dispose();
    api.client.close();
    super.dispose();
  }

  Future<void> checkSetup() async {
    try {
      final result = await api.request('/auth/bootstrap');
      if (mounted) {
        setState(() {
          needsSetup = result['needsSetup'] == true;
          loading = false;
        });
      }
    } catch (e) {
      if (mounted) {
        setState(() {
          error = cleanError(e);
          loading = false;
        });
      }
    }
  }

  String cleanError(Object value) =>
      value.toString().replaceFirst('Exception: ', '');

  Future<void> submit() async {
    if (busy) return;
    final cleanUser = username.text.trim();
    final cleanPin = pin.text.trim();
    if (needsSetup && name.text.trim().isEmpty) {
      setState(() => error = 'INFORME O NOME DO ADMINISTRADOR GERAL.');
      return;
    }
    if (cleanUser.length < 3) {
      setState(() => error = 'INFORME UM USUÁRIO COM PELO MENOS 3 CARACTERES.');
      return;
    }
    if (!RegExp(r'^\d{6,12}$').hasMatch(cleanPin)) {
      setState(() => error = 'O PIN DEVE CONTER DE 6 A 12 NÚMEROS.');
      return;
    }
    if (needsSetup && cleanPin != confirmPin.text.trim()) {
      setState(() => error = 'OS DOIS PINs PRECISAM SER IGUAIS.');
      return;
    }
    setState(() {
      busy = true;
      error = null;
    });
    try {
      final result = await api.request(
        needsSetup ? '/auth/bootstrap' : '/auth/login',
        {
          if (needsSetup) 'name': name.text.trim(),
          'username': cleanUser,
          'pin': cleanPin,
        },
      );
      api.token = result['token'];
      final authenticated = RecordData.from(result['user']);
      final available = (authenticated['companies'] as List)
          .map((item) => RecordData.from(item))
          .toList();
      if (!mounted) return;
      setState(() {
        user = authenticated;
        companies = available;
        selectedCompany = available.length == 1 ? available.first['id'] : null;
        busy = false;
        pin.clear();
        confirmPin.clear();
      });
    } catch (e) {
      if (mounted) {
        setState(() {
          error = cleanError(e);
          busy = false;
        });
      }
    }
  }

  Future<void> logout() async {
    try {
      await api.request('/auth/logout', {});
    } catch (_) {}
    if (!mounted) return;
    api.token = null;
    setState(() {
      user = null;
      companies = [];
      selectedCompany = null;
      error = null;
      needsSetup = false;
      username.clear();
      pin.clear();
    });
  }

  @override
  Widget build(BuildContext context) {
    if (loading) {
      return const Scaffold(body: Center(child: CircularProgressIndicator()));
    }
    if (user != null && selectedCompany != null) {
      return widget.builder(api, user!, selectedCompany!, logout);
    }
    return Theme(
      data: darkMode
          ? ThemeData.dark(useMaterial3: true).copyWith(
              colorScheme: ColorScheme.fromSeed(
                seedColor: _teal,
                brightness: Brightness.dark,
              ),
            )
          : Theme.of(context),
      child: MediaQuery(
        data: MediaQuery.of(context)
            .copyWith(textScaler: TextScaler.linear(fontScale)),
        child: user == null ? accessScreen() : companyScreen(),
      ),
    );
  }

  Widget accessScreen() => Scaffold(
    backgroundColor: darkMode ? const Color(0xFF111817) : _cream,
    body: Stack(
      children: [
        Positioned.fill(
          child: Row(
            children: [
              if (MediaQuery.sizeOf(context).width >= 850)
                Expanded(
                  flex: 5,
                  child: Container(
                    color: _teal,
                    padding: const EdgeInsets.all(54),
                    child: const Column(
                      mainAxisAlignment: MainAxisAlignment.center,
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          'monalisa',
                          style: TextStyle(
                            color: Colors.white,
                            fontSize: 54,
                            fontWeight: FontWeight.w800,
                            letterSpacing: -2,
                          ),
                        ),
                        Text(
                          'F I N A N C E I R O',
                          style: TextStyle(
                            color: Color(0xFFFFB651),
                            fontSize: 15,
                            fontWeight: FontWeight.w600,
                          ),
                        ),
                        SizedBox(height: 42),
                        Text(
                          'MAIS CONTROLE.\nMAIS ESPAÇO PARA CRESCER.',
                          style: TextStyle(
                            color: Colors.white,
                            fontSize: 28,
                            height: 1.12,
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                        SizedBox(height: 16),
                        Text(
                          'CONTAS A PAGAR E A RECEBER EM UM AMBIENTE SEGURO PARA TODAS AS LOJAS.',
                          style: TextStyle(
                            color: Colors.white70,
                            fontSize: 12,
                            height: 1.5,
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              Expanded(
                flex: 6,
                child: Center(
                  child: SingleChildScrollView(
                    padding: const EdgeInsets.all(32),
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(maxWidth: 430),
                      child: Card(
                        elevation: 0,
                        child: Padding(
                          padding: const EdgeInsets.all(32),
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              if (MediaQuery.sizeOf(context).width < 850) ...[
                                const Text(
                                  'monalisa',
                                  textAlign: TextAlign.center,
                                  style: TextStyle(
                                    color: _teal,
                                    fontSize: 34,
                                    fontWeight: FontWeight.w800,
                                    letterSpacing: -1.5,
                                  ),
                                ),
                                const Text(
                                  'F I N A N C E I R O',
                                  textAlign: TextAlign.center,
                                  style: TextStyle(
                                    color: Color(0xFFB85B11),
                                    fontSize: 10,
                                    fontWeight: FontWeight.w600,
                                  ),
                                ),
                                const SizedBox(height: 22),
                              ],
                              Icon(
                                needsSetup
                                    ? Icons.admin_panel_settings_outlined
                                    : Icons.lock_outline,
                                color: _teal,
                                size: 34,
                              ),
                              const SizedBox(height: 18),
                              Text(
                                needsSetup
                                    ? 'CRIAR ADMINISTRADOR GERAL'
                                    : 'ENTRAR NO FINANCEIRO',
                                textAlign: TextAlign.center,
                                style: const TextStyle(
                                  fontSize: 22,
                                  fontWeight: FontWeight.w700,
                                ),
                              ),
                              const SizedBox(height: 8),
                              Text(
                                needsSetup
                                    ? 'ESTE SERÁ O ACESSO RESPONSÁVEL POR TODAS AS EMPRESAS E LOJAS.'
                                    : 'USE SEU USUÁRIO E PIN PESSOAL.',
                                textAlign: TextAlign.center,
                                style: TextStyle(
                                  color: Theme.of(context)
                                      .colorScheme
                                      .onSurfaceVariant,
                                  fontSize: 12,
                                ),
                              ),
                              const SizedBox(height: 26),
                              if (needsSetup) ...[
                                TextField(
                                  controller: name,
                                  textCapitalization:
                                      TextCapitalization.characters,
                                  inputFormatters: [UpperCaseTextFormatter()],
                                  decoration: const InputDecoration(
                                    labelText: 'NOME COMPLETO',
                                    prefixIcon: Icon(Icons.person_outline),
                                  ),
                                ),
                                const SizedBox(height: 14),
                              ],
                              TextField(
                                controller: username,
                                textCapitalization:
                                    TextCapitalization.characters,
                                inputFormatters: [UpperCaseTextFormatter()],
                                decoration: const InputDecoration(
                                  labelText: 'USUÁRIO',
                                  prefixIcon: Icon(Icons.badge_outlined),
                                  hintText: 'EX.: MARIA.GERENTE',
                                ),
                              ),
                              const SizedBox(height: 14),
                              TextField(
                                controller: pin,
                                obscureText: hidePin,
                                keyboardType: TextInputType.number,
                                inputFormatters: [
                                  FilteringTextInputFormatter.digitsOnly,
                                  LengthLimitingTextInputFormatter(12),
                                ],
                                onSubmitted: (_) => submit(),
                                decoration: InputDecoration(
                                  labelText: needsSetup
                                      ? 'CRIE UM PIN'
                                      : 'PIN DE ACESSO',
                                  prefixIcon: const Icon(Icons.pin_outlined),
                                  suffixIcon: IconButton(
                                    onPressed: () =>
                                        setState(() => hidePin = !hidePin),
                                    icon: Icon(
                                      hidePin
                                          ? Icons.visibility_outlined
                                          : Icons.visibility_off_outlined,
                                    ),
                                  ),
                                ),
                              ),
                              if (needsSetup) ...[
                                const SizedBox(height: 14),
                                TextField(
                                  controller: confirmPin,
                                  obscureText: hidePin,
                                  keyboardType: TextInputType.number,
                                  inputFormatters: [
                                    FilteringTextInputFormatter.digitsOnly,
                                    LengthLimitingTextInputFormatter(12),
                                  ],
                                  onSubmitted: (_) => submit(),
                                  decoration: const InputDecoration(
                                    labelText: 'CONFIRME O PIN',
                                    prefixIcon: Icon(
                                      Icons.verified_user_outlined,
                                    ),
                                  ),
                                ),
                              ],
                              if (error != null)
                                Padding(
                                  padding: const EdgeInsets.only(top: 14),
                                  child: Text(
                                    error!,
                                    style: const TextStyle(
                                      color: Colors.redAccent,
                                      fontSize: 12,
                                    ),
                                  ),
                                ),
                              const SizedBox(height: 22),
                              FilledButton.icon(
                                onPressed: busy ? null : submit,
                                icon: busy
                                    ? const SizedBox.square(
                                        dimension: 18,
                                        child: CircularProgressIndicator(
                                          strokeWidth: 2,
                                        ),
                                      )
                                    : const Icon(Icons.login),
                                label: Text(
                                  needsSetup ? 'CRIAR E ENTRAR' : 'ENTRAR',
                                ),
                              ),
                              const SizedBox(height: 16),
                              Text(
                                'DADOS SALVOS NESTA MÁQUINA • ACESSO LOCAL',
                                textAlign: TextAlign.center,
                                style: TextStyle(
                                  color: Theme.of(context)
                                      .colorScheme
                                      .onSurfaceVariant,
                                  fontSize: 10,
                                ),
                              ),
                            ],
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
        Positioned(
          right: 20,
          top: 16,
          child: Row(
            children: [
              IconButton(
                tooltip: 'DIMINUIR LETRAS',
                onPressed: fontScale <= .9
                    ? null
                    : () => setState(() => fontScale -= .1),
                icon: const Icon(Icons.text_decrease),
              ),
              Text('${(fontScale * 100).round()}%'),
              IconButton(
                tooltip: 'AUMENTAR LETRAS',
                onPressed: fontScale >= 1.4
                    ? null
                    : () => setState(() => fontScale += .1),
                icon: const Icon(Icons.text_increase),
              ),
              IconButton(
                tooltip: darkMode ? 'USAR MODO CLARO' : 'USAR MODO ESCURO',
                onPressed: () => setState(() => darkMode = !darkMode),
                icon: Icon(
                  darkMode
                      ? Icons.light_mode_outlined
                      : Icons.dark_mode_outlined,
                ),
              ),
            ],
          ),
        ),
      ],
    ),
  );

  Widget companyScreen() => Scaffold(
    appBar: AppBar(
      title: const Text('ESCOLHA A EMPRESA'),
      actions: [
        TextButton.icon(
          onPressed: logout,
          icon: const Icon(Icons.logout),
          label: const Text('SAIR'),
        ),
        const SizedBox(width: 16),
      ],
    ),
    body: Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 860),
        child: Padding(
          padding: const EdgeInsets.all(32),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                'OLÁ, ${user!['name']}',
                style: const TextStyle(
                  fontSize: 26,
                  fontWeight: FontWeight.w700,
                  color: _teal,
                ),
              ),
              const SizedBox(height: 6),
              Text(
                user!['globalAdmin'] == true
                    ? 'ADMINISTRADOR GERAL • ACESSO A TODAS AS LOJAS'
                    : 'SELECIONE UMA DAS EMPRESAS LIBERADAS PARA O SEU ACESSO.',
              ),
              const SizedBox(height: 26),
              if (companies.isEmpty)
                const Card(
                  child: Padding(
                    padding: EdgeInsets.all(24),
                    child: Text(
                      'NENHUMA EMPRESA ESTÁ DISPONÍVEL PARA ESTE ACESSO.',
                    ),
                  ),
                )
              else
                Expanded(
                  child: GridView.builder(
                    gridDelegate:
                        const SliverGridDelegateWithMaxCrossAxisExtent(
                          maxCrossAxisExtent: 380,
                          mainAxisExtent: 150,
                          crossAxisSpacing: 16,
                          mainAxisSpacing: 16,
                        ),
                    itemCount: companies.length,
                    itemBuilder: (_, index) {
                      final item = companies[index];
                      return Card(
                        child: InkWell(
                          borderRadius: BorderRadius.circular(18),
                          onTap: () =>
                              setState(() => selectedCompany = item['id']),
                          child: Padding(
                            padding: const EdgeInsets.all(20),
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                const Icon(
                                  Icons.storefront_outlined,
                                  color: _teal,
                                ),
                                const Spacer(),
                                Text(
                                  item['name'],
                                  maxLines: 2,
                                  overflow: TextOverflow.ellipsis,
                                  style: const TextStyle(
                                    fontWeight: FontWeight.w700,
                                  ),
                                ),
                                const SizedBox(height: 4),
                                Text(
                                  cnpjLabel(item['cnpj']),
                                  style: const TextStyle(fontSize: 12),
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
        ),
      ),
    ),
  );
}

using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed partial class Ledger
{
    readonly string path;
    readonly object gate = new();
    public Ledger()
    {
        var configuredPath = Environment.GetEnvironmentVariable("FINANCE_DB");
        var developmentPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "data", "financeiro.db"));
        var runningFromProject = Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "..", "app"));
        path = Path.GetFullPath(configuredPath ?? (runningFromProject ? developmentPath : Path.Combine(AppContext.BaseDirectory, "data", "financeiro.db")));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var c = Open();
        Run(c, """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS companies(id INTEGER PRIMARY KEY,name TEXT NOT NULL,cnpj TEXT NOT NULL UNIQUE);
        CREATE TABLE IF NOT EXISTS parties(id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),name TEXT NOT NULL,document TEXT NOT NULL,type TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS accounts(id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),name TEXT NOT NULL,opening INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS plans(
            id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),
            name TEXT NOT NULL,kind TEXT NOT NULL DEFAULT 'payable' CHECK(kind IN ('payable','receivable')),
            interestRate REAL NOT NULL DEFAULT 0,interestMode TEXT NOT NULL DEFAULT 'percentage' CHECK(interestMode IN ('percentage','value')),interestValue INTEGER NOT NULL DEFAULT 0,
            penaltyRate REAL NOT NULL DEFAULT 0,penaltyMode TEXT NOT NULL DEFAULT 'percentage' CHECK(penaltyMode IN ('percentage','value')),penaltyValue INTEGER NOT NULL DEFAULT 0,
            discountRate REAL NOT NULL DEFAULT 0,discountMode TEXT NOT NULL DEFAULT 'percentage' CHECK(discountMode IN ('percentage','value')),discountValue INTEGER NOT NULL DEFAULT 0,
            UNIQUE(companyId,kind,name));
        CREATE TABLE IF NOT EXISTS titles(id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),kind TEXT NOT NULL CHECK(kind IN ('payable','receivable')),description TEXT NOT NULL,amount INTEGER NOT NULL CHECK(amount>0),due TEXT NOT NULL,partyId INTEGER REFERENCES parties(id),category TEXT NOT NULL,notes TEXT NOT NULL,cancelled INTEGER NOT NULL DEFAULT 0,created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
        CREATE TABLE IF NOT EXISTS settlements(id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),titleId INTEGER NOT NULL REFERENCES titles(id),accountId INTEGER NOT NULL REFERENCES accounts(id),principal INTEGER NOT NULL CHECK(principal>0),interest INTEGER NOT NULL,penalty INTEGER NOT NULL,discount INTEGER NOT NULL,date TEXT NOT NULL,reversed INTEGER NOT NULL DEFAULT 0,reason TEXT NOT NULL DEFAULT '');
        CREATE TABLE IF NOT EXISTS movements(id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),accountId INTEGER NOT NULL REFERENCES accounts(id),amount INTEGER NOT NULL,date TEXT NOT NULL,description TEXT NOT NULL,settlementId INTEGER REFERENCES settlements(id));
        CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),action TEXT NOT NULL,detail TEXT NOT NULL,created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
        CREATE INDEX IF NOT EXISTS ix_titles_company_due ON titles(companyId,due);
        CREATE INDEX IF NOT EXISTS ix_movements_company ON movements(companyId,accountId);
        CREATE INDEX IF NOT EXISTS ix_settlements_title ON settlements(titleId,reversed);
        """);
        if (!Query(c, "PRAGMA table_info(titles)").Any(r => (string)r["name"]! == "installmentGroup"))
        {
            // Keep a consistent copy before upgrading any pre-existing financial data.
            if (Query(c, "SELECT id FROM titles LIMIT 1").Count > 0) Backup();
            using var migration = c.BeginTransaction();
            Run(c, "ALTER TABLE titles ADD COLUMN installmentGroup TEXT; ALTER TABLE titles ADD COLUMN installmentNumber INTEGER NOT NULL DEFAULT 1; ALTER TABLE titles ADD COLUMN installmentCount INTEGER NOT NULL DEFAULT 1;");
            // Old releases recorded the complete creation request. Link only unambiguous matches.
            foreach (var entry in Query(c, "SELECT * FROM audit WHERE action='Lançamento' ORDER BY id"))
            {
                TitleInput? original;
                try { original = JsonSerializer.Deserialize<TitleInput>((string)entry["detail"]!); } catch (JsonException) { continue; }
                if (original is null || original.Installments < 2 || original.Installments > 120) continue;
                var ids = new List<long>();
                if (!DateOnly.TryParseExact(original.Due, "yyyy-MM-dd", out var firstDue)) continue;
                for (var i = 0; i < original.Installments; i++)
                {
                    var candidates = Query(c, "SELECT id FROM titles WHERE companyId=@p0 AND kind=@p1 AND description=@p2 AND amount=@p3 AND due=@p4 AND partyId IS @p5 AND category=@p6 AND notes=@p7 AND created=@p8 AND installmentGroup IS NULL", entry["companyId"], original.Kind, original.Description.Trim() + $" ({i + 1}/{original.Installments})", original.Amount / original.Installments + (i < original.Amount % original.Installments ? 1 : 0), firstDue.AddMonths(i).ToString("yyyy-MM-dd"), original.PartyId, original.Category ?? "", original.Notes ?? "", entry["created"]);
                    if (candidates.Count != 1) { ids.Clear(); break; }
                    ids.Add(Number(candidates[0], "id"));
                }
                if (ids.Count != original.Installments) continue;
                var group = Guid.NewGuid().ToString("N");
                for (var i = 0; i < ids.Count; i++) Run(c, "UPDATE titles SET installmentGroup=@p0,installmentNumber=@p1,installmentCount=@p2 WHERE id=@p3", group, i + 1, ids.Count, ids[i]);
            }
            Run(c, "PRAGMA user_version=2"); migration.Commit();
        }
        if (!Query(c, "PRAGMA table_info(titles)").Any(r => (string)r["name"]! == "deleted"))
        {
            if (Query(c, "SELECT id FROM titles LIMIT 1").Count > 0) Backup();
            Run(c, "ALTER TABLE titles ADD COLUMN deleted INTEGER NOT NULL DEFAULT 0; PRAGMA user_version=3");
        }
        if (!Query(c, "PRAGMA table_info(titles)").Any(r => (string)r["name"]! == "interestRate"))
        {
            if (Query(c, "SELECT id FROM titles LIMIT 1").Count > 0) Backup();
            Run(c, "ALTER TABLE titles ADD COLUMN interestRate REAL NOT NULL DEFAULT 0; ALTER TABLE titles ADD COLUMN penaltyRate REAL NOT NULL DEFAULT 0; ALTER TABLE titles ADD COLUMN discountRate REAL NOT NULL DEFAULT 0; PRAGMA user_version=4");
        }
        if (!Query(c, "PRAGMA table_info(parties)").Any(r => (string)r["name"]! == "phone"))
        {
            if (Query(c, "SELECT id FROM parties LIMIT 1").Count > 0) Backup();
            Run(c, "ALTER TABLE parties ADD COLUMN phone TEXT NOT NULL DEFAULT ''; ALTER TABLE parties ADD COLUMN email TEXT NOT NULL DEFAULT ''; PRAGMA user_version=5");
        }
        var version = Convert.ToInt32(Query(c, "PRAGMA user_version")[0]["user_version"]);
        if (version < 6) { Run(c, "UPDATE companies SET name=upper(name); UPDATE parties SET name=upper(name); UPDATE accounts SET name=upper(name); UPDATE plans SET name=upper(name); UPDATE titles SET description=upper(description),category=upper(category),notes=upper(notes); PRAGMA user_version=6"); }
        if (version < 7)
        {
            foreach (var tableColumn in new[] { ("companies", "name"), ("parties", "name"), ("accounts", "name"), ("titles", "description"), ("titles", "category"), ("titles", "notes") })
                foreach (var row in Query(c, $"SELECT id,{tableColumn.Item2} AS value FROM {tableColumn.Item1}")) Run(c, $"UPDATE {tableColumn.Item1} SET {tableColumn.Item2}=@p0 WHERE id=@p1", ((string)row["value"]!).ToUpperInvariant(), row["id"]);
            foreach (var companyRow in Query(c, "SELECT id FROM companies"))
            {
                var companyId = Convert.ToInt32(companyRow["id"]);
                var groups = Query(c, "SELECT id,name FROM plans WHERE companyId=@p0 ORDER BY id", companyId)
                    .GroupBy(row => ((string)row["name"]!).ToUpperInvariant(), StringComparer.Ordinal);
                foreach (var group in groups)
                {
                    var keep = group.First();
                    Run(c, "UPDATE plans SET name=@p0 WHERE id=@p1", group.Key, keep["id"]);
                    foreach (var duplicate in group.Skip(1)) Run(c, "DELETE FROM plans WHERE id=@p0", duplicate["id"]);
                }
            }
            Run(c, "PRAGMA user_version=7");
        }
        Run(c, "CREATE INDEX IF NOT EXISTS ix_titles_installment_group ON titles(companyId,installmentGroup)");
        if (Query(c, "SELECT id FROM companies").Count == 0)
        {
            Run(c, "INSERT INTO companies(name,cnpj) VALUES('EMPRESA AGUARDANDO ATIVAÇÃO','LICENCA-PENDENTE')");
            Run(c, "INSERT INTO accounts(companyId,name,opening) VALUES(1,'CAIXA',0)");
        }
        var currentVersion = Convert.ToInt32(Query(c, "PRAGMA user_version")[0]["user_version"]);
        if (currentVersion < 8)
        {
            foreach (var tableColumn in new[] { ("companies", "name"), ("parties", "name"), ("accounts", "name"), ("plans", "name"), ("titles", "description"), ("titles", "category"), ("titles", "notes"), ("settlements", "reason") })
                foreach (var row in Query(c, $"SELECT id,{tableColumn.Item2} AS value FROM {tableColumn.Item1}"))
                    Run(c, $"UPDATE {tableColumn.Item1} SET {tableColumn.Item2}=@p0 WHERE id=@p1", ((string)row["value"]!).ToUpperInvariant(), row["id"]);
            Run(c, "PRAGMA user_version=8");
        }
        if (!Query(c, "PRAGMA table_info(titles)").Any(r => (string)r["name"]! == "documentNumber"))
        {
            if (Query(c, "SELECT id FROM titles LIMIT 1").Count > 0) Backup();
            Run(c, "ALTER TABLE titles ADD COLUMN documentNumber TEXT NOT NULL DEFAULT ''; ALTER TABLE titles ADD COLUMN issueDate TEXT NOT NULL DEFAULT ''; ALTER TABLE titles ADD COLUMN branch TEXT NOT NULL DEFAULT ''; ALTER TABLE titles ADD COLUMN costCenter TEXT NOT NULL DEFAULT ''; PRAGMA user_version=9");
        }
        currentVersion = Convert.ToInt32(Query(c, "PRAGMA user_version")[0]["user_version"]);
        if (currentVersion < 10)
        {
            if (Query(c, "SELECT id FROM companies LIMIT 1").Count > 0) Backup();
            Run(c, """
                CREATE TABLE IF NOT EXISTS users(
                    id INTEGER PRIMARY KEY,name TEXT NOT NULL,username TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    pinSalt TEXT NOT NULL,pinHash TEXT NOT NULL,globalAdmin INTEGER NOT NULL DEFAULT 0,
                    active INTEGER NOT NULL DEFAULT 1,failedAttempts INTEGER NOT NULL DEFAULT 0,lockedUntil TEXT NOT NULL DEFAULT '',
                    created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                CREATE TABLE IF NOT EXISTS user_companies(
                    userId INTEGER NOT NULL REFERENCES users(id),companyId INTEGER NOT NULL REFERENCES companies(id),
                    role TEXT NOT NULL CHECK(role IN ('admin','user')),active INTEGER NOT NULL DEFAULT 1,
                    PRIMARY KEY(userId,companyId));
                CREATE TABLE IF NOT EXISTS access_audit(
                    id INTEGER PRIMARY KEY,actorUserId INTEGER REFERENCES users(id),companyId INTEGER REFERENCES companies(id),
                    action TEXT NOT NULL,detail TEXT NOT NULL,created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                CREATE INDEX IF NOT EXISTS ix_user_companies_company ON user_companies(companyId,active);
                PRAGMA user_version=10;
                """);
        }
        currentVersion = Convert.ToInt32(Query(c, "PRAGMA user_version")[0]["user_version"]);
        if (currentVersion < 11)
        {
            if (Query(c, "SELECT id FROM companies LIMIT 1").Count > 0) Backup();
            Run(c, """
                CREATE TABLE IF NOT EXISTS payment_methods(
                    id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),name TEXT NOT NULL,
                    kind TEXT NOT NULL DEFAULT 'both' CHECK(kind IN ('both','payable','receivable')),
                    UNIQUE(companyId,name));
                CREATE TABLE IF NOT EXISTS cost_centers(
                    id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),name TEXT NOT NULL,
                    UNIQUE(companyId,name));
                CREATE TABLE IF NOT EXISTS attachments(
                    id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),
                    titleId INTEGER REFERENCES titles(id),settlementId INTEGER REFERENCES settlements(id),
                    name TEXT NOT NULL,mimeType TEXT NOT NULL,data BLOB NOT NULL,
                    created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                CREATE INDEX IF NOT EXISTS ix_attachments_title ON attachments(companyId,titleId);
                CREATE INDEX IF NOT EXISTS ix_attachments_settlement ON attachments(companyId,settlementId);
                PRAGMA user_version=11;
                """);
        }
        if (!Query(c, "PRAGMA table_info(parties)").Any(r => (string)r["name"]! == "personType"))
            Run(c, "ALTER TABLE parties ADD COLUMN personType TEXT NOT NULL DEFAULT ''");
        if (!Query(c, "PRAGMA table_info(accounts)").Any(r => (string)r["name"]! == "type"))
            Run(c, "ALTER TABLE accounts ADD COLUMN type TEXT NOT NULL DEFAULT 'cash'");
        if (!Query(c, "PRAGMA table_info(titles)").Any(r => (string)r["name"]! == "paymentMethodId"))
            Run(c, "ALTER TABLE titles ADD COLUMN paymentMethodId INTEGER REFERENCES payment_methods(id)");
        if (!Query(c, "PRAGMA table_info(settlements)").Any(r => (string)r["name"]! == "additional"))
            Run(c, "ALTER TABLE settlements ADD COLUMN additional INTEGER NOT NULL DEFAULT 0");
        if (!Query(c, "PRAGMA table_info(user_companies)").Any(r => (string)r["name"]! == "canManagePlanRates"))
        {
            if (Query(c, "SELECT id FROM companies LIMIT 1").Count > 0) Backup();
            Run(c, "ALTER TABLE user_companies ADD COLUMN canManagePlanRates INTEGER NOT NULL DEFAULT 0; PRAGMA user_version=12");
        }
        if (!Query(c, "PRAGMA table_info(settlements)").Any(r => (string)r["name"]! == "paymentMethodId"))
            Run(c, "ALTER TABLE settlements ADD COLUMN paymentMethodId INTEGER REFERENCES payment_methods(id)");
        currentVersion = Convert.ToInt32(Query(c, "PRAGMA user_version")[0]["user_version"]);
        if (currentVersion < 13)
        {
            if (Query(c, "SELECT id FROM companies LIMIT 1").Count > 0) Backup();
            Run(c, """
                CREATE TABLE IF NOT EXISTS ofx_imports(
                    id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),
                    accountId INTEGER NOT NULL REFERENCES accounts(id),fileName TEXT NOT NULL,
                    fingerprint TEXT NOT NULL,bankId TEXT NOT NULL DEFAULT '',periodStart TEXT NOT NULL DEFAULT '',
                    periodEnd TEXT NOT NULL DEFAULT '',created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(companyId,accountId,fingerprint));
                CREATE TABLE IF NOT EXISTS ofx_transactions(
                    id INTEGER PRIMARY KEY,importId INTEGER NOT NULL REFERENCES ofx_imports(id),
                    companyId INTEGER NOT NULL REFERENCES companies(id),accountId INTEGER NOT NULL REFERENCES accounts(id),
                    fitId TEXT NOT NULL,date TEXT NOT NULL,amount INTEGER NOT NULL,memo TEXT NOT NULL,
                    transactionType TEXT NOT NULL DEFAULT '',settlementId INTEGER REFERENCES settlements(id),
                    UNIQUE(companyId,accountId,fitId));
                CREATE INDEX IF NOT EXISTS ix_ofx_transactions_import ON ofx_transactions(importId,id);
                CREATE INDEX IF NOT EXISTS ix_ofx_transactions_settlement ON ofx_transactions(settlementId);
                PRAGMA user_version=13;
                """);
        }
        currentVersion = Convert.ToInt32(Query(c, "PRAGMA user_version")[0]["user_version"]);
        if (currentVersion < 14)
        {
            if (Query(c, "SELECT id FROM titles LIMIT 1").Count > 0) Backup();
            if (!Query(c, "PRAGMA table_info(plans)").Any(row => (string)row["name"]! == "kind"))
            {
                var legacyPlans = Query(c, "SELECT * FROM plans ORDER BY id");
                Run(c, """
                    ALTER TABLE plans RENAME TO plans_legacy;
                    CREATE TABLE plans(
                        id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),
                        name TEXT NOT NULL,kind TEXT NOT NULL CHECK(kind IN ('payable','receivable')),
                        interestRate REAL NOT NULL DEFAULT 0,interestMode TEXT NOT NULL DEFAULT 'percentage' CHECK(interestMode IN ('percentage','value')),interestValue INTEGER NOT NULL DEFAULT 0,
                        penaltyRate REAL NOT NULL DEFAULT 0,penaltyMode TEXT NOT NULL DEFAULT 'percentage' CHECK(penaltyMode IN ('percentage','value')),penaltyValue INTEGER NOT NULL DEFAULT 0,
                        discountRate REAL NOT NULL DEFAULT 0,discountMode TEXT NOT NULL DEFAULT 'percentage' CHECK(discountMode IN ('percentage','value')),discountValue INTEGER NOT NULL DEFAULT 0,
                        UNIQUE(companyId,kind,name));
                    """);
                foreach (var plan in legacyPlans)
                {
                    var companyId = Convert.ToInt32(Number(plan, "companyId")); var name = (string)plan["name"]!;
                    var usedKinds = Query(c, "SELECT DISTINCT kind FROM titles WHERE companyId=@p0 AND upper(category)=upper(@p1)", companyId, name).Select(row => (string)row["kind"]!).ToList();
                    if (usedKinds.Count == 0) usedKinds.Add(ReceivablePlanName(name) ? "receivable" : "payable");
                    foreach (var kind in usedKinds.Distinct()) Run(c, "INSERT INTO plans(companyId,name,kind,interestRate,penaltyRate,discountRate) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)", companyId, name, kind, plan["interestRate"], plan["penaltyRate"], plan["discountRate"]);
                }
                Run(c, "DROP TABLE plans_legacy");
            }
            if (!Query(c, "PRAGMA table_info(titles)").Any(row => (string)row["name"]! == "interestMode")) Run(c, """
                ALTER TABLE titles ADD COLUMN interestMode TEXT NOT NULL DEFAULT 'percentage';
                ALTER TABLE titles ADD COLUMN interestValue INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE titles ADD COLUMN penaltyMode TEXT NOT NULL DEFAULT 'percentage';
                ALTER TABLE titles ADD COLUMN penaltyValue INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE titles ADD COLUMN discountMode TEXT NOT NULL DEFAULT 'percentage';
                ALTER TABLE titles ADD COLUMN discountValue INTEGER NOT NULL DEFAULT 0;
                """);
            Run(c, """
                UPDATE plans SET discountRate=interestRate,interestRate=0
                WHERE kind='receivable' AND (upper(name) LIKE '%CARTÃO%' OR upper(name) LIKE '%CARTAO%') AND interestRate>0 AND discountRate=0;
                UPDATE titles SET discountRate=interestRate,interestRate=0
                WHERE kind='receivable' AND (upper(category) LIKE '%CARTÃO%' OR upper(category) LIKE '%CARTAO%') AND interestRate>0 AND discountRate=0;
                PRAGMA user_version=14;
                """);
        }
        currentVersion = Convert.ToInt32(Query(c, "PRAGMA user_version")[0]["user_version"]);
        if (currentVersion < 15)
        {
            if (Query(c, "SELECT id FROM companies LIMIT 1").Count > 0) Backup();
            Run(c, """
                CREATE TABLE IF NOT EXISTS credit_cards(
                    id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),
                    name TEXT NOT NULL,closingDay INTEGER NOT NULL CHECK(closingDay BETWEEN 1 AND 31),
                    dueDay INTEGER NOT NULL CHECK(dueDay BETWEEN 1 AND 31),
                    accountId INTEGER REFERENCES accounts(id),notes TEXT NOT NULL DEFAULT '',active INTEGER NOT NULL DEFAULT 1,
                    created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,UNIQUE(companyId,name));
                CREATE TABLE IF NOT EXISTS credit_card_invoices(
                    id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),
                    cardId INTEGER NOT NULL REFERENCES credit_cards(id),referenceMonth TEXT NOT NULL,
                    closingDate TEXT NOT NULL,dueDate TEXT NOT NULL,titleId INTEGER NOT NULL UNIQUE REFERENCES titles(id),
                    created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,UNIQUE(cardId,referenceMonth));
                CREATE TABLE IF NOT EXISTS credit_card_purchases(
                    id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),
                    cardId INTEGER NOT NULL REFERENCES credit_cards(id),invoiceId INTEGER NOT NULL REFERENCES credit_card_invoices(id),
                    purchaseGroup TEXT NOT NULL,description TEXT NOT NULL,category TEXT NOT NULL,notes TEXT NOT NULL DEFAULT '',
                    amount INTEGER NOT NULL CHECK(amount>0),purchaseDate TEXT NOT NULL,
                    installmentNumber INTEGER NOT NULL DEFAULT 1,installmentCount INTEGER NOT NULL DEFAULT 1,
                    created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                CREATE INDEX IF NOT EXISTS ix_credit_card_invoices_company ON credit_card_invoices(companyId,dueDate);
                CREATE INDEX IF NOT EXISTS ix_credit_card_purchases_invoice ON credit_card_purchases(invoiceId,id);
                CREATE INDEX IF NOT EXISTS ix_credit_card_purchases_group ON credit_card_purchases(companyId,purchaseGroup);
                PRAGMA user_version=15;
                """);
        }
        currentVersion = Convert.ToInt32(Query(c, "PRAGMA user_version")[0]["user_version"]);
        if (currentVersion < 16)
        {
            if (Query(c, "SELECT id FROM companies LIMIT 1").Count > 0) Backup();
            Run(c, """
                CREATE TABLE IF NOT EXISTS credit_card_recurring_charges(
                    id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),
                    cardId INTEGER NOT NULL REFERENCES credit_cards(id),description TEXT NOT NULL,
                    amount INTEGER NOT NULL CHECK(amount>0),startDate TEXT NOT NULL,category TEXT NOT NULL,
                    notes TEXT NOT NULL DEFAULT '',active INTEGER NOT NULL DEFAULT 1,endedDate TEXT NOT NULL DEFAULT '',
                    created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                CREATE TABLE IF NOT EXISTS credit_card_recurring_skips(
                    id INTEGER PRIMARY KEY,recurringChargeId INTEGER NOT NULL REFERENCES credit_card_recurring_charges(id),
                    referenceMonth TEXT NOT NULL,reason TEXT NOT NULL DEFAULT '',created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(recurringChargeId,referenceMonth));
                ALTER TABLE credit_card_purchases ADD COLUMN recurringChargeId INTEGER REFERENCES credit_card_recurring_charges(id);
                CREATE INDEX IF NOT EXISTS ix_credit_card_recurring_company ON credit_card_recurring_charges(companyId,active);
                CREATE INDEX IF NOT EXISTS ix_credit_card_purchases_recurring ON credit_card_purchases(recurringChargeId,invoiceId);
                PRAGMA user_version=16;
                """);
        }
        currentVersion = Convert.ToInt32(Query(c, "PRAGMA user_version")[0]["user_version"]);
        if (currentVersion < 17)
        {
            if (Query(c, "SELECT id FROM companies LIMIT 1").Count > 0) Backup();
            Run(c, "ALTER TABLE credit_cards ADD COLUMN lastFour TEXT NOT NULL DEFAULT ''; PRAGMA user_version=17");
        }
        currentVersion = Convert.ToInt32(Query(c, "PRAGMA user_version")[0]["user_version"]);
        if (currentVersion < 18)
        {
            if (Query(c, "SELECT id FROM companies LIMIT 1").Count > 0) Backup();
            Run(c, """
                ALTER TABLE credit_cards ADD COLUMN startDate TEXT NOT NULL DEFAULT '';
                UPDATE credit_cards SET startDate=substr(created,1,10) WHERE startDate='';

                PRAGMA foreign_keys=OFF;
                BEGIN IMMEDIATE;
                CREATE TABLE credit_card_invoices_v18(
                    id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),
                    cardId INTEGER NOT NULL REFERENCES credit_cards(id),referenceMonth TEXT NOT NULL,
                    closingDate TEXT NOT NULL,dueDate TEXT NOT NULL,titleId INTEGER UNIQUE REFERENCES titles(id),
                    closedAt TEXT NOT NULL DEFAULT '',created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(cardId,referenceMonth));
                INSERT INTO credit_card_invoices_v18(id,companyId,cardId,referenceMonth,closingDate,dueDate,titleId,closedAt,created)
                SELECT i.id,i.companyId,i.cardId,i.referenceMonth,i.closingDate,i.dueDate,i.titleId,
                    CASE WHEN i.closingDate<date('now')
                           OR EXISTS(SELECT 1 FROM settlements s WHERE s.titleId=i.titleId AND s.reversed=0)
                           OR EXISTS(SELECT 1 FROM titles t WHERE t.id=i.titleId AND t.cancelled=1)
                         THEN i.closingDate ELSE '' END,
                    i.created
                FROM credit_card_invoices i;
                DROP TABLE credit_card_invoices;
                ALTER TABLE credit_card_invoices_v18 RENAME TO credit_card_invoices;
                CREATE INDEX ix_credit_card_invoices_company ON credit_card_invoices(companyId,dueDate);
                UPDATE titles SET deleted=1
                WHERE id IN (SELECT titleId FROM credit_card_invoices WHERE closedAt='' AND titleId IS NOT NULL);
                COMMIT;
                PRAGMA foreign_keys=ON;
                PRAGMA user_version=18;
                """);
        }
        currentVersion = Convert.ToInt32(Query(c, "PRAGMA user_version")[0]["user_version"]);
        if (currentVersion < 19)
        {
            if (Query(c, "SELECT id FROM companies LIMIT 1").Count > 0) Backup();
            Run(c, "PRAGMA user_version=19");
        }
        currentVersion = Convert.ToInt32(Query(c, "PRAGMA user_version")[0]["user_version"]);
        if (currentVersion < 20)
        {
            if (Query(c, "SELECT id FROM companies LIMIT 1").Count > 0) Backup();
            Run(c, "ALTER TABLE ofx_imports ADD COLUMN bankBalance INTEGER; ALTER TABLE ofx_imports ADD COLUMN balanceDate TEXT NOT NULL DEFAULT ''; PRAGMA user_version=20");
        }
        foreach (var company in Query(c, "SELECT id FROM companies")) { var companyId = Convert.ToInt32(company["id"]); SeedPlans(c, companyId); SeedAuxiliaries(c, companyId); }
    }
    SqliteConnection Open() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString()); c.Open(); return c; }
    static SqliteCommand Cmd(SqliteConnection c, string sql, params object?[] values) { var cmd = c.CreateCommand(); cmd.CommandText = sql; for (int i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("@p" + i, values[i] ?? DBNull.Value); return cmd; }
    static void Run(SqliteConnection c, string sql, params object?[] values) { using var cmd = Cmd(c, sql, values); cmd.ExecuteNonQuery(); }
    static List<Dictionary<string, object?>> Query(SqliteConnection c, string sql, params object?[] values) { using var cmd = Cmd(c, sql, values); using var r = cmd.ExecuteReader(); var rows = new List<Dictionary<string, object?>>(); while (r.Read()) { var row = new Dictionary<string, object?>(); for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i); rows.Add(row); } return rows; }
    public object Read(string sql) { lock (gate) { using var c = Open(); return Query(c, sql); } }
    static long Number(Dictionary<string, object?> row, string key) => Convert.ToInt64(row[key]);
    static string Required(string? value, string field) { if (string.IsNullOrWhiteSpace(value) || value.Length > 500) throw new ArgumentException(field + ": informe um valor válido (até 500 caracteres)."); return value.Trim().ToUpperInvariant(); }
    static string RequiredOrEmpty(string? value) { if ((value ?? "").Length > 500) throw new ArgumentException("Texto acima do limite de 500 caracteres."); return (value ?? "").Trim().ToUpperInvariant(); }
    static void Money(long amount, bool positive = false) { if (amount < 0 || (positive && amount == 0) || amount > 999999999999) throw new ArgumentException("Valor fora do limite permitido."); }
    static DateOnly Date(string value) { if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new ArgumentException("Data inválida."); return date; }
    static void Existing(SqliteConnection c, string table, int id, int company) { if (Query(c, $"SELECT id FROM {table} WHERE id=@p0 AND companyId=@p1", id, company).Count == 0) throw new ArgumentException("Registro não pertence à empresa selecionada."); }
    static void Company(SqliteConnection c, int id) { if (Query(c, "SELECT id FROM companies WHERE id=@p0", id).Count == 0) throw new ArgumentException("Empresa não encontrada."); }
    static string LinkedTitles(List<Dictionary<string, object?>> rows) => string.Join(", ", rows.Select(row => $"\"{row["description"]}\" (vencimento {Date((string)row["due"]!).ToString("dd/MM/yyyy")})"));
    static string LinkedMovements(List<Dictionary<string, object?>> rows) => string.Join(", ", rows.Select(row => $"\"{row["description"]}\" ({Date((string)row["date"]!).ToString("dd/MM/yyyy")})"));
    static void Audit(SqliteConnection c, int company, string action, object detail) => Run(c, "INSERT INTO audit(companyId,action,detail) VALUES(@p0,@p1,@p2)", company, action, JsonSerializer.Serialize(detail));
    static bool ReceivablePlanName(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper.Contains("RECEITA") || upper.Contains("VENDA") || upper.Contains("CARTÃO") || upper.Contains("CARTAO") || upper.Contains("VALE") || upper.Contains("CONVÊNIO");
    }
    static string PlanKind(string? value) { var kind = (value ?? "payable").Trim().ToLowerInvariant(); if (kind is not ("payable" or "receivable")) throw new ArgumentException("Selecione se o plano pertence ao contas a pagar ou ao contas a receber."); return kind; }
    static string AdjustmentMode(string? value) { var mode = (value ?? "percentage").Trim().ToLowerInvariant(); if (mode is not ("percentage" or "value")) throw new ArgumentException("Selecione percentual ou valor fixo."); return mode; }
    static void ValidateAdjustment(string mode, decimal rate, long value, string label) { if (mode == "percentage" && (rate < 0 || rate > 100)) throw new ArgumentException($"{label}: o percentual deve ficar entre 0 e 100."); Money(value); }
    static void SeedPlans(SqliteConnection c, int company)
    {
        foreach (var name in new[] { "DESPESAS GERAIS", "FORNECEDORES", "ALUGUEL E OCUPAÇÃO", "IMPOSTOS E TAXAS", "FOLHA DE PAGAMENTO", "SERVIÇOS CONTRATADOS" })
            if (Query(c, "SELECT id FROM plans WHERE companyId=@p0 AND kind='payable' AND upper(name)=upper(@p1)", company, name).Count == 0) Run(c, "INSERT INTO plans(companyId,name,kind) VALUES(@p0,@p1,'payable')", company, name);
        foreach (var name in new[] { "RECEITAS DE VENDAS", "RECEITAS DE SERVIÇOS", "CARTÃO DE DÉBITO", "CARTÃO DE CRÉDITO" })
            if (Query(c, "SELECT id FROM plans WHERE companyId=@p0 AND kind='receivable' AND upper(name)=upper(@p1)", company, name).Count == 0) Run(c, "INSERT INTO plans(companyId,name,kind) VALUES(@p0,@p1,'receivable')", company, name);
    }
    static void SeedAuxiliaries(SqliteConnection c, int company)
    {
        foreach (var item in new[] { ("DINHEIRO", "both"), ("PIX", "both"), ("BOLETO", "both"), ("TRANSFERÊNCIA BANCÁRIA", "both"), ("CARTÃO DE DÉBITO", "receivable"), ("CARTÃO DE CRÉDITO", "receivable") })
            if (Query(c, "SELECT id FROM payment_methods WHERE companyId=@p0 AND upper(name)=upper(@p1)", company, item.Item1).Count == 0) Run(c, "INSERT INTO payment_methods(companyId,name,kind) VALUES(@p0,@p1,@p2)", company, item.Item1, item.Item2);
        foreach (var name in new[] { "ADMINISTRAÇÃO", "ESTOQUE", "AÇOUGUE", "HORTIFRÚTI", "PADARIA", "FRENTE DE CAIXA", "MANUTENÇÃO" })
            if (Query(c, "SELECT id FROM cost_centers WHERE companyId=@p0 AND upper(name)=upper(@p1)", company, name).Count == 0) Run(c, "INSERT INTO cost_centers(companyId,name) VALUES(@p0,@p1)", company, name);
    }
    object Write(Func<SqliteConnection, object> operation) { lock (gate) { using var c = Open(); using var tx = c.BeginTransaction(); var result = operation(c); tx.Commit(); return result; } }
    public bool HasUsers() { lock (gate) { using var c = Open(); return Query(c, "SELECT id FROM users LIMIT 1").Count > 0; } }
    public AuthIdentity LocalAdministrator() { lock (gate) { using var c = Open(); var rows = Query(c, "SELECT * FROM users WHERE username='LOCAL' LIMIT 1"); if (rows.Count == 0) { var pin = HashPin(Convert.ToHexString(RandomNumberGenerator.GetBytes(16))); Run(c, "INSERT INTO users(name,username,pinSalt,pinHash,globalAdmin) VALUES('ACESSO LOCAL','LOCAL',@p0,@p1,1)", pin.Salt, pin.Hash); rows = Query(c, "SELECT * FROM users WHERE id=last_insert_rowid()"); } else if (Number(rows[0], "active") == 0 || Number(rows[0], "globalAdmin") == 0) { Run(c, "UPDATE users SET active=1,globalAdmin=1,failedAttempts=0,lockedUntil='' WHERE id=@p0", rows[0]["id"]); rows = Query(c, "SELECT * FROM users WHERE id=@p0", rows[0]["id"]); } return Identity(rows[0]); } }
    public void ApplyLicensedCompany(LicenseSnapshot license)
    {
        if (string.IsNullOrWhiteSpace(license.Company) || string.IsNullOrWhiteSpace(license.Cnpj)) return;
        lock (gate)
        {
            using var c = Open(); using var tx = c.BeginTransaction();
            var companies = Query(c, "SELECT id,name,cnpj FROM companies ORDER BY id");
            if (companies.Count != 1) return;
            var current = companies[0]; var name = (current["name"]?.ToString() ?? "").Trim(); var document = (current["cnpj"]?.ToString() ?? "").Trim();
            var placeholder = string.Equals(name, "EMPRESA DE TESTE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(document, "TESTE-LOCAL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "EMPRESA AGUARDANDO ATIVAÇÃO", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(document, "LICENCA-PENDENTE", StringComparison.OrdinalIgnoreCase);
            if (!placeholder) return;
            var licensedName = Required(license.Company, "Empresa da licença");
            var licensedDocument = new string(license.Cnpj.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (licensedDocument.Length is not (11 or 14)) throw new ArgumentException("O CPF ou CNPJ vinculado à licença é inválido.");
            var id = Convert.ToInt32(Number(current, "id"));
            Run(c, "UPDATE companies SET name=@p0,cnpj=@p1 WHERE id=@p2", licensedName, licensedDocument, id);
            Audit(c, id, "Empresa vinculada à licença", new { name = licensedName, document = licensedDocument });
            tx.Commit();
        }
    }
    static string Username(string? value) { var username = Required(value, "Usuário").Replace(" ", ""); if (username.Length < 3 || username.Length > 40 || !Regex.IsMatch(username, @"^[A-Z0-9._-]+$")) throw new ArgumentException("Use de 3 a 40 caracteres no usuário: letras, números, ponto, traço ou sublinhado."); if (string.Equals(username, "ADMIN", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("O usuário ADMIN é reservado ao Painel Central Monalisa."); return username; }
    static void ValidatePin(string? value) { if (value is null || !Regex.IsMatch(value, @"^\d{6,12}$")) throw new ArgumentException("O PIN deve conter de 6 a 12 números."); }
    static (string Salt, string Hash) HashPin(string pin) { var salt = RandomNumberGenerator.GetBytes(16); var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, 120000, HashAlgorithmName.SHA256, 32); return (Convert.ToBase64String(salt), Convert.ToBase64String(hash)); }
    static bool CheckPin(string pin, string salt, string expected) { try { var hash = Rfc2898DeriveBytes.Pbkdf2(pin, Convert.FromBase64String(salt), 120000, HashAlgorithmName.SHA256, 32); return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(expected)); } catch { return false; } }
    static AuthIdentity Identity(Dictionary<string, object?> row) => new(Convert.ToInt32(row["id"]), (string)row["name"]!, (string)row["username"]!, Number(row, "globalAdmin") == 1);
    public AuthIdentity SetupAdministrator(BootstrapInput x) { lock (gate) { using var c = Open(); using var tx = c.BeginTransaction(); if (Query(c, "SELECT id FROM users LIMIT 1").Count > 0) throw new ArgumentException("O administrador geral já foi configurado."); ValidatePin(x.Pin); var pin = HashPin(x.Pin); Run(c, "INSERT INTO users(name,username,pinSalt,pinHash,globalAdmin) VALUES(@p0,@p1,@p2,@p3,1)", Required(x.Name, "Nome"), Username(x.Username), pin.Salt, pin.Hash); var id = Convert.ToInt32(Number(Query(c, "SELECT last_insert_rowid() AS id")[0], "id")); Run(c, "INSERT INTO access_audit(actorUserId,action,detail) VALUES(@p0,'CRIAÇÃO DO ADMINISTRADOR GERAL',@p1)", id, JsonSerializer.Serialize(new { usuario = Username(x.Username) })); tx.Commit(); return new(id, Required(x.Name, "Nome"), Username(x.Username), true); } }
    public AuthIdentity Authenticate(LoginInput x) { lock (gate) { using var c = Open(); var username = Username(x.Username); var rows = Query(c, "SELECT * FROM users WHERE username=@p0", username); if (rows.Count == 0) throw new ArgumentException("Usuário ou PIN inválido."); var row = rows[0]; if (Number(row, "active") == 0) throw new ArgumentException("Este acesso está bloqueado."); if (DateTime.TryParse((string)row["lockedUntil"]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var locked) && locked > DateTime.UtcNow) throw new ArgumentException("Acesso temporariamente bloqueado. Aguarde alguns minutos."); if (!CheckPin(x.Pin, (string)row["pinSalt"]!, (string)row["pinHash"]!)) { var attempts = Convert.ToInt32(Number(row, "failedAttempts")) + 1; var until = attempts >= 5 ? DateTime.UtcNow.AddMinutes(5).ToString("O") : ""; Run(c, "UPDATE users SET failedAttempts=@p0,lockedUntil=@p1 WHERE id=@p2", attempts >= 5 ? 0 : attempts, until, row["id"]); throw new ArgumentException(attempts >= 5 ? "Muitas tentativas. O acesso foi bloqueado por cinco minutos." : "Usuário ou PIN inválido."); } Run(c, "UPDATE users SET failedAttempts=0,lockedUntil='' WHERE id=@p0", row["id"]); Run(c, "INSERT INTO access_audit(actorUserId,action,detail) VALUES(@p0,'LOGIN',@p1)", row["id"], JsonSerializer.Serialize(new { usuario = username })); return Identity(row); } }
    public AuthIdentity? SessionUser(int id) { lock (gate) { using var c = Open(); var rows = Query(c, "SELECT * FROM users WHERE id=@p0 AND active=1", id); return rows.Count == 0 ? null : Identity(rows[0]); } }
    public bool CanAccessCompany(int userId, int companyId) { lock (gate) { using var c = Open(); return Query(c, "SELECT id FROM users WHERE id=@p0 AND active=1 AND globalAdmin=1", userId).Count > 0 || Query(c, "SELECT userId FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND active=1", userId, companyId).Count > 0; } }
    public bool IsFinancialUser(AuthIdentity actor, int companyId) { lock (gate) { if (actor.GlobalAdmin) return false; using var c = Open(); return Query(c, "SELECT userId FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND role='user' AND active=1", actor.Id, companyId).Count > 0; } }
    public bool HasAdministrativeAccess(AuthIdentity actor) { lock (gate) { if (actor.GlobalAdmin) return true; using var c = Open(); return Query(c, "SELECT userId FROM user_companies WHERE userId=@p0 AND role='admin' AND active=1 LIMIT 1", actor.Id).Count > 0; } }
    public bool CanManagePlanRates(AuthIdentity actor, int companyId) { lock (gate) { if (actor.GlobalAdmin) return true; using var c = Open(); var rows = Query(c, "SELECT role,canManagePlanRates FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND active=1", actor.Id, companyId); return rows.Any(row => string.Equals(row["role"] as string, "admin", StringComparison.Ordinal) || Number(row, "canManagePlanRates") == 1); } }
    public bool CanManageCompanyAccess(AuthIdentity actor, int companyId) { lock (gate) { if (actor.GlobalAdmin) return true; using var c = Open(); return Query(c, "SELECT userId FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND role='admin' AND active=1", actor.Id, companyId).Count > 0; } }
    public object Companies(AuthIdentity? user) { lock (gate) { using var c = Open(); if (user is null || user.GlobalAdmin) return Query(c, "SELECT id,name,cnpj,'global' AS role FROM companies ORDER BY name"); return Query(c, "SELECT c.id,c.name,c.cnpj,uc.role FROM companies c JOIN user_companies uc ON uc.companyId=c.id WHERE uc.userId=@p0 AND uc.active=1 ORDER BY c.name", user.Id); } }
    public object AccessContext(AuthIdentity actor) { lock (gate) { using var c = Open(); var rows = Query(c, "SELECT * FROM users WHERE id=@p0 AND active=1", actor.Id); if (rows.Count == 0) throw new ArgumentException("Acesso não encontrado."); var user = Identity(rows[0]); var companies = user.GlobalAdmin ? Query(c, "SELECT id,name,cnpj,'global' AS role FROM companies ORDER BY name") : Query(c, "SELECT c.id,c.name,c.cnpj,uc.role FROM companies c JOIN user_companies uc ON uc.companyId=c.id WHERE uc.userId=@p0 AND uc.active=1 ORDER BY c.name", actor.Id); return new { id = user.Id, name = user.Name, username = user.Username, globalAdmin = user.GlobalAdmin, companies }; } }
    public object AllUsers(AuthIdentity actor) { lock (gate) { if (!actor.GlobalAdmin) throw new ArgumentException("Somente o administrador geral pode consultar todos os acessos."); using var c = Open(); return Query(c, "SELECT id,name,username,globalAdmin,active FROM users ORDER BY name"); } }
    static void RequireCompanyAdmin(SqliteConnection c, int company, AuthIdentity actor) { Company(c, company); if (actor.GlobalAdmin) return; var rows = Query(c, "SELECT role FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND active=1", actor.Id, company); if (rows.Count == 0 || !string.Equals(rows[0]["role"] as string, "admin", StringComparison.Ordinal)) throw new ArgumentException("Somente um administrador desta empresa pode gerenciar acessos."); }
    public object CompanyUsers(int company, AuthIdentity actor) { lock (gate) { using var c = Open(); RequireCompanyAdmin(c, company, actor); return Query(c, "SELECT u.id,u.name,u.username,u.active AS userActive,uc.role,uc.active,uc.canManagePlanRates FROM users u JOIN user_companies uc ON uc.userId=u.id WHERE uc.companyId=@p0 ORDER BY u.name", company); } }
    public object CreateCompanyUser(int company, UserAccessInput x, AuthIdentity actor) => Write(c => { RequireCompanyAdmin(c, company, actor); var role = x.Role.ToLowerInvariant(); if (role != "admin" && role != "user") throw new ArgumentException("Tipo de acesso inválido."); if (role == "admin" && !actor.GlobalAdmin) throw new ArgumentException("Somente o administrador geral pode criar outro administrador."); ValidatePin(x.Pin); var username = Username(x.Username); if (Query(c, "SELECT id FROM users WHERE username=@p0", username).Count > 0) throw new ArgumentException("Este nome de usuário já está em uso."); var pin = HashPin(x.Pin); Run(c, "INSERT INTO users(name,username,pinSalt,pinHash) VALUES(@p0,@p1,@p2,@p3)", Required(x.Name, "Nome"), username, pin.Salt, pin.Hash); var id = Number(Query(c, "SELECT last_insert_rowid() AS id")[0], "id"); var canRates = role == "user" && x.CanManagePlanRates ? 1 : 0; Run(c, "INSERT INTO user_companies(userId,companyId,role,canManagePlanRates) VALUES(@p0,@p1,@p2,@p3)", id, company, role, canRates); Run(c, "INSERT INTO access_audit(actorUserId,companyId,action,detail) VALUES(@p0,@p1,'CRIAÇÃO DE ACESSO',@p2)", actor.Id, company, JsonSerializer.Serialize(new { id, username, role, canManagePlanRates = canRates == 1 })); return new { id }; });
    public object LinkCompanyUser(int company, int userId, LinkUserInput x, AuthIdentity actor) => Write(c => { if (!actor.GlobalAdmin) throw new ArgumentException("Somente o administrador geral pode vincular uma pessoa a outra empresa."); RequireCompanyAdmin(c, company, actor); var role = x.Role.ToLowerInvariant(); if (role != "admin" && role != "user") throw new ArgumentException("Tipo de acesso inválido."); var user = Query(c, "SELECT id,globalAdmin FROM users WHERE id=@p0 AND active=1", userId); if (user.Count == 0) throw new ArgumentException("Usuário não encontrado."); if (Number(user[0], "globalAdmin") == 1) throw new ArgumentException("O administrador geral já possui acesso a todas as empresas."); if (Query(c, "SELECT userId FROM user_companies WHERE userId=@p0 AND companyId=@p1", userId, company).Count > 0) throw new ArgumentException("Esta pessoa já possui vínculo com a empresa."); var canRates = role == "user" && x.CanManagePlanRates ? 1 : 0; Run(c, "INSERT INTO user_companies(userId,companyId,role,canManagePlanRates) VALUES(@p0,@p1,@p2,@p3)", userId, company, role, canRates); Run(c, "INSERT INTO access_audit(actorUserId,companyId,action,detail) VALUES(@p0,@p1,'VÍNCULO DE ACESSO',@p2)", actor.Id, company, JsonSerializer.Serialize(new { userId, role, canManagePlanRates = canRates == 1 })); return new { ok = true }; });
    public object UpdateCompanyUser(int company, int userId, AccessUpdateInput x, AuthIdentity actor) => Write(c => { RequireCompanyAdmin(c, company, actor); var role = x.Role.ToLowerInvariant(); if (role != "admin" && role != "user") throw new ArgumentException("Tipo de acesso inválido."); if ((role == "admin" || userId == actor.Id) && !actor.GlobalAdmin) throw new ArgumentException("Somente o administrador geral pode alterar este acesso."); if (Query(c, "SELECT userId FROM user_companies WHERE userId=@p0 AND companyId=@p1", userId, company).Count == 0) throw new ArgumentException("Acesso não encontrado nesta empresa."); var canRates = role == "user" && x.CanManagePlanRates ? 1 : 0; Run(c, "UPDATE user_companies SET role=@p0,active=@p1,canManagePlanRates=@p2 WHERE userId=@p3 AND companyId=@p4", role, x.Active ? 1 : 0, canRates, userId, company); Run(c, "INSERT INTO access_audit(actorUserId,companyId,action,detail) VALUES(@p0,@p1,'ALTERAÇÃO DE ACESSO',@p2)", actor.Id, company, JsonSerializer.Serialize(new { userId, role, x.Active, canManagePlanRates = canRates == 1 })); return new { ok = true }; });
    public object CreateCompany(CompanyInput x) => Write(c =>
    {
        var name = Required(x.Name, "Nome"); var cnpj = new string((x.Cnpj ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (cnpj.Length != 14) throw new ArgumentException("Informe o CNPJ com 14 caracteres.");
        Run(c, "INSERT INTO companies(name,cnpj) VALUES(@p0,@p1)", name, cnpj);
        var id = Number(Query(c, "SELECT last_insert_rowid() AS id")[0], "id");
        Run(c, "INSERT INTO accounts(companyId,name,opening,type) VALUES(@p0,'CAIXA',0,'cash')", id); SeedPlans(c, Convert.ToInt32(id)); SeedAuxiliaries(c, Convert.ToInt32(id)); return new { id };
    });
    public object Party(int company, PartyInput x) => Write(c => { Company(c, company); var personType = ValidateParty(x); Run(c, "INSERT INTO parties(companyId,name,document,type,phone,email,personType) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6)", company, Required(x.Name, "Nome"), CleanDocument(x.Document), x.Type, RequiredOrEmpty(x.Phone), RequiredOrEmpty(x.Email), personType); var id = Number(Query(c, "SELECT last_insert_rowid() AS id")[0], "id"); Audit(c, company, "Cadastro de pessoa", x.Name); return new { ok = true, id }; });
    static string CleanDocument(string? value) => new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    static string ValidateParty(PartyInput x)
    {
        if (x.Type != "customer" && x.Type != "supplier" && x.Type != "both") throw new ArgumentException("Tipo de pessoa inválido."); if (!string.IsNullOrWhiteSpace(x.Email) && !x.Email.Contains('@')) throw new ArgumentException("Informe um e-mail válido.");
        var document = CleanDocument(x.Document); var personType = (x.PersonType ?? "").Trim().ToLowerInvariant(); if (personType.Length == 0) personType = document.Length == 11 ? "individual" : document.Length == 14 ? "company" : "";
        if (personType is not ("" or "individual" or "company")) throw new ArgumentException("Selecione pessoa física ou jurídica.");
        if (document.Length > 0 && personType == "individual" && document.Length != 11) throw new ArgumentException("O CPF deve conter 11 números.");
        if (document.Length > 0 && personType == "company" && document.Length != 14) throw new ArgumentException("O CNPJ deve conter 14 caracteres."); return personType;
    }
    public object EditParty(int company, int id, PartyInput x) => Write(c => { Existing(c, "parties", id, company); var personType = ValidateParty(x); Run(c, "UPDATE parties SET name=@p0,document=@p1,type=@p2,phone=@p3,email=@p4,personType=@p5 WHERE id=@p6", Required(x.Name, "Nome"), CleanDocument(x.Document), x.Type, RequiredOrEmpty(x.Phone), RequiredOrEmpty(x.Email), personType, id); Audit(c, company, "Edição de pessoa", new { id, input = x }); return new { ok = true }; });
    public object DeleteParty(int company, int id) => Write(c => { Existing(c, "parties", id, company); var linked = Query(c, "SELECT description,due FROM titles WHERE partyId=@p0 AND deleted=0 ORDER BY due,id LIMIT 5", id); if (linked.Count > 0) throw new ArgumentException("Não foi possível excluir. Este cadastro está vinculado aos lançamentos: " + LinkedTitles(linked) + "."); var name = (string)Query(c, "SELECT name FROM parties WHERE id=@p0", id)[0]["name"]!; Run(c, "UPDATE titles SET partyId=NULL WHERE partyId=@p0 AND deleted=1", id); Run(c, "DELETE FROM parties WHERE id=@p0", id); Audit(c, company, "Exclusão de pessoa", new { id, name }); return new { ok = true }; });
    static string AccountType(string? value) { var type = (value ?? "cash").ToLowerInvariant(); if (type is not ("cash" or "bank" or "wallet")) throw new ArgumentException("Tipo de conta inválido."); return type; }
    public object Account(int company, AccountInput x) => Write(c => { Company(c, company); if (x.Opening < -999999999999 || x.Opening > 999999999999) throw new ArgumentException("Saldo fora do limite."); Run(c, "INSERT INTO accounts(companyId,name,opening,type) VALUES(@p0,@p1,@p2,@p3)", company, Required(x.Name, "Nome"), x.Opening, AccountType(x.Type)); Audit(c, company, "Cadastro de conta", x); return new { ok = true }; });
    public object EditAccount(int company, int id, AccountInput x) => Write(c => { Existing(c, "accounts", id, company); if (x.Opening < -999999999999 || x.Opening > 999999999999) throw new ArgumentException("Saldo fora do limite."); Run(c, "UPDATE accounts SET name=@p0,opening=@p1,type=@p2 WHERE id=@p3", Required(x.Name, "Nome"), x.Opening, AccountType(x.Type), id); Audit(c, company, "Edição de conta", new { id, input = x }); return new { ok = true }; });
    public object DeleteAccount(int company, int id) => Write(c => { Existing(c, "accounts", id, company); var linked = Query(c, "SELECT description,date FROM movements WHERE accountId=@p0 ORDER BY date,id LIMIT 5", id); if (linked.Count > 0) throw new ArgumentException("Não foi possível excluir. Esta conta está vinculada às movimentações: " + LinkedMovements(linked) + "."); if (Query(c, "SELECT id FROM accounts WHERE companyId=@p0", company).Count <= 1) throw new ArgumentException("Mantenha ao menos uma conta financeira para registrar as baixas."); var name = (string)Query(c, "SELECT name FROM accounts WHERE id=@p0", id)[0]["name"]!; Run(c, "DELETE FROM accounts WHERE id=@p0", id); Audit(c, company, "Exclusão de conta", new { id, name }); return new { ok = true }; });
    static (string Kind, string InterestMode, string PenaltyMode, string DiscountMode) ValidatePlan(PlanInput x) { var kind = PlanKind(x.Kind); var interestMode = AdjustmentMode(x.InterestMode); var penaltyMode = AdjustmentMode(x.PenaltyMode); var discountMode = AdjustmentMode(x.DiscountMode); ValidateAdjustment(interestMode, x.InterestRate, x.InterestValue, "Juros"); ValidateAdjustment(penaltyMode, x.PenaltyRate, x.PenaltyValue, "Multa"); ValidateAdjustment(discountMode, x.DiscountRate, x.DiscountValue, kind == "receivable" ? "Taxa ou desconto" : "Desconto"); return (kind, interestMode, penaltyMode, discountMode); }
    public object Plan(int company, PlanInput x) => Write(c => { Company(c, company); var rule = ValidatePlan(x); Run(c, "INSERT INTO plans(companyId,name,kind,interestRate,interestMode,interestValue,penaltyRate,penaltyMode,penaltyValue,discountRate,discountMode,discountValue) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11)", company, Required(x.Name, "Plano de contas"), rule.Kind, x.InterestRate, rule.InterestMode, x.InterestValue, x.PenaltyRate, rule.PenaltyMode, x.PenaltyValue, x.DiscountRate, rule.DiscountMode, x.DiscountValue); Audit(c, company, "Cadastro de plano de contas", x); return new { ok = true }; });
    public object EditPlan(int company, int id, PlanInput x) => Write(c => { Existing(c, "plans", id, company); var rule = ValidatePlan(x); var name = Required(x.Name, "Plano de contas"); var old = Query(c, "SELECT name,kind FROM plans WHERE id=@p0", id)[0]; var oldName = (string)old["name"]!; var oldKind = (string)old["kind"]!; Run(c, "UPDATE plans SET name=@p0,kind=@p1,interestRate=@p2,interestMode=@p3,interestValue=@p4,penaltyRate=@p5,penaltyMode=@p6,penaltyValue=@p7,discountRate=@p8,discountMode=@p9,discountValue=@p10 WHERE id=@p11", name, rule.Kind, x.InterestRate, rule.InterestMode, x.InterestValue, x.PenaltyRate, rule.PenaltyMode, x.PenaltyValue, x.DiscountRate, rule.DiscountMode, x.DiscountValue, id); Run(c, "UPDATE titles SET category=@p0,interestRate=@p1,interestMode=@p2,interestValue=@p3,penaltyRate=@p4,penaltyMode=@p5,penaltyValue=@p6,discountRate=@p7,discountMode=@p8,discountValue=@p9 WHERE companyId=@p10 AND kind=@p11 AND category=@p12 AND deleted=0 AND cancelled=0", name, x.InterestRate, rule.InterestMode, x.InterestValue, x.PenaltyRate, rule.PenaltyMode, x.PenaltyValue, x.DiscountRate, rule.DiscountMode, x.DiscountValue, company, oldKind, oldName); Audit(c, company, "Edição de plano de contas", new { id, oldName, oldKind, input = x }); return new { ok = true }; });
    public object DeletePlan(int company, int id) => Write(c => { Existing(c, "plans", id, company); var plan = Query(c, "SELECT name,kind FROM plans WHERE id=@p0", id)[0]; var name = (string)plan["name"]!; var kind = (string)plan["kind"]!; var linked = Query(c, "SELECT description,due FROM titles WHERE companyId=@p0 AND kind=@p1 AND category=@p2 AND deleted=0 ORDER BY due,id LIMIT 5", company, kind, name); if (linked.Count > 0) throw new ArgumentException("Não foi possível excluir. Este plano de contas está vinculado aos lançamentos: " + LinkedTitles(linked) + "."); Run(c, "DELETE FROM plans WHERE id=@p0", id); Audit(c, company, "Exclusão de plano de contas", new { id, name, kind }); return new { ok = true }; });
    static string MethodKind(string? value) { var kind = (value ?? "both").ToLowerInvariant(); if (kind is not ("both" or "payable" or "receivable")) throw new ArgumentException("Uso da forma de pagamento inválido."); return kind; }
    public object PaymentMethod(int company, NamedKindInput x) => Write(c => { Company(c, company); Run(c, "INSERT INTO payment_methods(companyId,name,kind) VALUES(@p0,@p1,@p2)", company, Required(x.Name, "Forma de pagamento"), MethodKind(x.Kind)); Audit(c, company, "Cadastro de forma de pagamento", x); return new { ok = true }; });
    public object EditPaymentMethod(int company, int id, NamedKindInput x) => Write(c => { Existing(c, "payment_methods", id, company); var name = Required(x.Name, "Forma de pagamento"); Run(c, "UPDATE payment_methods SET name=@p0,kind=@p1 WHERE id=@p2", name, MethodKind(x.Kind), id); Audit(c, company, "Edição de forma de pagamento", new { id, name }); return new { ok = true }; });
    public object DeletePaymentMethod(int company, int id) => Write(c => { Existing(c, "payment_methods", id, company); if (Query(c, "SELECT id FROM titles WHERE paymentMethodId=@p0 AND deleted=0 UNION SELECT id FROM settlements WHERE paymentMethodId=@p0 LIMIT 1", id).Count > 0) throw new ArgumentException("Esta forma está vinculada a lançamentos ou baixas."); var name = (string)Query(c, "SELECT name FROM payment_methods WHERE id=@p0", id)[0]["name"]!; Run(c, "DELETE FROM payment_methods WHERE id=@p0", id); Audit(c, company, "Exclusão de forma de pagamento", new { id, name }); return new { ok = true }; });
    public object CostCenter(int company, NamedInput x) => Write(c => { Company(c, company); var name = Required(x.Name, "Centro de custo"); Run(c, "INSERT INTO cost_centers(companyId,name) VALUES(@p0,@p1)", company, name); Audit(c, company, "Cadastro de centro de custo", new { name }); return new { ok = true }; });
    public object EditCostCenter(int company, int id, NamedInput x) => Write(c => { Existing(c, "cost_centers", id, company); var old = (string)Query(c, "SELECT name FROM cost_centers WHERE id=@p0", id)[0]["name"]!; var name = Required(x.Name, "Centro de custo"); Run(c, "UPDATE cost_centers SET name=@p0 WHERE id=@p1", name, id); Run(c, "UPDATE titles SET costCenter=@p0 WHERE companyId=@p1 AND costCenter=@p2", name, company, old); Audit(c, company, "Edição de centro de custo", new { id, old, name }); return new { ok = true }; });
    public object DeleteCostCenter(int company, int id) => Write(c => { Existing(c, "cost_centers", id, company); var name = (string)Query(c, "SELECT name FROM cost_centers WHERE id=@p0", id)[0]["name"]!; var linked = Query(c, "SELECT description,due FROM titles WHERE companyId=@p0 AND costCenter=@p1 AND deleted=0 LIMIT 5", company, name); if (linked.Count > 0) throw new ArgumentException("Este centro de custo está vinculado aos lançamentos: " + LinkedTitles(linked) + "."); Run(c, "DELETE FROM cost_centers WHERE id=@p0", id); Audit(c, company, "Exclusão de centro de custo", new { id, name }); return new { ok = true }; });
    const string TitlesSql = """
        SELECT t.*,p.name AS partyName,pm.name AS paymentMethodName,COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) AS settled,
        t.amount-COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) AS remaining
        FROM titles t LEFT JOIN parties p ON p.id=t.partyId LEFT JOIN payment_methods pm ON pm.id=t.paymentMethodId WHERE t.companyId=@p0 AND t.deleted=0 ORDER BY t.due,t.id
        """;
    public object State(int company, AuthIdentity? actor = null)
    {
        lock (gate)
        {
            using var c = Open(); Company(c, company);
            EnsureRecurringCharges(c, company);
            CloseCreditCardInvoices(c, company, CreditCardToday());
            var financialUser = actor is not null && !actor.GlobalAdmin && Query(c, "SELECT role FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND active=1", actor.Id, company).Any(row => string.Equals(row["role"] as string, "user", StringComparison.Ordinal));
            return new
            {
                titles = Query(c, TitlesSql, company),
                parties = Query(c, "SELECT * FROM parties WHERE companyId=@p0 ORDER BY name", company),
                accounts = Query(c, "SELECT a.*,a.opening+COALESCE((SELECT SUM(m.amount) FROM movements m WHERE m.accountId=a.id),0) AS balance FROM accounts a WHERE a.companyId=@p0 ORDER BY a.name", company),
                plans = Query(c, "SELECT * FROM plans WHERE companyId=@p0 ORDER BY name", company),
                paymentMethods = Query(c, "SELECT * FROM payment_methods WHERE companyId=@p0 ORDER BY name", company),
                costCenters = Query(c, "SELECT * FROM cost_centers WHERE companyId=@p0 ORDER BY name", company),
                creditCards = Query(c, "SELECT cc.*,a.name AS accountName FROM credit_cards cc LEFT JOIN accounts a ON a.id=cc.accountId WHERE cc.companyId=@p0 AND cc.active=1 ORDER BY cc.name", company),
                creditCardInvoices = Query(c, """
                    SELECT i.*,cc.name AS cardName,cc.lastFour,t.description,
                    COALESCE((SELECT SUM(p.amount) FROM credit_card_purchases p WHERE p.invoiceId=i.id),0) AS amount,
                    COALESCE(t.cancelled,0) AS cancelled,COALESCE(t.deleted,0) AS deleted,
                    COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) AS settled,
                    COALESCE((SELECT SUM(p.amount) FROM credit_card_purchases p WHERE p.invoiceId=i.id),0)-
                        COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) AS remaining
                    FROM credit_card_invoices i JOIN credit_cards cc ON cc.id=i.cardId LEFT JOIN titles t ON t.id=i.titleId
                    WHERE i.companyId=@p0 ORDER BY i.dueDate DESC,i.id DESC
                    """, company),
                creditCardPurchases = Query(c, """
                    SELECT p.*,i.referenceMonth,i.dueDate,cc.name AS cardName
                    FROM credit_card_purchases p JOIN credit_card_invoices i ON i.id=p.invoiceId JOIN credit_cards cc ON cc.id=p.cardId
                    WHERE p.companyId=@p0 ORDER BY i.dueDate DESC,p.purchaseDate DESC,p.id DESC
                    """, company),
                creditCardRecurringCharges = Query(c, """
                    SELECT r.*,cc.name AS cardName
                    FROM credit_card_recurring_charges r JOIN credit_cards cc ON cc.id=r.cardId
                    WHERE r.companyId=@p0 ORDER BY r.active DESC,r.description,r.id
                    """, company),
                attachments = Query(c, "SELECT id,titleId,settlementId,name,mimeType,created,length(data) AS size FROM attachments WHERE companyId=@p0 ORDER BY id DESC", company),
                settlements = Query(c, "SELECT s.*,a.name AS accountName,pm.name AS paymentMethodName FROM settlements s JOIN accounts a ON a.id=s.accountId LEFT JOIN payment_methods pm ON pm.id=s.paymentMethodId WHERE s.companyId=@p0 ORDER BY s.id DESC", company),
                movements = financialUser ? [] : Query(c, "SELECT m.*,a.name AS accountName FROM movements m JOIN accounts a ON a.id=m.accountId WHERE m.companyId=@p0 ORDER BY m.date DESC,m.id DESC", company),
                audit = financialUser ? [] : Query(c, "SELECT * FROM audit WHERE companyId=@p0 ORDER BY id DESC LIMIT 100", company),
                permissions = new { role = financialUser ? "financial" : "administrator", canViewCashFlow = !financialUser, canViewHistory = !financialUser, canManageAccess = !financialUser, canCreateBackup = !financialUser, canManageCostCenters = !financialUser, canManagePlanRates = !financialUser || actor is not null && CanManagePlanRates(actor, company) }
            };
        }
    }
    public object Title(int company, TitleInput x, AuthIdentity? actor = null) => Write(c =>
    {
        Company(c, company); if (x.Kind != "payable" && x.Kind != "receivable") throw new ArgumentException("Tipo inválido."); Money(x.Amount, true); var due = Date(x.Due); var description = Required(x.Description, "Descrição");
        var document = (x.DocumentNumber ?? "").Trim().ToUpperInvariant(); var issue = string.IsNullOrWhiteSpace(x.IssueDate) ? "" : Date(x.IssueDate).ToString("yyyy-MM-dd");
        if (document.Length > 0 && Query(c, "SELECT id FROM titles WHERE companyId=@p0 AND deleted=0 AND documentNumber=@p1 AND partyId IS @p2 LIMIT 1", company, document, x.PartyId).Count > 0) throw new ArgumentException("Este documento já foi lançado para o mesmo fornecedor ou cliente.");
        var planName = (string.IsNullOrWhiteSpace(x.Category) ? (x.Kind == "payable" ? "DESPESAS GERAIS" : "RECEITAS DE SERVIÇOS") : x.Category.Trim()).ToUpperInvariant();
        var plan = Query(c, "SELECT * FROM plans WHERE companyId=@p0 AND kind=@p1 AND lower(name)=lower(@p2)", company, x.Kind, planName).SingleOrDefault();
        if (plan is null) { Run(c, "INSERT INTO plans(companyId,name,kind) VALUES(@p0,@p1,@p2)", company, planName, x.Kind); plan = Query(c, "SELECT * FROM plans WHERE id=last_insert_rowid()")[0]; }
        if (x.Installments < 1 || x.Installments > 120 || x.Amount < x.Installments) throw new ArgumentException("Informe entre 1 e 120 parcelas, com ao menos um centavo por parcela.");
        if (x.InstallmentIntervalDays < 0 || x.InstallmentIntervalDays > 365) throw new ArgumentException("O intervalo deve ficar entre 1 e 365 dias, ou zero para mensal.");
        var custom = x.InstallmentAmounts is not null || x.InstallmentDues is not null;
        if (custom && (x.InstallmentAmounts is null || x.InstallmentDues is null || x.InstallmentAmounts.Length != x.Installments || x.InstallmentDues.Length != x.Installments)) throw new ArgumentException("Complete o valor e o vencimento de todas as parcelas.");
        if (custom && (x.InstallmentAmounts!.Any(v => v <= 0) || x.InstallmentAmounts!.Sum() != x.Amount)) throw new ArgumentException("A soma das parcelas deve ser igual ao valor total.");
        var customDues = custom ? x.InstallmentDues!.Select(Date).ToArray() : Array.Empty<DateOnly>();
        if (x.PartyId.HasValue) Existing(c, "parties", x.PartyId.Value, company);
        if (x.PaymentMethodId.HasValue) { Existing(c, "payment_methods", x.PaymentMethodId.Value, company); var method = Query(c, "SELECT kind FROM payment_methods WHERE id=@p0", x.PaymentMethodId.Value)[0]["kind"] as string; if (method != "both" && method != x.Kind) throw new ArgumentException("A forma selecionada não atende este tipo de lançamento."); }
        var costCenter = RequiredOrEmpty(x.CostCenter); var existingCostCenter = costCenter.Length == 0 || Query(c, "SELECT id FROM cost_centers WHERE companyId=@p0 AND upper(name)=upper(@p1)", company, costCenter).Count > 0; if (!existingCostCenter && actor is not null && IsFinancialUser(actor, company)) throw new ArgumentException("Selecione um centro de custo já cadastrado pelo administrador."); if (!existingCostCenter) Run(c, "INSERT INTO cost_centers(companyId,name) VALUES(@p0,@p1)", company, costCenter);
        var group = x.Installments > 1 ? Guid.NewGuid().ToString("N") : null;
        for (var i = 0; i < x.Installments; i++) Run(c, "INSERT INTO titles(companyId,kind,description,amount,due,partyId,category,notes,installmentGroup,installmentNumber,installmentCount,interestRate,interestMode,interestValue,penaltyRate,penaltyMode,penaltyValue,discountRate,discountMode,discountValue,documentNumber,issueDate,branch,costCenter,paymentMethodId) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17,@p18,@p19,@p20,@p21,@p22,@p23,@p24)", company, x.Kind, description + (x.Installments > 1 ? $" ({i + 1}/{x.Installments})" : ""), custom ? x.InstallmentAmounts![i] : x.Amount / x.Installments + (i < x.Amount % x.Installments ? 1 : 0), (custom ? customDues[i] : x.InstallmentIntervalDays == 7 ? due.AddDays(i * 7) : due.AddMonths(i)).ToString("yyyy-MM-dd"), x.PartyId, planName, (x.Notes ?? "").ToUpperInvariant(), group, i + 1, x.Installments, plan["interestRate"], plan["interestMode"], plan["interestValue"], plan["penaltyRate"], plan["penaltyMode"], plan["penaltyValue"], plan["discountRate"], plan["discountMode"], plan["discountValue"], document, issue, RequiredOrEmpty(x.Branch), costCenter, x.PaymentMethodId);
        Audit(c, company, "Lançamento", x); return new { ok = true };
    });
    long SettleCore(SqliteConnection c, int company, int id, SettlementInput x)
    {
        Existing(c, "titles", id, company); Existing(c, "accounts", x.AccountId, company); var date = Date(x.Date); if (date > DateOnly.FromDateTime(DateTime.Today)) throw new ArgumentException("A baixa não pode ter data futura.");
        Money(x.Principal, true); Money(x.Interest); Money(x.Penalty); Money(x.Discount); Money(x.Additional);
        if (x.PaymentMethodId.HasValue) Existing(c, "payment_methods", x.PaymentMethodId.Value, company);
        var title = Query(c, TitlesSql, company).Single(r => Number(r, "id") == id);
        if (Number(title, "cancelled") != 0 || x.Principal > Number(title, "remaining")) throw new ArgumentException("Valor excede o saldo em aberto ou título está cancelado.");
        if (x.Discount > x.Principal) throw new ArgumentException("Desconto não pode exceder o principal da baixa.");
        var cash = checked(x.Principal + x.Interest + x.Penalty + x.Additional - x.Discount);
        Run(c, "INSERT INTO settlements(companyId,titleId,accountId,principal,interest,penalty,discount,date,additional,paymentMethodId) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9)", company, id, x.AccountId, x.Principal, x.Interest, x.Penalty, x.Discount, x.Date, x.Additional, x.PaymentMethodId);
        var sid = Number(Query(c, "SELECT last_insert_rowid() AS id")[0], "id");
        Run(c, "INSERT INTO movements(companyId,accountId,amount,date,description,settlementId) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)", company, x.AccountId, (string)title["kind"]! == "payable" ? -cash : cash, x.Date, title["description"], sid);
        Audit(c, company, "Baixa", new { titleId = id, settlementId = sid, input = x }); return sid;
    }
    public object Settle(int company, int id, SettlementInput x) => Write(c => new { id = SettleCore(c, company, id, x) });
    public object BulkSettle(int company, BulkSettlementInput x) => Write(c =>
    {
        if (x.Items is null || x.Items.Length < 2 || x.Items.Length > 200) throw new ArgumentException("Selecione de 2 a 200 títulos para a baixa em lote."); if (x.Items.Select(i => i.TitleId).Distinct().Count() != x.Items.Length) throw new ArgumentException("Há títulos repetidos na seleção.");
        Existing(c, "accounts", x.AccountId, company); Date(x.Date); var ids = new List<long>(); long total = 0;
        foreach (var item in x.Items) { var input = new SettlementInput(x.AccountId, item.Principal, item.Interest, item.Penalty, item.Discount, x.Date, item.Additional, x.PaymentMethodId); ids.Add(SettleCore(c, company, item.TitleId, input)); total = checked(total + item.Principal + item.Interest + item.Penalty + item.Additional - item.Discount); }
        Audit(c, company, "Baixa em lote", new { count = ids.Count, total, settlementIds = ids }); return new { ok = true, count = ids.Count, total, settlementIds = ids };
    });
    public object Duplicate(int company, int id, DuplicateInput x) => Write(c =>
    {
        Existing(c, "titles", id, company); EnsureManualTitle(c, id); var t = Query(c, "SELECT * FROM titles WHERE id=@p0 AND deleted=0", id).SingleOrDefault() ?? throw new ArgumentException("Lançamento não encontrado."); var due = string.IsNullOrWhiteSpace(x.Due) ? (string)t["due"]! : Date(x.Due).ToString("yyyy-MM-dd"); var description = Regex.Replace((string)t["description"]!, @" \(\d+/\d+\)$", "") + " - CÓPIA";
        Run(c, "INSERT INTO titles(companyId,kind,description,amount,due,partyId,category,notes,installmentNumber,installmentCount,interestRate,interestMode,interestValue,penaltyRate,penaltyMode,penaltyValue,discountRate,discountMode,discountValue,issueDate,branch,costCenter,paymentMethodId) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,1,1,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17,@p18,@p19,@p20)", company, t["kind"], description, t["amount"], due, t["partyId"], t["category"], t["notes"], t["interestRate"], t["interestMode"], t["interestValue"], t["penaltyRate"], t["penaltyMode"], t["penaltyValue"], t["discountRate"], t["discountMode"], t["discountValue"], t["issueDate"], t["branch"], t["costCenter"], t["paymentMethodId"]); var newId = Number(Query(c, "SELECT last_insert_rowid() AS id")[0], "id"); Audit(c, company, "Duplicação", new { sourceId = id, titleId = newId, due }); return new { ok = true, id = newId };
    });
    public object Reverse(int company, int id, ReasonInput x) => Write(c =>
    {
        Existing(c, "settlements", id, company); var reason = Required(x.Reason, "Motivo"); var s = Query(c, "SELECT * FROM settlements WHERE id=@p0", id)[0];
        if (Number(s, "reversed") != 0) throw new ArgumentException("Esta baixa já foi estornada.");
        Run(c, "UPDATE settlements SET reversed=1,reason=@p0 WHERE id=@p1", reason, id);
        Run(c, "UPDATE ofx_transactions SET settlementId=NULL WHERE settlementId=@p0", id);
        Run(c, "INSERT INTO movements(companyId,accountId,amount,date,description,settlementId) SELECT companyId,accountId,-amount,@p0,'Estorno: '||description,settlementId FROM movements WHERE settlementId=@p1", DateTime.Today.ToString("yyyy-MM-dd"), id);
        Audit(c, company, "Estorno", new { settlementId = id, reason }); return new { ok = true };
    });
    public object Cancel(int company, int id, CancelInput x) => Write(c =>
    {
        if (x.Scope != "single" && x.Scope != "all_open") throw new ArgumentException("Escolha cancelar somente esta parcela ou todas as parcelas em aberto.");
        Existing(c, "titles", id, company); EnsureManualTitle(c, id); var reason = Required(x.Reason, "Motivo"); var all = Query(c, TitlesSql, company); var t = all.Single(r => Number(r, "id") == id);
        if (Number(t, "settled") > 0 || Number(t, "cancelled") != 0) throw new ArgumentException("Estorne as baixas antes de cancelar. Título já cancelado não pode ser cancelado novamente.");
        var group = t["installmentGroup"] as string;
        if (x.Scope == "all_open" && group is null) throw new ArgumentException("Este lançamento não possui parcelas vinculadas.");
        var targets = x.Scope == "all_open" ? all.Where(r => (string?)r["installmentGroup"] == group && Number(r, "cancelled") == 0 && Number(r, "settled") == 0).ToList() : new List<Dictionary<string, object?>> { t };
        foreach (var target in targets)
        {
            var titleId = Number(target, "id"); Run(c, "UPDATE titles SET cancelled=1 WHERE id=@p0", titleId);
            Audit(c, company, "Cancelamento", new { titleId, reason, scope = x.Scope, installmentGroup = group });
        }
        return new { ok = true, cancelledCount = targets.Count };
    });
    public object Edit(int company, int id, EditTitleInput x, AuthIdentity? actor = null) => Write(c =>
    {
        if (x.Scope != "single" && x.Scope != "all_open") throw new ArgumentException("Escolha editar somente esta parcela ou todas as parcelas sem baixa."); Existing(c, "titles", id, company); EnsureManualTitle(c, id);
        Money(x.Amount, true); var due = Date(x.Due); var description = Required(x.Description, "Descrição");
        if (x.PartyId.HasValue) Existing(c, "parties", x.PartyId.Value, company);
        if (x.PaymentMethodId.HasValue) Existing(c, "payment_methods", x.PaymentMethodId.Value, company);
        var costCenter = RequiredOrEmpty(x.CostCenter); var existingCostCenter = costCenter.Length == 0 || Query(c, "SELECT id FROM cost_centers WHERE companyId=@p0 AND upper(name)=upper(@p1)", company, costCenter).Count > 0; if (!existingCostCenter && actor is not null && IsFinancialUser(actor, company)) throw new ArgumentException("Selecione um centro de custo já cadastrado pelo administrador."); if (!existingCostCenter) Run(c, "INSERT INTO cost_centers(companyId,name) VALUES(@p0,@p1)", company, costCenter);
        var all = Query(c, TitlesSql, company); var selected = all.SingleOrDefault(r => Number(r, "id") == id) ?? throw new ArgumentException("Lançamento não encontrado.");
        if (Number(selected, "cancelled") != 0 || Number(selected, "settled") != 0) throw new ArgumentException("Só é possível editar um lançamento sem baixa e que não esteja cancelado.");
        var titleKind = (string)selected["kind"]!; var planName = string.IsNullOrWhiteSpace(x.Category) ? (titleKind == "payable" ? "DESPESAS GERAIS" : "RECEITAS DE SERVIÇOS") : Required(x.Category, "Plano de contas");
        var plan = Query(c, "SELECT * FROM plans WHERE companyId=@p0 AND kind=@p1 AND upper(name)=upper(@p2)", company, titleKind, planName).SingleOrDefault();
        if (plan is null) { Run(c, "INSERT INTO plans(companyId,name,kind) VALUES(@p0,@p1,@p2)", company, planName, titleKind); plan = Query(c, "SELECT * FROM plans WHERE id=last_insert_rowid()")[0]; }
        var group = selected["installmentGroup"] as string;
        if (x.Scope == "all_open" && group is null) throw new ArgumentException("Este lançamento não possui parcelas vinculadas.");
        var targets = x.Scope == "all_open" ? all.Where(r => (string?)r["installmentGroup"] == group && Number(r, "cancelled") == 0 && Number(r, "settled") == 0).ToList() : new() { selected };
        var selectedNumber = Convert.ToInt32(selected["installmentNumber"]);
        foreach (var target in targets)
        {
            var number = Convert.ToInt32(target["installmentNumber"]); var count = Convert.ToInt32(target["installmentCount"]);
            var label = description + (count > 1 ? $" ({number}/{count})" : ""); var targetDue = due.AddMonths(number - selectedNumber).ToString("yyyy-MM-dd");
            Run(c, "UPDATE titles SET description=@p0,amount=@p1,due=@p2,partyId=@p3,category=@p4,notes=@p5,documentNumber=@p6,issueDate=@p7,branch=@p8,costCenter=@p9,paymentMethodId=@p10,interestRate=@p11,interestMode=@p12,interestValue=@p13,penaltyRate=@p14,penaltyMode=@p15,penaltyValue=@p16,discountRate=@p17,discountMode=@p18,discountValue=@p19 WHERE id=@p20", label, x.Amount, targetDue, x.PartyId, planName, (x.Notes ?? "").ToUpperInvariant(), RequiredOrEmpty(x.DocumentNumber), string.IsNullOrWhiteSpace(x.IssueDate) ? "" : Date(x.IssueDate).ToString("yyyy-MM-dd"), RequiredOrEmpty(x.Branch), costCenter, x.PaymentMethodId, plan["interestRate"], plan["interestMode"], plan["interestValue"], plan["penaltyRate"], plan["penaltyMode"], plan["penaltyValue"], plan["discountRate"], plan["discountMode"], plan["discountValue"], Number(target, "id"));
            Audit(c, company, "Edição", new { titleId = Number(target, "id"), description = label, amount = x.Amount, due = targetDue, scope = x.Scope });
        }
        return new { ok = true, editedCount = targets.Count };
    });
    public object Delete(int company, int id, DeleteInput x) => Write(c =>
    {
        if (x.Scope != "single" && x.Scope != "all_open") throw new ArgumentException("Escolha excluir somente esta parcela ou todas as parcelas sem baixa."); Existing(c, "titles", id, company); EnsureManualTitle(c, id);
        var reason = Required(x.Reason, "Motivo"); var all = Query(c, TitlesSql, company); var selected = all.SingleOrDefault(r => Number(r, "id") == id) ?? throw new ArgumentException("Lançamento não encontrado.");
        if (Query(c, "SELECT id FROM settlements WHERE titleId=@p0 AND reversed=0 LIMIT 1", id).Count > 0) throw new ArgumentException("Este lançamento ainda possui uma baixa ativa. Estorne todas as baixas antes de excluir.");
        var group = selected["installmentGroup"] as string;
        if (x.Scope == "all_open" && group is null) throw new ArgumentException("Este lançamento não possui parcelas vinculadas.");
        var targets = x.Scope == "all_open" ? all.Where(r => (string?)r["installmentGroup"] == group && Query(c, "SELECT id FROM settlements WHERE titleId=@p0 AND reversed=0 LIMIT 1", Number(r, "id")).Count == 0).ToList() : new() { selected };
        foreach (var target in targets)
        {
            var titleId = Number(target, "id"); var description = (string)target["description"]!;
            Run(c, "UPDATE titles SET deleted=1 WHERE id=@p0", titleId);
            Audit(c, company, "Exclusão", new { titleId, description, reason, scope = x.Scope, installmentGroup = group });
        }
        return new { ok = true, deletedCount = targets.Count };
    });
    public object Transfer(int company, TransferInput x) => Write(c =>
    {
        Existing(c, "accounts", x.FromId, company); Existing(c, "accounts", x.ToId, company); Money(x.Amount, true); var date = Date(x.Date); if (date > DateOnly.FromDateTime(DateTime.Today)) throw new ArgumentException("Transferência não pode ter data futura."); if (x.FromId == x.ToId) throw new ArgumentException("Selecione contas diferentes.");
        Run(c, "INSERT INTO movements(companyId,accountId,amount,date,description) VALUES(@p0,@p1,@p2,@p3,'Transferência entre contas'),(@p0,@p4,@p5,@p3,'Transferência entre contas')", company, x.FromId, -x.Amount, x.Date, x.ToId, x.Amount); Audit(c, company, "Transferência", x); return new { ok = true };
    });
    public object Adjust(int company, int accountId, AdjustmentInput x) => Write(c =>
    {
        Existing(c, "accounts", accountId, company); if (x.Amount == 0) throw new ArgumentException("Informe um valor diferente de zero."); Money(Math.Abs(x.Amount), true); var date = Date(x.Date); if (date > DateOnly.FromDateTime(DateTime.Today)) throw new ArgumentException("O ajuste não pode ter data futura."); var reason = Required(x.Reason, "Justificativa");
        Run(c, "INSERT INTO movements(companyId,accountId,amount,date,description) VALUES(@p0,@p1,@p2,@p3,@p4)", company, accountId, x.Amount, date.ToString("yyyy-MM-dd"), "AJUSTE DE SALDO: " + reason); Audit(c, company, "Ajuste de saldo", new { accountId, x.Amount, date = x.Date, reason }); return new { ok = true };
    });
    public object AddAttachment(int company, AttachmentInput x) => Write(c =>
    {
        Company(c, company); if ((x.TitleId.HasValue ? 1 : 0) + (x.SettlementId.HasValue ? 1 : 0) != 1) throw new ArgumentException("Vincule o anexo a um lançamento ou a uma baixa."); if (x.TitleId.HasValue) Existing(c, "titles", x.TitleId.Value, company); if (x.SettlementId.HasValue) Existing(c, "settlements", x.SettlementId.Value, company);
        byte[] data; try { data = Convert.FromBase64String(x.DataBase64 ?? ""); } catch (FormatException) { throw new ArgumentException("Arquivo inválido."); }
        if (data.Length == 0 || data.Length > 10 * 1024 * 1024) throw new ArgumentException("O anexo deve ter até 10 MB."); var name = Path.GetFileName(Required(x.Name, "Nome do arquivo")); var mime = string.IsNullOrWhiteSpace(x.MimeType) ? "application/octet-stream" : x.MimeType.Trim().ToLowerInvariant();
        Run(c, "INSERT INTO attachments(companyId,titleId,settlementId,name,mimeType,data) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)", company, x.TitleId, x.SettlementId, name, mime, data); var id = Number(Query(c, "SELECT last_insert_rowid() AS id")[0], "id"); Audit(c, company, "Anexo incluído", new { id, x.TitleId, x.SettlementId, name, size = data.Length }); return new { ok = true, id };
    });
    public AttachmentFile Attachment(int company, int id) { lock (gate) { using var c = Open(); Existing(c, "attachments", id, company); var row = Query(c, "SELECT name,mimeType,data FROM attachments WHERE id=@p0", id)[0]; return new((byte[])row["data"]!, (string)row["mimeType"]!, (string)row["name"]!); } }
    public object DeleteAttachment(int company, int id) => Write(c => { Existing(c, "attachments", id, company); var name = (string)Query(c, "SELECT name FROM attachments WHERE id=@p0", id)[0]["name"]!; Run(c, "DELETE FROM attachments WHERE id=@p0", id); Audit(c, company, "Anexo excluído", new { id, name }); return new { ok = true }; });
    public object ResetSupermarketDemo() => Write(c =>
    {
        Run(c, "DELETE FROM movements; DELETE FROM settlements; DELETE FROM audit; DELETE FROM titles; DELETE FROM parties; DELETE FROM accounts; DELETE FROM plans; DELETE FROM user_companies; DELETE FROM companies;");
        Run(c, "INSERT INTO companies(id,name,cnpj) VALUES(1,'SUPERMERCADO MODELO MONALISA LTDA','12345678000195')");
        Run(c, "INSERT INTO accounts(id,companyId,name,opening) VALUES(1,1,'CAIXA LOJA',350000),(2,1,'BANCO ITAÚ',2500000),(3,1,'RECEBIMENTOS DE CARTÕES',0)");
        void AddPlan(string name, string kind = "payable", decimal interest = 0, decimal penalty = 0, decimal discount = 0) => Run(c, "INSERT INTO plans(companyId,name,kind,interestRate,penaltyRate,discountRate) VALUES(1,@p0,@p1,@p2,@p3,@p4)", name, kind, interest, penalty, discount);
        AddPlan("COMPRA DE MERCADORIAS", interest: .1m, penalty: 2m); AddPlan("HORTIFRÚTI", interest: .1m, penalty: 2m); AddPlan("AÇOUGUE", interest: .1m, penalty: 2m); AddPlan("PADARIA", interest: .1m, penalty: 2m); AddPlan("BEBIDAS", interest: .1m, penalty: 2m); AddPlan("MATERIAL DE LIMPEZA"); AddPlan("ENERGIA ELÉTRICA"); AddPlan("ALUGUEL"); AddPlan("FOLHA DE PAGAMENTO"); AddPlan("IMPOSTOS E TAXAS"); AddPlan("MANUTENÇÃO"); AddPlan("SEGURANÇA"); AddPlan("FRETES"); AddPlan("PERDAS E AVARIAS"); AddPlan("TAXAS DE CARTÃO"); AddPlan("VENDAS EM DINHEIRO", "receivable"); AddPlan("VENDAS POR PIX", "receivable"); AddPlan("CARTÃO DE DÉBITO", "receivable", discount: 1.8m); AddPlan("CARTÃO DE CRÉDITO", "receivable", discount: 3.2m); AddPlan("VALE-ALIMENTAÇÃO", "receivable"); AddPlan("CONVÊNIOS", "receivable");
        long AddParty(string name, string document, string type, string phone, string email) { Run(c, "INSERT INTO parties(companyId,name,document,type,phone,email) VALUES(1,@p0,@p1,@p2,@p3,@p4)", name, document, type, phone, email); return Number(Query(c, "SELECT last_insert_rowid() AS id")[0], "id"); }
        var atacado = AddParty("ATACADO BOM PREÇO LTDA", "11222333000181", "supplier", "11940001001", "FINANCEIRO@BOMPRECO.EXEMPLO");
        var frigorifico = AddParty("FRIGORÍFICO BOI FORTE LTDA", "22333444000162", "supplier", "11940001002", "COBRANCA@BOIFORTE.EXEMPLO");
        var hortifruti = AddParty("HORTIFRÚTI CAMPO VERDE LTDA", "33444555000143", "supplier", "11940001003", "PEDIDOS@CAMPOVERDE.EXEMPLO");
        var energia = AddParty("ENERGIA REGIONAL", "44555666000124", "supplier", "08000001004", "ATENDIMENTO@ENERGIA.EXEMPLO");
        var imovel = AddParty("ADMINISTRADORA PRAÇA CENTRAL LTDA", "55666777000105", "supplier", "11940001005", "ALUGUEL@PRACACENTRAL.EXEMPLO");
        var adquirente = AddParty("OPERADORA DE CARTÕES MODELO", "66777888000196", "customer", "1130001000", "RECEBIVEIS@CARTOES.EXEMPLO");
        var vale = AddParty("VALE ALIMENTAÇÃO BRASIL", "77888999000177", "customer", "1130002000", "REPASSE@VALE.EXEMPLO");
        var convenio = AddParty("RESTAURANTE SABOR CASEIRO LTDA", "88999000000158", "customer", "11940001006", "FINANCEIRO@SABORCASEIRO.EXEMPLO");
        var today = DateOnly.FromDateTime(DateTime.Today); var first = new DateOnly(today.Year, today.Month, 1); var next = first.AddMonths(1);
        long AddTitle(string kind, string description, long amount, DateOnly due, long? party, string plan, string document, DateOnly issue, string branch, string cost, string notes = "", string? group = null, int number = 1, int count = 1)
        {
            Run(c, "INSERT INTO titles(companyId,kind,description,amount,due,partyId,category,notes,installmentGroup,installmentNumber,installmentCount,interestRate,interestMode,interestValue,penaltyRate,penaltyMode,penaltyValue,discountRate,discountMode,discountValue,documentNumber,issueDate,branch,costCenter) SELECT 1,@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,interestRate,interestMode,interestValue,penaltyRate,penaltyMode,penaltyValue,discountRate,discountMode,discountValue,@p10,@p11,@p12,@p13 FROM plans WHERE companyId=1 AND kind=@p0 AND name=@p5", kind, description, amount, due.ToString("yyyy-MM-dd"), party, plan, notes, group, number, count, document, issue.ToString("yyyy-MM-dd"), branch, cost); return Number(Query(c, "SELECT last_insert_rowid() AS id")[0], "id");
        }
        long Pay(long title, long account, long principal, DateOnly date, long interest = 0, long penalty = 0, long discount = 0)
        {
            Run(c, "INSERT INTO settlements(companyId,titleId,accountId,principal,interest,penalty,discount,date) VALUES(1,@p0,@p1,@p2,@p3,@p4,@p5,@p6)", title, account, principal, interest, penalty, discount, date.ToString("yyyy-MM-dd")); var id = Number(Query(c, "SELECT last_insert_rowid() AS id")[0], "id"); var kind = (string)Query(c, "SELECT kind FROM titles WHERE id=@p0", title)[0]["kind"]!; var cash = principal + interest + penalty - discount; Run(c, "INSERT INTO movements(companyId,accountId,amount,date,description,settlementId) SELECT 1,@p0,@p1,@p2,description,@p3 FROM titles WHERE id=@p4", account, kind == "payable" ? -cash : cash, date.ToString("yyyy-MM-dd"), id, title); return id;
        }
        var compra = AddTitle("payable", "COMPRA SEMANAL DE MERCADORIAS", 1245000, today.AddDays(2), atacado, "COMPRA DE MERCADORIAS", "NF 45872", today.AddDays(-10), "LOJA MATRIZ", "ESTOQUE", "ENTREGA CONFERIDA PELO RECEBIMENTO");
        var carnes = AddTitle("payable", "COMPRA DE CARNES E CONGELADOS", 897000, today.AddDays(-3), frigorifico, "AÇOUGUE", "NF 18743", today.AddDays(-12), "LOJA MATRIZ", "AÇOUGUE");
        AddTitle("payable", "FORNECIMENTO DE ENERGIA", 324000, today.AddDays(5), energia, "ENERGIA ELÉTRICA", "FAT 09/2026", first, "LOJA MATRIZ", "ADMINISTRAÇÃO");
        var aluguel = AddTitle("payable", "ALUGUEL DA LOJA", 1500000, first.AddDays(9), imovel, "ALUGUEL", "REC 09/2026", first, "LOJA MATRIZ", "ADMINISTRAÇÃO"); var baixaAluguel = Pay(aluguel, 2, 1500000, first.AddDays(8), discount: 5000);
        var manutencao = AddTitle("payable", "MANUTENÇÃO DA CÂMARA FRIA", 245000, first.AddDays(14), frigorifico, "MANUTENÇÃO", "OS 9021", first.AddDays(2), "LOJA MATRIZ", "MANUTENÇÃO"); var baixaManutencao = Pay(manutencao, 2, 245000, first.AddDays(16), interest: 1000, penalty: 2000, discount: 500);
        var equipmentGroup = Guid.NewGuid().ToString("N"); for (var i = 0; i < 3; i++) AddTitle("payable", $"EQUIPAMENTO PARA PADARIA ({i + 1}/3)", 360000, next.AddMonths(i).AddDays(9), atacado, "PADARIA", $"NF 46010-{i + 1}", today, "LOJA MATRIZ", "PADARIA", "PARCELAMENTO DO NOVO FORNO", equipmentGroup, i + 1, 3);
        AddTitle("payable", "MATERIAL DE LIMPEZA", 128000, next.AddDays(4), atacado, "MATERIAL DE LIMPEZA", "NF 45901", today.AddDays(-1), "LOJA MATRIZ", "ESTOQUE");
        var debit = AddTitle("receivable", "VENDAS NO CARTÃO DE DÉBITO", 1845000, today.AddDays(-1), adquirente, "CARTÃO DE DÉBITO", "LOTE DÉBITO 2309", today.AddDays(-2), "LOJA MATRIZ", "FRENTE DE CAIXA"); var baixaDebito = Pay(debit, 3, 1845000, today.AddDays(-1), discount: 33210);
        AddTitle("receivable", "VENDAS NO CARTÃO DE CRÉDITO", 3290000, next.AddDays(7), adquirente, "CARTÃO DE CRÉDITO", "LOTE CRÉDITO 2309", today, "LOJA MATRIZ", "FRENTE DE CAIXA");
        var pix = AddTitle("receivable", "VENDAS RECEBIDAS POR PIX", 780000, today, null, "VENDAS POR PIX", "MOV PIX 2309", today, "LOJA MATRIZ", "FRENTE DE CAIXA"); var baixaPix = Pay(pix, 2, 780000, today);
        AddTitle("receivable", "REPASSE DE VALE-ALIMENTAÇÃO", 1240000, first.AddMonths(1).AddDays(-2), vale, "VALE-ALIMENTAÇÃO", "LOTE VALE 2309", today, "LOJA MATRIZ", "FRENTE DE CAIXA");
        AddTitle("receivable", "CONVÊNIO DE COMPRAS CORPORATIVAS", 560000, next.AddDays(14), convenio, "CONVÊNIOS", "FAT CONV 0926", today, "LOJA MATRIZ", "ADMINISTRAÇÃO");
        Audit(c, 1, "Lançamento", new { Kind = "payable", Description = "COMPRA SEMANAL DE MERCADORIAS", Amount = 1245000, Due = today.AddDays(2).ToString("yyyy-MM-dd"), titleId = compra });
        Audit(c, 1, "Lançamento", new { Kind = "payable", Description = "COMPRA DE CARNES E CONGELADOS", Amount = 897000, Due = today.AddDays(-3).ToString("yyyy-MM-dd"), titleId = carnes });
        Audit(c, 1, "Baixa", new { titleId = aluguel, settlementId = baixaAluguel, input = new { AccountId = 2, Principal = 1500000, Interest = 0, Penalty = 0, Discount = 5000, Date = first.AddDays(8).ToString("yyyy-MM-dd") } });
        Audit(c, 1, "Baixa", new { titleId = manutencao, settlementId = baixaManutencao, input = new { AccountId = 2, Principal = 245000, Interest = 1000, Penalty = 2000, Discount = 500, Date = first.AddDays(16).ToString("yyyy-MM-dd") } });
        Audit(c, 1, "Baixa", new { titleId = debit, settlementId = baixaDebito, input = new { AccountId = 3, Principal = 1845000, Interest = 0, Penalty = 0, Discount = 33210, Date = today.AddDays(-1).ToString("yyyy-MM-dd") } });
        Audit(c, 1, "Baixa", new { titleId = pix, settlementId = baixaPix, input = new { AccountId = 2, Principal = 780000, Interest = 0, Penalty = 0, Discount = 0, Date = today.ToString("yyyy-MM-dd") } });
        Audit(c, 1, "Base de demonstração", new { segmento = "SUPERMERCADO", geradoEm = DateTime.Now });
        return new { ok = true, companyId = 1 };
    });
    public string Backup() { lock (gate) { var folder = Path.Combine(Path.GetDirectoryName(path)!, "backups"); Directory.CreateDirectory(folder); var target = Path.Combine(folder, $"financeiro-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db"); using var src = Open(); using var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString()); dest.Open(); src.BackupDatabase(dest); return target; } }
    public object PrepareGestoresImport(GestoresImportInput x, AuthIdentity actor) { if (!actor.GlobalAdmin) throw new ArgumentException("Somente o administrador geral pode recuperar um backup do Gestores."); lock (gate) return GestoresBackupImporter.Prepare(path, x); }
    public object CommitGestoresImport(GestoresImportCommitInput x, AuthIdentity actor)
    {
        if (!actor.GlobalAdmin) throw new ArgumentException("Somente o administrador geral pode recuperar um backup do Gestores.");
        lock (gate)
        {
            var prepared = GestoresBackupImporter.PreparedPath(path, x.Token); using (var check = new SqliteConnection($"Data Source={prepared};Mode=ReadOnly")) { check.Open(); if (!string.Equals(Query(check, "PRAGMA integrity_check")[0].Values.FirstOrDefault()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("O backup convertido não passou na verificação de integridade."); foreach (var table in new[] { "companies", "titles", "accounts", "settlements", "movements", "audit", "users" }) if (Query(check, "SELECT name FROM sqlite_master WHERE type='table' AND name=@p0", table).Count == 0) throw new ArgumentException("O backup convertido está incompleto."); }
            var safety = Backup(); SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "-wal", "-shm" }) { var side = path + suffix; if (File.Exists(side)) File.Delete(side); }
            File.Copy(prepared, path, true); using (var restored = Open()) { var companyRow = Query(restored, "SELECT id FROM companies ORDER BY id LIMIT 1").FirstOrDefault(); if (companyRow is not null) Audit(restored, Convert.ToInt32(companyRow["id"]), "Recuperação do Gestores", new { copiaDeSeguranca = safety }); }
            try { File.Delete(prepared); } catch { }
            return new { ok = true, safetyBackup = safety };
        }
    }
    public object Restore(RestoreInput x, AuthIdentity actor)
    {
        lock (gate)
        {
            if (!actor.GlobalAdmin) throw new ArgumentException("Somente o administrador geral pode restaurar um backup completo.");
            byte[] data; try { data = Convert.FromBase64String(x.DataBase64 ?? ""); } catch (FormatException) { throw new ArgumentException("Arquivo de backup inválido."); }
            if (data.Length < 4096 || data.Length > 100 * 1024 * 1024) throw new ArgumentException("O backup deve ter entre 4 KB e 100 MB.");
            var temp = Path.Combine(Path.GetDirectoryName(path)!, $"restore-{Guid.NewGuid():N}.db"); File.WriteAllBytes(temp, data); try
            {
                using (var check = new SqliteConnection($"Data Source={temp};Mode=ReadOnly")) { check.Open(); var integrity = Query(check, "PRAGMA integrity_check"); if (integrity.Count == 0 || !string.Equals(integrity[0].Values.FirstOrDefault()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("O arquivo não passou na verificação de integridade."); foreach (var table in new[] { "companies", "titles", "accounts", "settlements", "movements", "audit", "users" }) if (Query(check, "SELECT name FROM sqlite_master WHERE type='table' AND name=@p0", table).Count == 0) throw new ArgumentException("O arquivo não é um backup compatível do Monalisa Financeiro."); }
                var safety = Backup(); SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "-wal", "-shm" }) { var side = path + suffix; if (File.Exists(side)) File.Delete(side); }
                File.Copy(temp, path, true); using (var restored = Open()) { var companyRow = Query(restored, "SELECT id FROM companies ORDER BY id LIMIT 1").FirstOrDefault(); if (companyRow is not null) Audit(restored, Convert.ToInt32(companyRow["id"]), "Restauração de backup", new { arquivo = Path.GetFileName(x.Name), copiaDeSeguranca = safety }); }
                return new { ok = true, safetyBackup = safety };
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}

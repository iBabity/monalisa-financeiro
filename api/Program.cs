using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("FINANCE_URL") ?? "http://127.0.0.1:5187");
builder.Services.AddSingleton<Ledger>();
builder.Services.AddSingleton<AuthSessions>();
builder.Services.AddSingleton<MasterAccess>();
var app = builder.Build();
app.Use(async (ctx, next) => {
    var origin = ctx.Request.Headers.Origin.ToString();
    var localOrigin=$"{ctx.Request.Scheme}://{ctx.Request.Host}";
    if (origin.Length > 0 && !string.Equals(origin,localOrigin,StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 403; return; }
    try { await next(); }
    catch (ArgumentException ex) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = "Registro duplicado ou referência inválida." }); }
});
app.Use(async (ctx, next) => {
    if(!ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/api/health") ||
       ctx.Request.Path.StartsWithSegments("/api/auth/bootstrap") || ctx.Request.Path.StartsWithSegments("/api/auth/login")) { await next(); return; }
    var db=ctx.RequestServices.GetRequiredService<Ledger>();
    if(Environment.GetEnvironmentVariable("FINANCE_AUTH_DISABLED")=="1") { await next(); return; }
    var needsSetup=!db.HasUsers();
    var sessions=ctx.RequestServices.GetRequiredService<AuthSessions>();
    var header=ctx.Request.Headers.Authorization.ToString();
    var token=header.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)?header[7..].Trim():"";
    if(!sessions.TryGet(token,out var sessionIdentity)) {
        ctx.Response.StatusCode=needsSetup?428:401;await ctx.Response.WriteAsJsonAsync(new{error=needsSetup?"Crie o administrador geral ou entre com o acesso mestre.":"Faça login para continuar."});return;
    }
    var identity=sessionIdentity.Id==0?sessionIdentity:db.SessionUser(sessionIdentity.Id);
    if(identity is null) { ctx.Response.StatusCode=401;await ctx.Response.WriteAsJsonAsync(new{error="Faça login para continuar."});return; }
    ctx.Items["user"]=identity;
    var match=Regex.Match(ctx.Request.Path.Value??"",@"^/api/companies/(\d+)");
    if(match.Success && int.TryParse(match.Groups[1].Value,out var companyId) && !identity.GlobalAdmin && !db.CanAccessCompany(identity.Id,companyId)) {
        ctx.Response.StatusCode=403;await ctx.Response.WriteAsJsonAsync(new{error="Você não possui acesso a esta empresa."});return;
    }
    if(ctx.Request.Method=="POST" && ctx.Request.Path=="/api/companies" && !identity.GlobalAdmin) {
        ctx.Response.StatusCode=403;await ctx.Response.WriteAsJsonAsync(new{error="Somente o administrador geral pode criar empresas."});return;
    }
    if(ctx.Request.Path=="/api/users" && !identity.GlobalAdmin) {
        ctx.Response.StatusCode=403;await ctx.Response.WriteAsJsonAsync(new{error="Somente o administrador geral pode consultar todos os acessos."});return;
    }
    if(match.Success && Regex.IsMatch(ctx.Request.Path.Value??"",@"^/api/companies/\d+/users(?:/|$)") &&
       int.TryParse(match.Groups[1].Value,out var accessCompanyId) && !db.CanManageCompanyAccess(identity,accessCompanyId)) {
        ctx.Response.StatusCode=403;await ctx.Response.WriteAsJsonAsync(new{error="Este perfil não possui acesso à administração da empresa."});return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Context.Response.Headers.Pragma = "no-cache";
        context.Context.Response.Headers.Expires = "0";
    }
});
app.MapGet("/api/health", () => new { status = "ok", mode = "local-test" });
app.MapGet("/api/auth/bootstrap", (Ledger db) => new { needsSetup=!db.HasUsers() });
app.MapPost("/api/auth/bootstrap", (BootstrapInput x, Ledger db, AuthSessions sessions) => {
    var user=db.SetupAdministrator(x);var token=sessions.Create(user);return new {token,user=db.AccessContext(user)};
});
app.MapPost("/api/auth/login", (LoginInput x, Ledger db, AuthSessions sessions, MasterAccess master) => {
    var user=master.IsMasterUsername(x.Username)?master.Authenticate(x.Pin):db.Authenticate(x);
    if(user.Id==0)db.RecordMasterLogin();
    var token=sessions.Create(user);return new {token,user=db.AccessContext(user)};
});
app.MapPost("/api/auth/logout", (HttpContext context, AuthSessions sessions) => {
    var header=context.Request.Headers.Authorization.ToString();if(header.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))sessions.Remove(header[7..].Trim());return new{ok=true};
});
app.MapGet("/api/auth/me", (HttpContext context, Ledger db) => db.AccessContext(CurrentUser(context)));
app.MapGet("/api/users", (HttpContext context, Ledger db) => db.AllUsers(CurrentUser(context)));
app.MapGet("/api/companies", (HttpContext context, Ledger db) => db.Companies(context.Items["user"] as AuthIdentity));
app.MapGet("/api/cnpj/{cnpj}", async (string cnpj) => await LookupCnpj(cnpj));
app.MapPost("/api/companies", (CompanyInput x, Ledger db) => db.CreateCompany(x));
app.MapGet("/api/companies/{companyId:int}/state", (int companyId, HttpContext context, Ledger db) => db.State(companyId,context.Items["user"] as AuthIdentity));
app.MapGet("/api/companies/{companyId:int}/users", (int companyId,HttpContext context,Ledger db) => db.CompanyUsers(companyId,CurrentUser(context)));
app.MapPost("/api/companies/{companyId:int}/users", (int companyId,UserAccessInput x,HttpContext context,Ledger db) => db.CreateCompanyUser(companyId,x,CurrentUser(context)));
app.MapPost("/api/companies/{companyId:int}/users/{userId:int}/link", (int companyId,int userId,LinkUserInput x,HttpContext context,Ledger db) => db.LinkCompanyUser(companyId,userId,x,CurrentUser(context)));
app.MapPost("/api/companies/{companyId:int}/users/{userId:int}/access", (int companyId,int userId,AccessUpdateInput x,HttpContext context,Ledger db) => db.UpdateCompanyUser(companyId,userId,x,CurrentUser(context)));
app.MapGet("/api/companies/{companyId:int}/reports/financial", (int companyId, string start, string end, HttpContext context, Ledger db) => {
    var report=db.FinancialReport(companyId,start,end);
    context.Response.Headers.ContentDisposition=$"inline; filename=\"{report.FileName}\"";
    return Results.File(report.Content,"application/pdf");
});
app.MapGet("/api/companies/{companyId:int}/reports/payables", (int companyId, string month, string type, HttpContext context, Ledger db) => {
    var report=db.PayableReport(companyId,month,type);
    context.Response.Headers.ContentDisposition=$"inline; filename=\"{report.FileName}\"";
    return Results.File(report.Content,"application/pdf");
});
app.MapGet("/api/companies/{companyId:int}/reports/receivables", (int companyId, string month, string type, HttpContext context, Ledger db) => {
    var report=db.ReceivableReport(companyId,month,type);
    context.Response.Headers.ContentDisposition=$"inline; filename=\"{report.FileName}\"";
    return Results.File(report.Content,"application/pdf");
});
app.MapPost("/api/companies/{companyId:int}/parties", (int companyId, PartyInput x, Ledger db) => db.Party(companyId,x));
app.MapPost("/api/companies/{companyId:int}/parties/{id:int}/edit", (int companyId,int id,PartyInput x,Ledger db) => db.EditParty(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/parties/{id:int}/delete", (int companyId,int id,Ledger db) => db.DeleteParty(companyId,id));
app.MapPost("/api/companies/{companyId:int}/accounts", (int companyId, AccountInput x, Ledger db) => db.Account(companyId,x));
app.MapPost("/api/companies/{companyId:int}/accounts/{id:int}/delete", (int companyId,int id,Ledger db) => db.DeleteAccount(companyId,id));
app.MapPost("/api/companies/{companyId:int}/plans", (int companyId, PlanInput x, Ledger db) => db.Plan(companyId,x));
app.MapPost("/api/companies/{companyId:int}/plans/{id:int}/edit", (int companyId,int id,PlanInput x,Ledger db) => db.EditPlan(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/plans/{id:int}/delete", (int companyId,int id,Ledger db) => db.DeletePlan(companyId,id));
app.MapPost("/api/companies/{companyId:int}/titles", (int companyId, TitleInput x, Ledger db) => db.Title(companyId,x));
app.MapPost("/api/companies/{companyId:int}/titles/{id:int}/settle", (int companyId, int id, SettlementInput x, Ledger db) => db.Settle(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/settlements/{id:int}/reverse", (int companyId, int id, ReasonInput x, Ledger db) => db.Reverse(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/titles/{id:int}/cancel", (int companyId,int id,CancelInput x,Ledger db) => db.Cancel(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/titles/{id:int}/edit", (int companyId,int id,EditTitleInput x,Ledger db) => db.Edit(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/titles/{id:int}/delete", (int companyId,int id,DeleteInput x,Ledger db) => db.Delete(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/transfers", (int companyId, TransferInput x, Ledger db) => db.Transfer(companyId,x));
app.MapPost("/api/backup", (Ledger db) => new { path = db.Backup() });
app.MapFallbackToFile("index.html");
app.Services.GetRequiredService<Ledger>();
app.Run();

static async Task<object> LookupCnpj(string input) {
    var cnpj=new string(input.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    if(cnpj.Length!=14)throw new ArgumentException("Informe o CNPJ completo para consultar.");
    try {
        using var client=new HttpClient { Timeout=TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MonalisaFinanceiro/1.0");
        using var response=await client.GetAsync($"https://brasilapi.com.br/api/cnpj/v1/{Uri.EscapeDataString(cnpj)}");
        if(!response.IsSuccessStatusCode)throw new ArgumentException("CNPJ não encontrado. Confira o número ou preencha os dados manualmente.");
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var root=json.RootElement;
        string Value(string key)=>root.TryGetProperty(key,out var value)?value.GetString()??"":"";
        return new {cnpj,name=Value("razao_social"),tradeName=Value("nome_fantasia"),status=Value("descricao_situacao_cadastral"),city=Value("municipio"),state=Value("uf")};
    } catch(ArgumentException){throw;} catch { throw new ArgumentException("Não foi possível consultar o CNPJ agora. Verifique a internet ou preencha os dados manualmente."); }
}

static AuthIdentity CurrentUser(HttpContext context) => context.Items["user"] as AuthIdentity
    ?? throw new ArgumentException("Faça login para continuar.");

record AuthIdentity(int Id,string Name,string Username,bool GlobalAdmin);

sealed class AuthSessions {
    readonly Dictionary<string,(AuthIdentity Identity,DateTime Expires)> sessions=new();
    readonly object gate=new();
    public string Create(AuthIdentity identity) { lock(gate) { Cleanup();var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));var expires=identity.Id==0?DateTime.Today.AddDays(1):DateTime.Now.AddHours(12);sessions[token]=(identity,expires);return token; } }
    public bool TryGet(string token,out AuthIdentity identity) { lock(gate) { Cleanup();if(token.Length>0&&sessions.TryGetValue(token,out var session)){if(session.Identity.Id!=0)sessions[token]=(session.Identity,DateTime.Now.AddHours(12));identity=session.Identity;return true;}identity=null!;return false; } }
    public void Remove(string token) { lock(gate) sessions.Remove(token); }
    void Cleanup(){foreach(var token in sessions.Where(item=>item.Value.Expires<=DateTime.Now).Select(item=>item.Key).ToList())sessions.Remove(token);}
}

sealed class MasterAccess {
    const string Username="ADMIN";
    readonly Dictionary<string,string> dailyHashes;
    readonly object gate=new();
    int failedAttempts;
    DateTime lockedUntil=DateTime.MinValue;
    public MasterAccess() {
        var assembly=typeof(MasterAccess).Assembly;
        var resource=assembly.GetManifestResourceNames().SingleOrDefault(name=>name.EndsWith("master-access-codes.json",StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Tabela de acesso mestre não encontrada.");
        using var stream=assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Tabela de acesso mestre não encontrada.");
        dailyHashes=JsonSerializer.Deserialize<Dictionary<string,string>>(stream)
            ?? throw new InvalidOperationException("Tabela de acesso mestre inválida.");
    }
    public bool IsMasterUsername(string? value)=>string.Equals(value?.Trim(),Username,StringComparison.OrdinalIgnoreCase);
    public AuthIdentity Authenticate(string? pin) { lock(gate) {
        if(DateTime.Now<lockedUntil)throw new ArgumentException("Acesso mestre temporariamente bloqueado. Aguarde cinco minutos.");
        var day=DateTime.Today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        var valid=dailyHashes.TryGetValue(day,out var expected) && pin is not null && Regex.IsMatch(pin,@"^\d{10}$") &&
            CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected),SHA256.HashData(Encoding.UTF8.GetBytes($"{day}:{pin}")));
        if(!valid) {
            failedAttempts++;
            if(failedAttempts>=5){failedAttempts=0;lockedUntil=DateTime.Now.AddMinutes(5);throw new ArgumentException("Muitas tentativas. O acesso mestre foi bloqueado por cinco minutos.");}
            throw new ArgumentException("Usuário ou PIN inválido.");
        }
        failedAttempts=0;lockedUntil=DateTime.MinValue;
        return new AuthIdentity(0,"SUPORTE MONALISA",Username,true);
    } }
}

record CompanyInput(string Name, string Cnpj);
record BootstrapInput(string Name,string Username,string Pin);
record LoginInput(string Username,string Pin);
record UserAccessInput(string Name,string Username,string Pin,string Role="admin");
record LinkUserInput(string Role="admin");
record AccessUpdateInput(string Role="admin",bool Active=true);
record PartyInput(string Name,string Document,string Type,string Phone="",string Email="");
record AccountInput(string Name,long Opening);
record PlanInput(string Name,decimal InterestRate=0,decimal PenaltyRate=0,decimal DiscountRate=0);
record TitleInput(string Kind,string Description,long Amount,string Due,int? PartyId,string Category,string Notes,int Installments = 1,decimal InterestRate = 0,decimal PenaltyRate = 0,decimal DiscountRate = 0,long[]? InstallmentAmounts = null,string[]? InstallmentDues = null,string DocumentNumber = "",string IssueDate = "",string Branch = "",string CostCenter = "");
record SettlementInput(int AccountId,long Principal,long Interest,long Penalty,long Discount,string Date);
record ReasonInput(string Reason);
record CancelInput(string Reason, string Scope = "single");
record EditTitleInput(string Description,long Amount,string Due,int? PartyId,string Category,string Notes,string Scope = "single",string DocumentNumber = "",string IssueDate = "",string Branch = "",string CostCenter = "");
record DeleteInput(string Reason, string Scope = "single");
record TransferInput(int FromId,int ToId,long Amount,string Date);

sealed class Ledger {
    readonly string path;
    readonly object gate = new();
    public Ledger() {
        var configuredPath=Environment.GetEnvironmentVariable("FINANCE_DB");
        var developmentPath=Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),"..","data","financeiro.db"));
        var runningFromProject=Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(),"..","app"));
        path=Path.GetFullPath(configuredPath ?? (runningFromProject ? developmentPath : Path.Combine(AppContext.BaseDirectory,"data","financeiro.db")));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var c = Open();
        Run(c,"""
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS companies(id INTEGER PRIMARY KEY,name TEXT NOT NULL,cnpj TEXT NOT NULL UNIQUE);
        CREATE TABLE IF NOT EXISTS parties(id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),name TEXT NOT NULL,document TEXT NOT NULL,type TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS accounts(id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),name TEXT NOT NULL,opening INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS plans(id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),name TEXT NOT NULL,interestRate REAL NOT NULL DEFAULT 0,penaltyRate REAL NOT NULL DEFAULT 0,discountRate REAL NOT NULL DEFAULT 0,UNIQUE(companyId,name));
        CREATE TABLE IF NOT EXISTS titles(id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),kind TEXT NOT NULL CHECK(kind IN ('payable','receivable')),description TEXT NOT NULL,amount INTEGER NOT NULL CHECK(amount>0),due TEXT NOT NULL,partyId INTEGER REFERENCES parties(id),category TEXT NOT NULL,notes TEXT NOT NULL,cancelled INTEGER NOT NULL DEFAULT 0,created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
        CREATE TABLE IF NOT EXISTS settlements(id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),titleId INTEGER NOT NULL REFERENCES titles(id),accountId INTEGER NOT NULL REFERENCES accounts(id),principal INTEGER NOT NULL CHECK(principal>0),interest INTEGER NOT NULL,penalty INTEGER NOT NULL,discount INTEGER NOT NULL,date TEXT NOT NULL,reversed INTEGER NOT NULL DEFAULT 0,reason TEXT NOT NULL DEFAULT '');
        CREATE TABLE IF NOT EXISTS movements(id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),accountId INTEGER NOT NULL REFERENCES accounts(id),amount INTEGER NOT NULL,date TEXT NOT NULL,description TEXT NOT NULL,settlementId INTEGER REFERENCES settlements(id));
        CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),action TEXT NOT NULL,detail TEXT NOT NULL,created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
        CREATE INDEX IF NOT EXISTS ix_titles_company_due ON titles(companyId,due);
        CREATE INDEX IF NOT EXISTS ix_movements_company ON movements(companyId,accountId);
        CREATE INDEX IF NOT EXISTS ix_settlements_title ON settlements(titleId,reversed);
        """);
        if (!Query(c,"PRAGMA table_info(titles)").Any(r => (string)r["name"]! == "installmentGroup")) {
            // Keep a consistent copy before upgrading any pre-existing financial data.
            if (Query(c,"SELECT id FROM titles LIMIT 1").Count > 0) Backup();
            using var migration=c.BeginTransaction();
            Run(c,"ALTER TABLE titles ADD COLUMN installmentGroup TEXT; ALTER TABLE titles ADD COLUMN installmentNumber INTEGER NOT NULL DEFAULT 1; ALTER TABLE titles ADD COLUMN installmentCount INTEGER NOT NULL DEFAULT 1;");
            // Old releases recorded the complete creation request. Link only unambiguous matches.
            foreach(var entry in Query(c,"SELECT * FROM audit WHERE action='Lançamento' ORDER BY id")) {
                TitleInput? original;
                try { original=JsonSerializer.Deserialize<TitleInput>((string)entry["detail"]!); } catch(JsonException) { continue; }
                if(original is null || original.Installments < 2 || original.Installments > 120) continue;
                var ids=new List<long>();
                if(!DateOnly.TryParseExact(original.Due,"yyyy-MM-dd",out var firstDue)) continue;
                for(var i=0;i<original.Installments;i++) {
                    var candidates=Query(c,"SELECT id FROM titles WHERE companyId=@p0 AND kind=@p1 AND description=@p2 AND amount=@p3 AND due=@p4 AND partyId IS @p5 AND category=@p6 AND notes=@p7 AND created=@p8 AND installmentGroup IS NULL",entry["companyId"],original.Kind,original.Description.Trim()+$" ({i+1}/{original.Installments})",original.Amount/original.Installments+(i<original.Amount%original.Installments?1:0),firstDue.AddMonths(i).ToString("yyyy-MM-dd"),original.PartyId,original.Category??"",original.Notes??"",entry["created"]);
                    if(candidates.Count!=1){ids.Clear();break;}
                    ids.Add(Number(candidates[0],"id"));
                }
                if(ids.Count!=original.Installments)continue;
                var group=Guid.NewGuid().ToString("N");
                for(var i=0;i<ids.Count;i++)Run(c,"UPDATE titles SET installmentGroup=@p0,installmentNumber=@p1,installmentCount=@p2 WHERE id=@p3",group,i+1,ids.Count,ids[i]);
            }
            Run(c,"PRAGMA user_version=2");migration.Commit();
        }
        if (!Query(c,"PRAGMA table_info(titles)").Any(r => (string)r["name"]! == "deleted")) {
            if (Query(c,"SELECT id FROM titles LIMIT 1").Count > 0) Backup();
            Run(c,"ALTER TABLE titles ADD COLUMN deleted INTEGER NOT NULL DEFAULT 0; PRAGMA user_version=3");
        }
        if (!Query(c,"PRAGMA table_info(titles)").Any(r => (string)r["name"]! == "interestRate")) {
            if (Query(c,"SELECT id FROM titles LIMIT 1").Count > 0) Backup();
            Run(c,"ALTER TABLE titles ADD COLUMN interestRate REAL NOT NULL DEFAULT 0; ALTER TABLE titles ADD COLUMN penaltyRate REAL NOT NULL DEFAULT 0; ALTER TABLE titles ADD COLUMN discountRate REAL NOT NULL DEFAULT 0; PRAGMA user_version=4");
        }
        if (!Query(c,"PRAGMA table_info(parties)").Any(r => (string)r["name"]! == "phone")) {
            if (Query(c,"SELECT id FROM parties LIMIT 1").Count > 0) Backup();
            Run(c,"ALTER TABLE parties ADD COLUMN phone TEXT NOT NULL DEFAULT ''; ALTER TABLE parties ADD COLUMN email TEXT NOT NULL DEFAULT ''; PRAGMA user_version=5");
        }
        var version=Convert.ToInt32(Query(c,"PRAGMA user_version")[0]["user_version"]);
        if(version<6){Run(c,"UPDATE companies SET name=upper(name); UPDATE parties SET name=upper(name); UPDATE accounts SET name=upper(name); UPDATE plans SET name=upper(name); UPDATE titles SET description=upper(description),category=upper(category),notes=upper(notes); PRAGMA user_version=6");}
        if(version<7){
            foreach(var tableColumn in new[]{("companies","name"),("parties","name"),("accounts","name"),("titles","description"),("titles","category"),("titles","notes")})
                foreach(var row in Query(c,$"SELECT id,{tableColumn.Item2} AS value FROM {tableColumn.Item1}"))Run(c,$"UPDATE {tableColumn.Item1} SET {tableColumn.Item2}=@p0 WHERE id=@p1",((string)row["value"]!).ToUpperInvariant(),row["id"]);
            foreach(var companyRow in Query(c,"SELECT id FROM companies")) {
                var companyId=Convert.ToInt32(companyRow["id"]);
                var groups=Query(c,"SELECT id,name FROM plans WHERE companyId=@p0 ORDER BY id",companyId)
                    .GroupBy(row=>((string)row["name"]!).ToUpperInvariant(),StringComparer.Ordinal);
                foreach(var group in groups) {
                    var keep=group.First();
                    Run(c,"UPDATE plans SET name=@p0 WHERE id=@p1",group.Key,keep["id"]);
                    foreach(var duplicate in group.Skip(1))Run(c,"DELETE FROM plans WHERE id=@p0",duplicate["id"]);
                }
            }
            Run(c,"PRAGMA user_version=7");
        }
        Run(c,"CREATE INDEX IF NOT EXISTS ix_titles_installment_group ON titles(companyId,installmentGroup)");
        if (Query(c,"SELECT id FROM companies").Count == 0) {
            Run(c,"INSERT INTO companies(name,cnpj) VALUES('EMPRESA DE TESTE','TESTE-LOCAL')");
            Run(c,"INSERT INTO accounts(companyId,name,opening) VALUES(1,'CAIXA',0)");
        }
        foreach(var company in Query(c,"SELECT id FROM companies")) SeedPlans(c,Convert.ToInt32(company["id"]));
        var currentVersion=Convert.ToInt32(Query(c,"PRAGMA user_version")[0]["user_version"]);
        if(currentVersion<8) {
            foreach(var tableColumn in new[]{("companies","name"),("parties","name"),("accounts","name"),("plans","name"),("titles","description"),("titles","category"),("titles","notes"),("settlements","reason")})
                foreach(var row in Query(c,$"SELECT id,{tableColumn.Item2} AS value FROM {tableColumn.Item1}"))
                    Run(c,$"UPDATE {tableColumn.Item1} SET {tableColumn.Item2}=@p0 WHERE id=@p1",((string)row["value"]!).ToUpperInvariant(),row["id"]);
            Run(c,"PRAGMA user_version=8");
        }
        if (!Query(c,"PRAGMA table_info(titles)").Any(r => (string)r["name"]! == "documentNumber")) {
            if (Query(c,"SELECT id FROM titles LIMIT 1").Count > 0) Backup();
            Run(c,"ALTER TABLE titles ADD COLUMN documentNumber TEXT NOT NULL DEFAULT ''; ALTER TABLE titles ADD COLUMN issueDate TEXT NOT NULL DEFAULT ''; ALTER TABLE titles ADD COLUMN branch TEXT NOT NULL DEFAULT ''; ALTER TABLE titles ADD COLUMN costCenter TEXT NOT NULL DEFAULT ''; PRAGMA user_version=9");
        }
        currentVersion=Convert.ToInt32(Query(c,"PRAGMA user_version")[0]["user_version"]);
        if(currentVersion<10) {
            if(Query(c,"SELECT id FROM companies LIMIT 1").Count>0)Backup();
            Run(c,"""
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
    }
    SqliteConnection Open() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=path, ForeignKeys=true }.ToString()); c.Open(); return c; }
    static SqliteCommand Cmd(SqliteConnection c,string sql,params object?[] values) { var cmd=c.CreateCommand(); cmd.CommandText=sql; for(int i=0;i<values.Length;i++) cmd.Parameters.AddWithValue("@p"+i,values[i]??DBNull.Value); return cmd; }
    static void Run(SqliteConnection c,string sql,params object?[] values) { using var cmd=Cmd(c,sql,values);cmd.ExecuteNonQuery(); }
    static List<Dictionary<string,object?>> Query(SqliteConnection c,string sql,params object?[] values) { using var cmd=Cmd(c,sql,values);using var r=cmd.ExecuteReader();var rows=new List<Dictionary<string,object?>>();while(r.Read()){var row=new Dictionary<string,object?>();for(int i=0;i<r.FieldCount;i++)row[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);rows.Add(row);}return rows; }
    public object Read(string sql) { lock(gate){using var c=Open();return Query(c,sql);} }
    static long Number(Dictionary<string,object?> row,string key)=>Convert.ToInt64(row[key]);
    static string Required(string? value,string field) { if(string.IsNullOrWhiteSpace(value)||value.Length>500)throw new ArgumentException(field+": informe um valor válido (até 500 caracteres).");return value.Trim().ToUpperInvariant(); }
    static string RequiredOrEmpty(string? value) { if((value??"").Length>500)throw new ArgumentException("Texto acima do limite de 500 caracteres.");return (value??"").Trim().ToUpperInvariant(); }
    static void Money(long amount,bool positive=false) { if(amount<0||(positive&&amount==0)||amount>999999999999)throw new ArgumentException("Valor fora do limite permitido."); }
    static DateOnly Date(string value) { if(!DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))throw new ArgumentException("Data inválida.");return date; }
    static void Existing(SqliteConnection c,string table,int id,int company) { if(Query(c,$"SELECT id FROM {table} WHERE id=@p0 AND companyId=@p1",id,company).Count==0)throw new ArgumentException("Registro não pertence à empresa selecionada."); }
    static void Company(SqliteConnection c,int id) { if(Query(c,"SELECT id FROM companies WHERE id=@p0",id).Count==0)throw new ArgumentException("Empresa não encontrada."); }
    static string LinkedTitles(List<Dictionary<string,object?>> rows)=>string.Join(", ",rows.Select(row=>$"\"{row["description"]}\" (vencimento {Date((string)row["due"]!).ToString("dd/MM/yyyy")})"));
    static string LinkedMovements(List<Dictionary<string,object?>> rows)=>string.Join(", ",rows.Select(row=>$"\"{row["description"]}\" ({Date((string)row["date"]!).ToString("dd/MM/yyyy")})"));
    static void Audit(SqliteConnection c,int company,string action,object detail)=>Run(c,"INSERT INTO audit(companyId,action,detail) VALUES(@p0,@p1,@p2)",company,action,JsonSerializer.Serialize(detail));
    static void SeedPlans(SqliteConnection c,int company) { foreach(var name in new[]{"DESPESAS GERAIS","FORNECEDORES","ALUGUEL E OCUPAÇÃO","IMPOSTOS E TAXAS","FOLHA DE PAGAMENTO","SERVIÇOS CONTRATADOS","RECEITAS DE VENDAS","RECEITAS DE SERVIÇOS"}) if(Query(c,"SELECT id FROM plans WHERE companyId=@p0 AND upper(name)=upper(@p1)",company,name).Count==0)Run(c,"INSERT INTO plans(companyId,name) VALUES(@p0,@p1)",company,name); }
    object Write(Func<SqliteConnection,object> operation) { lock(gate){using var c=Open();using var tx=c.BeginTransaction();var result=operation(c);tx.Commit();return result;} }
    public bool HasUsers(){lock(gate){using var c=Open();return Query(c,"SELECT id FROM users LIMIT 1").Count>0;}}
    static string Username(string? value){var username=Required(value,"Usuário").Replace(" ","");if(username.Length<3||username.Length>40||!Regex.IsMatch(username,@"^[A-Z0-9._-]+$"))throw new ArgumentException("Use de 3 a 40 caracteres no usuário: letras, números, ponto, traço ou sublinhado.");if(string.Equals(username,"ADMIN",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("O usuário ADMIN é reservado para o acesso mestre diário.");return username;}
    static void ValidatePin(string? value){if(value is null||!Regex.IsMatch(value,@"^\d{6,12}$"))throw new ArgumentException("O PIN deve conter de 6 a 12 números.");}
    static (string Salt,string Hash) HashPin(string pin){var salt=RandomNumberGenerator.GetBytes(16);var hash=Rfc2898DeriveBytes.Pbkdf2(pin,salt,120000,HashAlgorithmName.SHA256,32);return(Convert.ToBase64String(salt),Convert.ToBase64String(hash));}
    static bool CheckPin(string pin,string salt,string expected){try{var hash=Rfc2898DeriveBytes.Pbkdf2(pin,Convert.FromBase64String(salt),120000,HashAlgorithmName.SHA256,32);return CryptographicOperations.FixedTimeEquals(hash,Convert.FromBase64String(expected));}catch{return false;}}
    static AuthIdentity Identity(Dictionary<string,object?> row)=>new(Convert.ToInt32(row["id"]), (string)row["name"]!, (string)row["username"]!, Number(row,"globalAdmin")==1);
    public AuthIdentity SetupAdministrator(BootstrapInput x){lock(gate){using var c=Open();using var tx=c.BeginTransaction();if(Query(c,"SELECT id FROM users LIMIT 1").Count>0)throw new ArgumentException("O administrador geral já foi configurado.");ValidatePin(x.Pin);var pin=HashPin(x.Pin);Run(c,"INSERT INTO users(name,username,pinSalt,pinHash,globalAdmin) VALUES(@p0,@p1,@p2,@p3,1)",Required(x.Name,"Nome"),Username(x.Username),pin.Salt,pin.Hash);var id=Convert.ToInt32(Number(Query(c,"SELECT last_insert_rowid() AS id")[0],"id"));Run(c,"INSERT INTO access_audit(actorUserId,action,detail) VALUES(@p0,'CRIAÇÃO DO ADMINISTRADOR GERAL',@p1)",id,JsonSerializer.Serialize(new{usuario=Username(x.Username)}));tx.Commit();return new(id,Required(x.Name,"Nome"),Username(x.Username),true);}}
    public AuthIdentity Authenticate(LoginInput x){lock(gate){using var c=Open();var username=Username(x.Username);var rows=Query(c,"SELECT * FROM users WHERE username=@p0",username);if(rows.Count==0)throw new ArgumentException("Usuário ou PIN inválido.");var row=rows[0];if(Number(row,"active")==0)throw new ArgumentException("Este acesso está bloqueado.");if(DateTime.TryParse((string)row["lockedUntil"]!,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var locked)&&locked>DateTime.UtcNow)throw new ArgumentException("Acesso temporariamente bloqueado. Aguarde alguns minutos.");if(!CheckPin(x.Pin,(string)row["pinSalt"]!,(string)row["pinHash"]!)){var attempts=Convert.ToInt32(Number(row,"failedAttempts"))+1;var until=attempts>=5?DateTime.UtcNow.AddMinutes(5).ToString("O"):"";Run(c,"UPDATE users SET failedAttempts=@p0,lockedUntil=@p1 WHERE id=@p2",attempts>=5?0:attempts,until,row["id"]);throw new ArgumentException(attempts>=5?"Muitas tentativas. O acesso foi bloqueado por cinco minutos.":"Usuário ou PIN inválido.");}Run(c,"UPDATE users SET failedAttempts=0,lockedUntil='' WHERE id=@p0",row["id"]);Run(c,"INSERT INTO access_audit(actorUserId,action,detail) VALUES(@p0,'LOGIN',@p1)",row["id"],JsonSerializer.Serialize(new{usuario=username}));return Identity(row);}}
    public AuthIdentity? SessionUser(int id){lock(gate){using var c=Open();var rows=Query(c,"SELECT * FROM users WHERE id=@p0 AND active=1",id);return rows.Count==0?null:Identity(rows[0]);}}
    public bool CanAccessCompany(int userId,int companyId){lock(gate){using var c=Open();return Query(c,"SELECT id FROM users WHERE id=@p0 AND active=1 AND globalAdmin=1",userId).Count>0||Query(c,"SELECT userId FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND active=1",userId,companyId).Count>0;}}
    public bool CanManageCompanyAccess(AuthIdentity actor,int companyId){lock(gate){if(actor.GlobalAdmin)return true;using var c=Open();return Query(c,"SELECT userId FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND role='admin' AND active=1",actor.Id,companyId).Count>0;}}
    public object Companies(AuthIdentity? user){lock(gate){using var c=Open();if(user is null||user.GlobalAdmin)return Query(c,"SELECT id,name,cnpj,'global' AS role FROM companies ORDER BY name");return Query(c,"SELECT c.id,c.name,c.cnpj,uc.role FROM companies c JOIN user_companies uc ON uc.companyId=c.id WHERE uc.userId=@p0 AND uc.active=1 ORDER BY c.name",user.Id);}}
    public object AccessContext(AuthIdentity actor){lock(gate){using var c=Open();if(actor.Id==0)return new{id=actor.Id,name=actor.Name,username=actor.Username,globalAdmin=true,companies=Query(c,"SELECT id,name,cnpj,'global' AS role FROM companies ORDER BY name")};var rows=Query(c,"SELECT * FROM users WHERE id=@p0 AND active=1",actor.Id);if(rows.Count==0)throw new ArgumentException("Acesso não encontrado.");var user=Identity(rows[0]);var companies=user.GlobalAdmin?Query(c,"SELECT id,name,cnpj,'global' AS role FROM companies ORDER BY name"):Query(c,"SELECT c.id,c.name,c.cnpj,uc.role FROM companies c JOIN user_companies uc ON uc.companyId=c.id WHERE uc.userId=@p0 AND uc.active=1 ORDER BY c.name",actor.Id);return new{id=user.Id,name=user.Name,username=user.Username,globalAdmin=user.GlobalAdmin,companies};}}
    public void RecordMasterLogin(){lock(gate){using var c=Open();Run(c,"INSERT INTO access_audit(actorUserId,action,detail) VALUES(NULL,'LOGIN MESTRE',@p0)",JsonSerializer.Serialize(new{usuario="ADMIN"}));}}
    public object AllUsers(AuthIdentity actor){lock(gate){if(!actor.GlobalAdmin)throw new ArgumentException("Somente o administrador geral pode consultar todos os acessos.");using var c=Open();return Query(c,"SELECT id,name,username,globalAdmin,active FROM users ORDER BY name");}}
    static void RequireCompanyAdmin(SqliteConnection c,int company,AuthIdentity actor){Company(c,company);if(actor.GlobalAdmin)return;var rows=Query(c,"SELECT role FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND active=1",actor.Id,company);if(rows.Count==0||!string.Equals(rows[0]["role"] as string,"admin",StringComparison.Ordinal))throw new ArgumentException("Somente um administrador desta empresa pode gerenciar acessos.");}
    public object CompanyUsers(int company,AuthIdentity actor){lock(gate){using var c=Open();RequireCompanyAdmin(c,company,actor);return Query(c,"SELECT u.id,u.name,u.username,u.active AS userActive,uc.role,uc.active FROM users u JOIN user_companies uc ON uc.userId=u.id WHERE uc.companyId=@p0 ORDER BY u.name",company);}}
    public object CreateCompanyUser(int company,UserAccessInput x,AuthIdentity actor)=>Write(c=>{RequireCompanyAdmin(c,company,actor);var role=x.Role.ToLowerInvariant();if(role!="admin"&&role!="user")throw new ArgumentException("Tipo de acesso inválido.");if(role=="admin"&&!actor.GlobalAdmin)throw new ArgumentException("Somente o administrador geral pode criar outro administrador.");ValidatePin(x.Pin);var username=Username(x.Username);if(Query(c,"SELECT id FROM users WHERE username=@p0",username).Count>0)throw new ArgumentException("Este nome de usuário já está em uso.");var pin=HashPin(x.Pin);Run(c,"INSERT INTO users(name,username,pinSalt,pinHash) VALUES(@p0,@p1,@p2,@p3)",Required(x.Name,"Nome"),username,pin.Salt,pin.Hash);var id=Number(Query(c,"SELECT last_insert_rowid() AS id")[0],"id");Run(c,"INSERT INTO user_companies(userId,companyId,role) VALUES(@p0,@p1,@p2)",id,company,role);Run(c,"INSERT INTO access_audit(actorUserId,companyId,action,detail) VALUES(@p0,@p1,'CRIAÇÃO DE ACESSO',@p2)",actor.Id==0?null:actor.Id,company,JsonSerializer.Serialize(new{id,username,role}));return new{id};});
    public object LinkCompanyUser(int company,int userId,LinkUserInput x,AuthIdentity actor)=>Write(c=>{if(!actor.GlobalAdmin)throw new ArgumentException("Somente o administrador geral pode vincular uma pessoa a outra empresa.");RequireCompanyAdmin(c,company,actor);var role=x.Role.ToLowerInvariant();if(role!="admin"&&role!="user")throw new ArgumentException("Tipo de acesso inválido.");var user=Query(c,"SELECT id,globalAdmin FROM users WHERE id=@p0 AND active=1",userId);if(user.Count==0)throw new ArgumentException("Usuário não encontrado.");if(Number(user[0],"globalAdmin")==1)throw new ArgumentException("O administrador geral já possui acesso a todas as empresas.");if(Query(c,"SELECT userId FROM user_companies WHERE userId=@p0 AND companyId=@p1",userId,company).Count>0)throw new ArgumentException("Esta pessoa já possui vínculo com a empresa.");Run(c,"INSERT INTO user_companies(userId,companyId,role) VALUES(@p0,@p1,@p2)",userId,company,role);Run(c,"INSERT INTO access_audit(actorUserId,companyId,action,detail) VALUES(@p0,@p1,'VÍNCULO DE ACESSO',@p2)",actor.Id==0?null:actor.Id,company,JsonSerializer.Serialize(new{userId,role}));return new{ok=true};});
    public object UpdateCompanyUser(int company,int userId,AccessUpdateInput x,AuthIdentity actor)=>Write(c=>{RequireCompanyAdmin(c,company,actor);var role=x.Role.ToLowerInvariant();if(role!="admin"&&role!="user")throw new ArgumentException("Tipo de acesso inválido.");if((role=="admin"||userId==actor.Id)&&!actor.GlobalAdmin)throw new ArgumentException("Somente o administrador geral pode alterar este acesso.");if(Query(c,"SELECT userId FROM user_companies WHERE userId=@p0 AND companyId=@p1",userId,company).Count==0)throw new ArgumentException("Acesso não encontrado nesta empresa.");Run(c,"UPDATE user_companies SET role=@p0,active=@p1 WHERE userId=@p2 AND companyId=@p3",role,x.Active?1:0,userId,company);Run(c,"INSERT INTO access_audit(actorUserId,companyId,action,detail) VALUES(@p0,@p1,'ALTERAÇÃO DE ACESSO',@p2)",actor.Id==0?null:actor.Id,company,JsonSerializer.Serialize(new{userId,role,x.Active}));return new{ok=true};});
    public object CreateCompany(CompanyInput x) => Write(c=> {
        var name=Required(x.Name,"Nome");var cnpj=new string((x.Cnpj??"").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if(cnpj.Length!=14)throw new ArgumentException("Informe o CNPJ com 14 caracteres.");
        Run(c,"INSERT INTO companies(name,cnpj) VALUES(@p0,@p1)",name,cnpj);
        var id=Number(Query(c,"SELECT last_insert_rowid() AS id")[0],"id");
        Run(c,"INSERT INTO accounts(companyId,name,opening) VALUES(@p0,'Caixa',0)",id);SeedPlans(c,Convert.ToInt32(id));return new {id};
    });
    public object Party(int company,PartyInput x)=>Write(c=> { Company(c,company);ValidateParty(x);Run(c,"INSERT INTO parties(companyId,name,document,type,phone,email) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)",company,Required(x.Name,"Nome"),x.Document??"",x.Type,x.Phone??"",x.Email??"");var id=Number(Query(c,"SELECT last_insert_rowid() AS id")[0],"id");Audit(c,company,"Cadastro de pessoa",x.Name);return new {ok=true,id}; });
    static void ValidateParty(PartyInput x){if(x.Type!="customer"&&x.Type!="supplier"&&x.Type!="both")throw new ArgumentException("Tipo de pessoa inválido.");if(!string.IsNullOrWhiteSpace(x.Email)&&!x.Email.Contains('@'))throw new ArgumentException("Informe um e-mail válido.");}
    public object EditParty(int company,int id,PartyInput x)=>Write(c=> {Existing(c,"parties",id,company);ValidateParty(x);Run(c,"UPDATE parties SET name=@p0,document=@p1,type=@p2,phone=@p3,email=@p4 WHERE id=@p5",Required(x.Name,"Nome"),x.Document??"",x.Type,x.Phone??"",x.Email??"",id);Audit(c,company,"Edição de pessoa",new{id,input=x});return new {ok=true};});
    public object DeleteParty(int company,int id)=>Write(c=> {Existing(c,"parties",id,company);var linked=Query(c,"SELECT description,due FROM titles WHERE partyId=@p0 AND deleted=0 ORDER BY due,id LIMIT 5",id);if(linked.Count>0)throw new ArgumentException("Não foi possível excluir. Este cadastro está vinculado aos lançamentos: "+LinkedTitles(linked)+".");var name=(string)Query(c,"SELECT name FROM parties WHERE id=@p0",id)[0]["name"]!;Run(c,"UPDATE titles SET partyId=NULL WHERE partyId=@p0 AND deleted=1",id);Run(c,"DELETE FROM parties WHERE id=@p0",id);Audit(c,company,"Exclusão de pessoa",new{id,name});return new{ok=true};});
    public object Account(int company,AccountInput x)=>Write(c=> {Company(c,company);if(x.Opening < -999999999999 || x.Opening > 999999999999)throw new ArgumentException("Saldo fora do limite.");Run(c,"INSERT INTO accounts(companyId,name,opening) VALUES(@p0,@p1,@p2)",company,Required(x.Name,"Nome"),x.Opening);Audit(c,company,"Cadastro de conta",x);return new {ok=true};});
    public object DeleteAccount(int company,int id)=>Write(c=> {Existing(c,"accounts",id,company);var linked=Query(c,"SELECT description,date FROM movements WHERE accountId=@p0 ORDER BY date,id LIMIT 5",id);if(linked.Count>0)throw new ArgumentException("Não foi possível excluir. Esta conta está vinculada às movimentações: "+LinkedMovements(linked)+".");if(Query(c,"SELECT id FROM accounts WHERE companyId=@p0",company).Count<=1)throw new ArgumentException("Mantenha ao menos uma conta financeira para registrar as baixas.");var name=(string)Query(c,"SELECT name FROM accounts WHERE id=@p0",id)[0]["name"]!;Run(c,"DELETE FROM accounts WHERE id=@p0",id);Audit(c,company,"Exclusão de conta",new{id,name});return new{ok=true};});
    public object Plan(int company,PlanInput x)=>Write(c=> {Company(c,company);var rates=new[]{x.InterestRate,x.PenaltyRate,x.DiscountRate};if(rates.Any(v=>v<0||v>100))throw new ArgumentException("Os percentuais devem ficar entre 0 e 100.");Run(c,"INSERT INTO plans(companyId,name,interestRate,penaltyRate,discountRate) VALUES(@p0,@p1,@p2,@p3,@p4)",company,Required(x.Name,"Plano de contas"),x.InterestRate,x.PenaltyRate,x.DiscountRate);Audit(c,company,"Cadastro de plano de contas",x);return new {ok=true};});
    public object EditPlan(int company,int id,PlanInput x)=>Write(c=> {Existing(c,"plans",id,company);var rates=new[]{x.InterestRate,x.PenaltyRate,x.DiscountRate};if(rates.Any(v=>v<0||v>100))throw new ArgumentException("Os percentuais devem ficar entre 0 e 100.");var name=Required(x.Name,"Plano de contas");var old=(string)Query(c,"SELECT name FROM plans WHERE id=@p0",id)[0]["name"]!;Run(c,"UPDATE plans SET name=@p0,interestRate=@p1,penaltyRate=@p2,discountRate=@p3 WHERE id=@p4",name,x.InterestRate,x.PenaltyRate,x.DiscountRate,id);Run(c,"UPDATE titles SET category=@p0,interestRate=@p1,penaltyRate=@p2,discountRate=@p3 WHERE companyId=@p4 AND category=@p5 AND deleted=0 AND cancelled=0",name,x.InterestRate,x.PenaltyRate,x.DiscountRate,company,old);Audit(c,company,"Edição de plano de contas",new {id,old,input=x});return new {ok=true};});
    public object DeletePlan(int company,int id)=>Write(c=> {Existing(c,"plans",id,company);var name=(string)Query(c,"SELECT name FROM plans WHERE id=@p0",id)[0]["name"]!;var linked=Query(c,"SELECT description,due FROM titles WHERE companyId=@p0 AND category=@p1 AND deleted=0 ORDER BY due,id LIMIT 5",company,name);if(linked.Count>0)throw new ArgumentException("Não foi possível excluir. Este plano de contas está vinculado aos lançamentos: "+LinkedTitles(linked)+".");Run(c,"DELETE FROM plans WHERE id=@p0",id);Audit(c,company,"Exclusão de plano de contas",new{id,name});return new{ok=true};});
    const string TitlesSql="""
        SELECT t.*,p.name AS partyName,COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) AS settled,
        t.amount-COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) AS remaining
        FROM titles t LEFT JOIN parties p ON p.id=t.partyId WHERE t.companyId=@p0 AND t.deleted=0 ORDER BY t.due,t.id
        """;
    public object State(int company,AuthIdentity? actor=null) { lock(gate){using var c=Open();Company(c,company);
        var financialUser=actor is not null&&!actor.GlobalAdmin&&Query(c,"SELECT role FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND active=1",actor.Id,company).Any(row=>string.Equals(row["role"] as string,"user",StringComparison.Ordinal));
        return new {
        titles=Query(c,TitlesSql,company),
        parties=Query(c,"SELECT * FROM parties WHERE companyId=@p0 ORDER BY name",company),
        accounts=Query(c,"SELECT a.*,a.opening+COALESCE((SELECT SUM(m.amount) FROM movements m WHERE m.accountId=a.id),0) AS balance FROM accounts a WHERE a.companyId=@p0 ORDER BY a.name",company),
        plans=Query(c,"SELECT * FROM plans WHERE companyId=@p0 ORDER BY name",company),
        settlements=Query(c,"SELECT s.*,a.name AS accountName FROM settlements s JOIN accounts a ON a.id=s.accountId WHERE s.companyId=@p0 ORDER BY s.id DESC",company),
        movements=financialUser?[]:Query(c,"SELECT m.*,a.name AS accountName FROM movements m JOIN accounts a ON a.id=m.accountId WHERE m.companyId=@p0 ORDER BY m.date DESC,m.id DESC",company),
        audit=financialUser?[]:Query(c,"SELECT * FROM audit WHERE companyId=@p0 ORDER BY id DESC LIMIT 100",company),
        permissions=new {role=financialUser?"financial":"administrator",canViewCashFlow=!financialUser,canViewHistory=!financialUser,canManageAccess=!financialUser}
    };} }
    public PdfReport FinancialReport(int company,string startValue,string endValue) { lock(gate){using var c=Open();Company(c,company);
        var start=Date(startValue);var selectedEnd=Date(endValue);if(selectedEnd<start)throw new ArgumentException("A data final não pode ser anterior à data inicial.");
        if(selectedEnd.DayNumber-start.DayNumber>730)throw new ArgumentException("Escolha um período de até dois anos.");
        var end=selectedEnd.AddDays(1);var startText=start.ToString("yyyy-MM-dd");var endText=end.ToString("yyyy-MM-dd");
        var companyRow=Query(c,"SELECT name,cnpj FROM companies WHERE id=@p0",company)[0];
        var titles=Query(c,TitlesSql,company).Where(row=>string.CompareOrdinal((string)row["due"]!,startText)>=0&&string.CompareOrdinal((string)row["due"]!,endText)<0).ToList();
        var settlements=Query(c,"""
            SELECT s.*,t.kind,t.description,a.name AS accountName
            FROM settlements s JOIN titles t ON t.id=s.titleId JOIN accounts a ON a.id=s.accountId
            WHERE s.companyId=@p0 AND s.reversed=0 AND t.deleted=0 AND t.cancelled=0
              AND s.date>=@p1 AND s.date<@p2
            ORDER BY s.date,s.id
            """,company,startText,endText);
        long Cash(Dictionary<string,object?> row)=>Number(row,"principal")+Number(row,"interest")+Number(row,"penalty")-Number(row,"discount");
        string TitleStatus(Dictionary<string,object?> row) {
            if(Number(row,"cancelled")==1)return "CANCELADO";
            if(Number(row,"remaining")==0)return (string)row["kind"]! == "payable" ? "PAGO" : "RECEBIDO";
            if(string.CompareOrdinal((string)row["due"]!,DateTime.Today.ToString("yyyy-MM-dd"))<0)return "VENCIDO";
            if(Number(row,"settled")>0)return "PARCIAL";
            return "EM ABERTO";
        }
        var active=titles.Where(row=>Number(row,"cancelled")==0).ToList();
        var reportTitles=active;
        var data=new MonthlyReportData(
            (string)companyRow["name"]!, (string)companyRow["cnpj"]!, start, selectedEnd,
            active.Where(row=>(string)row["kind"]! == "payable").Sum(row=>Number(row,"amount")),
            active.Where(row=>(string)row["kind"]! == "receivable").Sum(row=>Number(row,"amount")),
            active.Where(row=>(string)row["kind"]! == "payable").Sum(row=>Number(row,"remaining")),
            active.Where(row=>(string)row["kind"]! == "receivable").Sum(row=>Number(row,"remaining")),
            settlements.Where(row=>(string)row["kind"]! == "payable").Sum(Cash),
            settlements.Where(row=>(string)row["kind"]! == "receivable").Sum(Cash),
            settlements.Select(row=>new MonthlyReportLine(
                Date((string)row["date"]!).ToString("dd/MM/yyyy"),
                (string)row["kind"]! == "payable" ? "PAGAMENTO" : "RECEBIMENTO",
                (string)row["description"]!, (string)row["accountName"]!, Cash(row), Principal:Number(row,"principal"), Interest:Number(row,"interest"), Penalty:Number(row,"penalty"), Discount:Number(row,"discount"))).ToList(),
            reportTitles.Select(row=>new MonthlyReportLine(
                Date((string)row["due"]!).ToString("dd/MM/yyyy"),
                (string)row["kind"]! == "payable" ? "A PAGAR" : "A RECEBER",
                (string)row["description"]!, (string)row["category"]!, Number(row,"amount"), Number(row,"cancelled")==1 ? 0 : Number(row,"remaining"), TitleStatus(row))).ToList());
        var periodName=start==selectedEnd ? startText : $"{startText}-a-{endValue}";
        return new PdfReport(MonthlyReportPdf.Generate(data),$"relatorio-financeiro-{periodName}.pdf");
    } }
    public PdfReport PayableReport(int company,string monthValue,string type) { lock(gate){using var c=Open();Company(c,company);
        if(!DateOnly.TryParseExact(monthValue+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var start))throw new ArgumentException("Mês inválido.");
        var end=start.AddMonths(1);var startText=start.ToString("yyyy-MM-dd");var endText=end.ToString("yyyy-MM-dd");
        var allowed=new[]{"payments","by-plan","month","future"};if(!allowed.Contains(type))throw new ArgumentException("Tipo de relatório inválido.");
        var companyRow=Query(c,"SELECT name,cnpj FROM companies WHERE id=@p0",company)[0];
        var rows=new List<PayableReportRow>();
        if(type=="payments") {
            foreach(var row in Query(c,"""
                SELECT t.id,t.description,t.documentNumber,t.amount,t.due,t.category,COALESCE(p.name,'SEM FORNECEDOR') AS supplier,
                       s.date AS paymentDate,s.principal,s.interest,s.penalty,s.discount,
                       t.amount-COALESCE((SELECT SUM(x.principal) FROM settlements x WHERE x.titleId=t.id AND x.reversed=0),0) AS remaining
                FROM settlements s JOIN titles t ON t.id=s.titleId LEFT JOIN parties p ON p.id=t.partyId
                WHERE s.companyId=@p0 AND t.kind='payable' AND t.deleted=0 AND t.cancelled=0 AND s.reversed=0
                  AND s.date>=@p1 AND s.date<@p2 ORDER BY s.date,t.due,t.id,s.id
                """,company,startText,endText)) rows.Add(ReportRow(row,false));
        } else {
            var range=type=="future" ? "t.due>=@p1" : "t.due>=@p1 AND t.due<@p2";
            foreach(var row in Query(c,$"""
                SELECT t.id,t.description,t.documentNumber,t.amount,t.due,t.category,COALESCE(p.name,'SEM FORNECEDOR') AS supplier,
                       MAX(CASE WHEN s.reversed=0 THEN s.date END) AS paymentDate,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.principal ELSE 0 END),0) AS principal,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.interest ELSE 0 END),0) AS interest,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.penalty ELSE 0 END),0) AS penalty,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.discount ELSE 0 END),0) AS discount,
                       t.amount-COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.principal ELSE 0 END),0) AS remaining
                FROM titles t LEFT JOIN parties p ON p.id=t.partyId LEFT JOIN settlements s ON s.titleId=t.id
                WHERE t.companyId=@p0 AND t.kind='payable' AND t.deleted=0 AND t.cancelled=0 AND {range}
                GROUP BY t.id ORDER BY t.due,t.id
                """,company,type=="future"?endText:startText,endText)) rows.Add(ReportRow(row,false));
        }
        var title=type switch {"payments"=>"EXTRATO DE PAGAMENTOS","by-plan"=>"CONTAS A PAGAR POR PLANO DE CONTAS","month"=>"EXTRATO DE CONTAS A PAGAR",_=>"CONTAS A PAGAR DOS PRÓXIMOS MESES"};
        var groups=type switch {
            "by-plan"=>rows.GroupBy(r=>r.Plan).OrderBy(g=>g.Key).Select(g=>new PayableReportGroup(g.Key,g.ToList())).ToList(),
            "future"=>rows.GroupBy(r=>r.Due[..7]).OrderBy(g=>g.Key).Select(g=>new PayableReportGroup(DateOnly.ParseExact(g.Key+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture).ToString("MMMM 'DE' yyyy",CultureInfo.GetCultureInfo("pt-BR")).ToUpper(CultureInfo.GetCultureInfo("pt-BR")),g.ToList())).ToList(),
            _=>new List<PayableReportGroup>{new(start.ToString("MMMM 'DE' yyyy",CultureInfo.GetCultureInfo("pt-BR")).ToUpper(CultureInfo.GetCultureInfo("pt-BR")),rows)}
        };
        var data=new PayableReportData((string)companyRow["name"]!,(string)companyRow["cnpj"]!,title,start,"payable",type,groups);
        var slug=type switch {"payments"=>"extrato-pagamentos","by-plan"=>"contas-pagar-por-plano","month"=>"extrato-contas-pagar",_=>"contas-pagar-proximos-meses"};
        return new PdfReport(PayableReportsPdf.Generate(data),$"{slug}-{monthValue}.pdf");
    } }
    public PdfReport ReceivableReport(int company,string monthValue,string type) { lock(gate){using var c=Open();Company(c,company);
        if(!DateOnly.TryParseExact(monthValue+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var start))throw new ArgumentException("Mês inválido.");
        var end=start.AddMonths(1);var startText=start.ToString("yyyy-MM-dd");var endText=end.ToString("yyyy-MM-dd");
        var allowed=new[]{"receipts","by-plan","month","future"};if(!allowed.Contains(type))throw new ArgumentException("Tipo de relatório inválido.");
        var companyRow=Query(c,"SELECT name,cnpj FROM companies WHERE id=@p0",company)[0];
        var rows=new List<PayableReportRow>();
        if(type=="receipts") {
            foreach(var row in Query(c,"""
                SELECT t.id,t.description,t.documentNumber,t.amount,t.due,t.category,COALESCE(p.name,'SEM CLIENTE') AS supplier,
                       s.date AS paymentDate,s.principal,s.interest,s.penalty,s.discount,
                       t.amount-COALESCE((SELECT SUM(x.principal) FROM settlements x WHERE x.titleId=t.id AND x.reversed=0),0) AS remaining
                FROM settlements s JOIN titles t ON t.id=s.titleId LEFT JOIN parties p ON p.id=t.partyId
                WHERE s.companyId=@p0 AND t.kind='receivable' AND t.deleted=0 AND t.cancelled=0 AND s.reversed=0
                  AND s.date>=@p1 AND s.date<@p2 ORDER BY s.date,t.due,t.id,s.id
                """,company,startText,endText)) rows.Add(ReportRow(row,true));
        } else {
            var range=type=="future" ? "t.due>=@p1" : "t.due>=@p1 AND t.due<@p2";
            foreach(var row in Query(c,$"""
                SELECT t.id,t.description,t.documentNumber,t.amount,t.due,t.category,COALESCE(p.name,'SEM CLIENTE') AS supplier,
                       MAX(CASE WHEN s.reversed=0 THEN s.date END) AS paymentDate,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.principal ELSE 0 END),0) AS principal,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.interest ELSE 0 END),0) AS interest,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.penalty ELSE 0 END),0) AS penalty,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.discount ELSE 0 END),0) AS discount,
                       t.amount-COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.principal ELSE 0 END),0) AS remaining
                FROM titles t LEFT JOIN parties p ON p.id=t.partyId LEFT JOIN settlements s ON s.titleId=t.id
                WHERE t.companyId=@p0 AND t.kind='receivable' AND t.deleted=0 AND t.cancelled=0 AND {range}
                GROUP BY t.id ORDER BY t.due,t.id
                """,company,type=="future"?endText:startText,endText)) rows.Add(ReportRow(row,true));
        }
        var title=type switch {"receipts"=>"EXTRATO DE RECEBIMENTOS","by-plan"=>"CONTAS A RECEBER POR PLANO DE CONTAS","month"=>"EXTRATO DE CONTAS A RECEBER",_=>"CONTAS A RECEBER DOS PRÓXIMOS MESES"};
        var groups=type switch {
            "by-plan"=>rows.GroupBy(r=>r.Plan).OrderBy(g=>g.Key).Select(g=>new PayableReportGroup(g.Key,g.ToList())).ToList(),
            "future"=>rows.GroupBy(r=>r.Due[..7]).OrderBy(g=>g.Key).Select(g=>new PayableReportGroup(DateOnly.ParseExact(g.Key+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture).ToString("MMMM 'DE' yyyy",CultureInfo.GetCultureInfo("pt-BR")).ToUpper(CultureInfo.GetCultureInfo("pt-BR")),g.ToList())).ToList(),
            _=>new List<PayableReportGroup>{new(start.ToString("MMMM 'DE' yyyy",CultureInfo.GetCultureInfo("pt-BR")).ToUpper(CultureInfo.GetCultureInfo("pt-BR")),rows)}
        };
        var pdfType=type=="receipts"?"payments":type;
        var data=new PayableReportData((string)companyRow["name"]!,(string)companyRow["cnpj"]!,title,start,"receivable",pdfType,groups);
        var slug=type switch {"receipts"=>"extrato-recebimentos","by-plan"=>"contas-receber-por-plano","month"=>"extrato-contas-receber",_=>"contas-receber-proximos-meses"};
        return new PdfReport(PayableReportsPdf.Generate(data),$"{slug}-{monthValue}.pdf");
    } }
    static PayableReportRow ReportRow(Dictionary<string,object?> row,bool receivable) {
        var principal=Number(row,"principal");var interest=Number(row,"interest");var penalty=Number(row,"penalty");var discount=Number(row,"discount");var remaining=Number(row,"remaining");
        var status=remaining<=0?(receivable?"RECEBIDO":"PAGO"):principal>0?"PARCIAL":string.CompareOrdinal((string)row["due"]!,DateTime.Today.ToString("yyyy-MM-dd"))<0?"VENCIDO":"EM ABERTO";
        return new PayableReportRow(Number(row,"id"),(string)row["due"]!,row["paymentDate"] as string,(string)row["supplier"]!,(string)row["documentNumber"]!,(string)row["description"]!,(string)row["category"]!,Number(row,"amount"),principal,remaining,interest,penalty,discount,principal+interest+penalty-discount,status);
    }
    public object Title(int company,TitleInput x)=>Write(c=> {
        Company(c,company);if(x.Kind!="payable"&&x.Kind!="receivable")throw new ArgumentException("Tipo inválido.");Money(x.Amount,true);var due=Date(x.Due);var description=Required(x.Description,"Descrição");
        var document=(x.DocumentNumber??"").Trim().ToUpperInvariant();var issue=string.IsNullOrWhiteSpace(x.IssueDate)?"":Date(x.IssueDate).ToString("yyyy-MM-dd");
        if(document.Length>0 && Query(c,"SELECT id FROM titles WHERE companyId=@p0 AND deleted=0 AND documentNumber=@p1 AND partyId IS @p2 LIMIT 1",company,document,x.PartyId).Count>0)throw new ArgumentException("Este documento já foi lançado para o mesmo fornecedor ou cliente.");
        var planName=(string.IsNullOrWhiteSpace(x.Category)?(x.Kind=="payable"?"DESPESAS GERAIS":"RECEITAS DE SERVIÇOS"):x.Category.Trim()).ToUpperInvariant();
        var plan=Query(c,"SELECT * FROM plans WHERE companyId=@p0 AND lower(name)=lower(@p1)",company,planName).SingleOrDefault();
        if(plan is null){Run(c,"INSERT INTO plans(companyId,name) VALUES(@p0,@p1)",company,planName);plan=Query(c,"SELECT * FROM plans WHERE id=last_insert_rowid()")[0];}
        if(x.Installments<1||x.Installments>120||x.Amount<x.Installments)throw new ArgumentException("Informe entre 1 e 120 parcelas, com ao menos um centavo por parcela.");
        var custom=x.InstallmentAmounts is not null||x.InstallmentDues is not null;
        if(custom && (x.InstallmentAmounts is null||x.InstallmentDues is null||x.InstallmentAmounts.Length!=x.Installments||x.InstallmentDues.Length!=x.Installments))throw new ArgumentException("Complete o valor e o vencimento de todas as parcelas.");
        if(custom && (x.InstallmentAmounts!.Any(v=>v<=0)||x.InstallmentAmounts!.Sum()!=x.Amount))throw new ArgumentException("A soma das parcelas deve ser igual ao valor total.");
        var customDues=custom?x.InstallmentDues!.Select(Date).ToArray():Array.Empty<DateOnly>();
        if(x.PartyId.HasValue)Existing(c,"parties",x.PartyId.Value,company);
        var group=x.Installments>1 ? Guid.NewGuid().ToString("N") : null;
        for(var i=0;i<x.Installments;i++) Run(c,"INSERT INTO titles(companyId,kind,description,amount,due,partyId,category,notes,installmentGroup,installmentNumber,installmentCount,interestRate,penaltyRate,discountRate,documentNumber,issueDate,branch,costCenter) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17)",company,x.Kind,description+(x.Installments>1?$" ({i+1}/{x.Installments})":""),custom?x.InstallmentAmounts![i]:x.Amount/x.Installments+(i<x.Amount%x.Installments?1:0),(custom?customDues[i]:due.AddMonths(i)).ToString("yyyy-MM-dd"),x.PartyId,planName,(x.Notes??"").ToUpperInvariant(),group,i+1,x.Installments,plan["interestRate"],plan["penaltyRate"],plan["discountRate"],document,issue,RequiredOrEmpty(x.Branch),RequiredOrEmpty(x.CostCenter));
        Audit(c,company,"Lançamento",x);return new {ok=true};
    });
    public object Settle(int company,int id,SettlementInput x)=>Write(c=> {
        Existing(c,"titles",id,company);Existing(c,"accounts",x.AccountId,company);var date=Date(x.Date);if(date>DateOnly.FromDateTime(DateTime.Today))throw new ArgumentException("A baixa não pode ter data futura.");
        Money(x.Principal,true);Money(x.Interest);Money(x.Penalty);Money(x.Discount);
        var title=Query(c,TitlesSql,company).Single(r=>Number(r,"id")==id);
        if(Number(title,"cancelled")!=0||x.Principal>Number(title,"remaining"))throw new ArgumentException("Valor excede o saldo em aberto ou título está cancelado.");
        if(x.Discount>x.Principal)throw new ArgumentException("Desconto não pode exceder o principal da baixa.");
        var cash=checked(x.Principal+x.Interest+x.Penalty-x.Discount);
        Run(c,"INSERT INTO settlements(companyId,titleId,accountId,principal,interest,penalty,discount,date) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)",company,id,x.AccountId,x.Principal,x.Interest,x.Penalty,x.Discount,x.Date);
        var sid=Number(Query(c,"SELECT last_insert_rowid() AS id")[0],"id");
        Run(c,"INSERT INTO movements(companyId,accountId,amount,date,description,settlementId) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)",company,x.AccountId,(string)title["kind"]! == "payable"?-cash:cash,x.Date,title["description"],sid);
        Audit(c,company,"Baixa",new {titleId=id,settlementId=sid,input=x});return new {id=sid};
    });
    public object Reverse(int company,int id,ReasonInput x)=>Write(c=> {
        Existing(c,"settlements",id,company);var reason=Required(x.Reason,"Motivo");var s=Query(c,"SELECT * FROM settlements WHERE id=@p0",id)[0];
        if(Number(s,"reversed")!=0)throw new ArgumentException("Esta baixa já foi estornada.");
        Run(c,"UPDATE settlements SET reversed=1,reason=@p0 WHERE id=@p1",reason,id);
        Run(c,"INSERT INTO movements(companyId,accountId,amount,date,description,settlementId) SELECT companyId,accountId,-amount,@p0,'Estorno: '||description,settlementId FROM movements WHERE settlementId=@p1",DateTime.Today.ToString("yyyy-MM-dd"),id);
        Audit(c,company,"Estorno",new {settlementId=id,reason});return new {ok=true};
    });
    public object Cancel(int company,int id,CancelInput x)=>Write(c=> {
        if(x.Scope!="single" && x.Scope!="all_open")throw new ArgumentException("Escolha cancelar somente esta parcela ou todas as parcelas em aberto.");
        Existing(c,"titles",id,company);var reason=Required(x.Reason,"Motivo");var all=Query(c,TitlesSql,company);var t=all.Single(r=>Number(r,"id")==id);
        if(Number(t,"settled")>0||Number(t,"cancelled")!=0)throw new ArgumentException("Estorne as baixas antes de cancelar. Título já cancelado não pode ser cancelado novamente.");
        var group=t["installmentGroup"] as string;
        if(x.Scope=="all_open" && group is null)throw new ArgumentException("Este lançamento não possui parcelas vinculadas.");
        var targets=x.Scope=="all_open" ? all.Where(r=>(string?)r["installmentGroup"]==group && Number(r,"cancelled")==0 && Number(r,"settled")==0).ToList() : new List<Dictionary<string,object?>>{t};
        foreach(var target in targets) {
            var titleId=Number(target,"id");Run(c,"UPDATE titles SET cancelled=1 WHERE id=@p0",titleId);
            Audit(c,company,"Cancelamento",new {titleId,reason,scope=x.Scope,installmentGroup=group});
        }
        return new {ok=true,cancelledCount=targets.Count};
    });
    public object Edit(int company,int id,EditTitleInput x)=>Write(c=> {
        if(x.Scope!="single" && x.Scope!="all_open")throw new ArgumentException("Escolha editar somente esta parcela ou todas as parcelas sem baixa.");
        Money(x.Amount,true);var due=Date(x.Due);var description=Required(x.Description,"Descrição");
        if(x.PartyId.HasValue)Existing(c,"parties",x.PartyId.Value,company);
        var all=Query(c,TitlesSql,company);var selected=all.SingleOrDefault(r=>Number(r,"id")==id)??throw new ArgumentException("Lançamento não encontrado.");
        if(Number(selected,"cancelled")!=0||Number(selected,"settled")!=0)throw new ArgumentException("Só é possível editar um lançamento sem baixa e que não esteja cancelado.");
        var group=selected["installmentGroup"] as string;
        if(x.Scope=="all_open" && group is null)throw new ArgumentException("Este lançamento não possui parcelas vinculadas.");
        var targets=x.Scope=="all_open" ? all.Where(r=>(string?)r["installmentGroup"]==group && Number(r,"cancelled")==0 && Number(r,"settled")==0).ToList() : new(){selected};
        var selectedNumber=Convert.ToInt32(selected["installmentNumber"]);
        foreach(var target in targets) {
            var number=Convert.ToInt32(target["installmentNumber"]);var count=Convert.ToInt32(target["installmentCount"]);
            var label=description+(count>1?$" ({number}/{count})":"");var targetDue=due.AddMonths(number-selectedNumber).ToString("yyyy-MM-dd");
            Run(c,"UPDATE titles SET description=@p0,amount=@p1,due=@p2,partyId=@p3,category=@p4,notes=@p5,documentNumber=@p6,issueDate=@p7,branch=@p8,costCenter=@p9 WHERE id=@p10",label,x.Amount,targetDue,x.PartyId,(x.Category??"").ToUpperInvariant(),(x.Notes??"").ToUpperInvariant(),RequiredOrEmpty(x.DocumentNumber),string.IsNullOrWhiteSpace(x.IssueDate)?"":Date(x.IssueDate).ToString("yyyy-MM-dd"),RequiredOrEmpty(x.Branch),RequiredOrEmpty(x.CostCenter),Number(target,"id"));
            Audit(c,company,"Edição",new {titleId=Number(target,"id"),description=label,amount=x.Amount,due=targetDue,scope=x.Scope});
        }
        return new {ok=true,editedCount=targets.Count};
    });
    public object Delete(int company,int id,DeleteInput x)=>Write(c=> {
        if(x.Scope!="single" && x.Scope!="all_open")throw new ArgumentException("Escolha excluir somente esta parcela ou todas as parcelas sem baixa.");
        var reason=Required(x.Reason,"Motivo");var all=Query(c,TitlesSql,company);var selected=all.SingleOrDefault(r=>Number(r,"id")==id)??throw new ArgumentException("Lançamento não encontrado.");
        if(Query(c,"SELECT id FROM settlements WHERE titleId=@p0 AND reversed=0 LIMIT 1",id).Count>0)throw new ArgumentException("Este lançamento ainda possui uma baixa ativa. Estorne todas as baixas antes de excluir.");
        var group=selected["installmentGroup"] as string;
        if(x.Scope=="all_open" && group is null)throw new ArgumentException("Este lançamento não possui parcelas vinculadas.");
        var targets=x.Scope=="all_open" ? all.Where(r=>(string?)r["installmentGroup"]==group && Query(c,"SELECT id FROM settlements WHERE titleId=@p0 AND reversed=0 LIMIT 1",Number(r,"id")).Count==0).ToList() : new(){selected};
        foreach(var target in targets) {
            var titleId=Number(target,"id");var description=(string)target["description"]!;
            Run(c,"UPDATE titles SET deleted=1 WHERE id=@p0",titleId);
            Audit(c,company,"Exclusão",new {titleId,description,reason,scope=x.Scope,installmentGroup=group});
        }
        return new {ok=true,deletedCount=targets.Count};
    });
    public object Transfer(int company,TransferInput x)=>Write(c=> {
        Existing(c,"accounts",x.FromId,company);Existing(c,"accounts",x.ToId,company);Money(x.Amount,true);var date=Date(x.Date);if(date>DateOnly.FromDateTime(DateTime.Today))throw new ArgumentException("Transferência não pode ter data futura.");if(x.FromId==x.ToId)throw new ArgumentException("Selecione contas diferentes.");
        Run(c,"INSERT INTO movements(companyId,accountId,amount,date,description) VALUES(@p0,@p1,@p2,@p3,'Transferência entre contas'),(@p0,@p4,@p5,@p3,'Transferência entre contas')",company,x.FromId,-x.Amount,x.Date,x.ToId,x.Amount);Audit(c,company,"Transferência",x);return new {ok=true};
    });
    public object ResetSupermarketDemo()=>Write(c=> {
        Run(c,"DELETE FROM movements; DELETE FROM settlements; DELETE FROM audit; DELETE FROM titles; DELETE FROM parties; DELETE FROM accounts; DELETE FROM plans; DELETE FROM user_companies; DELETE FROM companies;");
        Run(c,"INSERT INTO companies(id,name,cnpj) VALUES(1,'SUPERMERCADO MODELO MONALISA LTDA','12345678000195')");
        Run(c,"INSERT INTO accounts(id,companyId,name,opening) VALUES(1,1,'CAIXA LOJA',350000),(2,1,'BANCO ITAÚ',2500000),(3,1,'RECEBIMENTOS DE CARTÕES',0)");
        void AddPlan(string name,decimal interest=0,decimal penalty=0,decimal discount=0)=>Run(c,"INSERT INTO plans(companyId,name,interestRate,penaltyRate,discountRate) VALUES(1,@p0,@p1,@p2,@p3)",name,interest,penalty,discount);
        AddPlan("COMPRA DE MERCADORIAS",.1m,2m);AddPlan("HORTIFRÚTI",.1m,2m);AddPlan("AÇOUGUE",.1m,2m);AddPlan("PADARIA",.1m,2m);AddPlan("BEBIDAS",.1m,2m);AddPlan("MATERIAL DE LIMPEZA");AddPlan("ENERGIA ELÉTRICA");AddPlan("ALUGUEL");AddPlan("FOLHA DE PAGAMENTO");AddPlan("IMPOSTOS E TAXAS");AddPlan("MANUTENÇÃO");AddPlan("SEGURANÇA");AddPlan("FRETES");AddPlan("PERDAS E AVARIAS");AddPlan("TAXAS DE CARTÃO");AddPlan("VENDAS EM DINHEIRO");AddPlan("VENDAS POR PIX");AddPlan("CARTÃO DE DÉBITO");AddPlan("CARTÃO DE CRÉDITO");AddPlan("VALE-ALIMENTAÇÃO");AddPlan("CONVÊNIOS");
        long AddParty(string name,string document,string type,string phone,string email){Run(c,"INSERT INTO parties(companyId,name,document,type,phone,email) VALUES(1,@p0,@p1,@p2,@p3,@p4)",name,document,type,phone,email);return Number(Query(c,"SELECT last_insert_rowid() AS id")[0],"id");}
        var atacado=AddParty("ATACADO BOM PREÇO LTDA","11222333000181","supplier","11940001001","FINANCEIRO@BOMPRECO.EXEMPLO");
        var frigorifico=AddParty("FRIGORÍFICO BOI FORTE LTDA","22333444000162","supplier","11940001002","COBRANCA@BOIFORTE.EXEMPLO");
        var hortifruti=AddParty("HORTIFRÚTI CAMPO VERDE LTDA","33444555000143","supplier","11940001003","PEDIDOS@CAMPOVERDE.EXEMPLO");
        var energia=AddParty("ENERGIA REGIONAL","44555666000124","supplier","08000001004","ATENDIMENTO@ENERGIA.EXEMPLO");
        var imovel=AddParty("ADMINISTRADORA PRAÇA CENTRAL LTDA","55666777000105","supplier","11940001005","ALUGUEL@PRACACENTRAL.EXEMPLO");
        var adquirente=AddParty("OPERADORA DE CARTÕES MODELO","66777888000196","customer","1130001000","RECEBIVEIS@CARTOES.EXEMPLO");
        var vale=AddParty("VALE ALIMENTAÇÃO BRASIL","77888999000177","customer","1130002000","REPASSE@VALE.EXEMPLO");
        var convenio=AddParty("RESTAURANTE SABOR CASEIRO LTDA","88999000000158","customer","11940001006","FINANCEIRO@SABORCASEIRO.EXEMPLO");
        var today=DateOnly.FromDateTime(DateTime.Today);var first=new DateOnly(today.Year,today.Month,1);var next=first.AddMonths(1);
        long AddTitle(string kind,string description,long amount,DateOnly due,long? party,string plan,string document,DateOnly issue,string branch,string cost,string notes="",string? group=null,int number=1,int count=1){
            Run(c,"INSERT INTO titles(companyId,kind,description,amount,due,partyId,category,notes,installmentGroup,installmentNumber,installmentCount,interestRate,penaltyRate,discountRate,documentNumber,issueDate,branch,costCenter) SELECT 1,@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,interestRate,penaltyRate,discountRate,@p10,@p11,@p12,@p13 FROM plans WHERE companyId=1 AND name=@p5",kind,description,amount,due.ToString("yyyy-MM-dd"),party,plan,notes,group,number,count,document,issue.ToString("yyyy-MM-dd"),branch,cost);return Number(Query(c,"SELECT last_insert_rowid() AS id")[0],"id");
        }
        long Pay(long title,long account,long principal,DateOnly date,long interest=0,long penalty=0,long discount=0){
            Run(c,"INSERT INTO settlements(companyId,titleId,accountId,principal,interest,penalty,discount,date) VALUES(1,@p0,@p1,@p2,@p3,@p4,@p5,@p6)",title,account,principal,interest,penalty,discount,date.ToString("yyyy-MM-dd"));var id=Number(Query(c,"SELECT last_insert_rowid() AS id")[0],"id");var kind=(string)Query(c,"SELECT kind FROM titles WHERE id=@p0",title)[0]["kind"]!;var cash=principal+interest+penalty-discount;Run(c,"INSERT INTO movements(companyId,accountId,amount,date,description,settlementId) SELECT 1,@p0,@p1,@p2,description,@p3 FROM titles WHERE id=@p4",account,kind=="payable"?-cash:cash,date.ToString("yyyy-MM-dd"),id,title);return id;
        }
        var compra=AddTitle("payable","COMPRA SEMANAL DE MERCADORIAS",1245000,today.AddDays(2),atacado,"COMPRA DE MERCADORIAS","NF 45872",today.AddDays(-10),"LOJA MATRIZ","ESTOQUE","ENTREGA CONFERIDA PELO RECEBIMENTO");
        var carnes=AddTitle("payable","COMPRA DE CARNES E CONGELADOS",897000,today.AddDays(-3),frigorifico,"AÇOUGUE","NF 18743",today.AddDays(-12),"LOJA MATRIZ","AÇOUGUE");
        AddTitle("payable","FORNECIMENTO DE ENERGIA",324000,today.AddDays(5),energia,"ENERGIA ELÉTRICA","FAT 09/2026",first,"LOJA MATRIZ","ADMINISTRAÇÃO");
        var aluguel=AddTitle("payable","ALUGUEL DA LOJA",1500000,first.AddDays(9),imovel,"ALUGUEL","REC 09/2026",first,"LOJA MATRIZ","ADMINISTRAÇÃO");var baixaAluguel=Pay(aluguel,2,1500000,first.AddDays(8),discount:5000);
        var manutencao=AddTitle("payable","MANUTENÇÃO DA CÂMARA FRIA",245000,first.AddDays(14),frigorifico,"MANUTENÇÃO","OS 9021",first.AddDays(2),"LOJA MATRIZ","MANUTENÇÃO");var baixaManutencao=Pay(manutencao,2,245000,first.AddDays(16),interest:1000,penalty:2000,discount:500);
        var equipmentGroup=Guid.NewGuid().ToString("N");for(var i=0;i<3;i++)AddTitle("payable",$"EQUIPAMENTO PARA PADARIA ({i+1}/3)",360000,next.AddMonths(i).AddDays(9),atacado,"PADARIA",$"NF 46010-{i+1}",today,"LOJA MATRIZ","PADARIA","PARCELAMENTO DO NOVO FORNO",equipmentGroup,i+1,3);
        AddTitle("payable","MATERIAL DE LIMPEZA",128000,next.AddDays(4),atacado,"MATERIAL DE LIMPEZA","NF 45901",today.AddDays(-1),"LOJA MATRIZ","ESTOQUE");
        var debit=AddTitle("receivable","VENDAS NO CARTÃO DE DÉBITO",1845000,today.AddDays(-1),adquirente,"CARTÃO DE DÉBITO","LOTE DÉBITO 2309",today.AddDays(-2),"LOJA MATRIZ","FRENTE DE CAIXA");var baixaDebito=Pay(debit,3,1845000,today.AddDays(-1),discount:33210);
        AddTitle("receivable","VENDAS NO CARTÃO DE CRÉDITO",3290000,next.AddDays(7),adquirente,"CARTÃO DE CRÉDITO","LOTE CRÉDITO 2309",today,"LOJA MATRIZ","FRENTE DE CAIXA");
        var pix=AddTitle("receivable","VENDAS RECEBIDAS POR PIX",780000,today,null,"VENDAS POR PIX","MOV PIX 2309",today,"LOJA MATRIZ","FRENTE DE CAIXA");var baixaPix=Pay(pix,2,780000,today);
        AddTitle("receivable","REPASSE DE VALE-ALIMENTAÇÃO",1240000,first.AddMonths(1).AddDays(-2),vale,"VALE-ALIMENTAÇÃO","LOTE VALE 2309",today,"LOJA MATRIZ","FRENTE DE CAIXA");
        AddTitle("receivable","CONVÊNIO DE COMPRAS CORPORATIVAS",560000,next.AddDays(14),convenio,"CONVÊNIOS","FAT CONV 0926",today,"LOJA MATRIZ","ADMINISTRAÇÃO");
        Audit(c,1,"Lançamento",new{Kind="payable",Description="COMPRA SEMANAL DE MERCADORIAS",Amount=1245000,Due=today.AddDays(2).ToString("yyyy-MM-dd"),titleId=compra});
        Audit(c,1,"Lançamento",new{Kind="payable",Description="COMPRA DE CARNES E CONGELADOS",Amount=897000,Due=today.AddDays(-3).ToString("yyyy-MM-dd"),titleId=carnes});
        Audit(c,1,"Baixa",new{titleId=aluguel,settlementId=baixaAluguel,input=new{AccountId=2,Principal=1500000,Interest=0,Penalty=0,Discount=5000,Date=first.AddDays(8).ToString("yyyy-MM-dd")}});
        Audit(c,1,"Baixa",new{titleId=manutencao,settlementId=baixaManutencao,input=new{AccountId=2,Principal=245000,Interest=1000,Penalty=2000,Discount=500,Date=first.AddDays(16).ToString("yyyy-MM-dd")}});
        Audit(c,1,"Baixa",new{titleId=debit,settlementId=baixaDebito,input=new{AccountId=3,Principal=1845000,Interest=0,Penalty=0,Discount=33210,Date=today.AddDays(-1).ToString("yyyy-MM-dd")}});
        Audit(c,1,"Baixa",new{titleId=pix,settlementId=baixaPix,input=new{AccountId=2,Principal=780000,Interest=0,Penalty=0,Discount=0,Date=today.ToString("yyyy-MM-dd")}});
        Audit(c,1,"Base de demonstração",new{segmento="SUPERMERCADO",geradoEm=DateTime.Now});
        return new{ok=true,companyId=1};
    });
    public string Backup() {lock(gate){var folder=Path.Combine(Path.GetDirectoryName(path)!,"backups");Directory.CreateDirectory(folder);var target=Path.Combine(folder,$"financeiro-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");using var src=Open();using var dest=new SqliteConnection($"Data Source={target}");dest.Open();src.BackupDatabase(dest);return target;}}
}

using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("FINANCE_URL") ?? "http://127.0.0.1:5187");
builder.WebHost.ConfigureKestrel(options=>options.Limits.MaxRequestBodySize=700L*1024*1024);
builder.Services.AddSingleton<Ledger>();
builder.Services.AddSingleton<AuthSessions>();
builder.Services.AddSingleton<LicenseGuard>();
var app = builder.Build();
var networkRole=(Environment.GetEnvironmentVariable("MONALISA_NETWORK_ROLE")??"standalone").Trim().ToLowerInvariant();
var dataServerUrl=(Environment.GetEnvironmentVariable("MONALISA_DATA_SERVER_URL")??"").Trim().TrimEnd('/');
var networkKey=(Environment.GetEnvironmentVariable("MONALISA_NETWORK_KEY")??"").Trim();
var sharedPort=int.TryParse(Environment.GetEnvironmentVariable("MONALISA_NETWORK_SHARED_PORT"),out var configuredSharedPort)?configuredSharedPort:5188;
var clientMode=networkRole=="client"&&Uri.TryCreate(dataServerUrl,UriKind.Absolute,out _)&&networkKey.Length>=24;
var serverMode=networkRole=="server"&&networkKey.Length>=24;
var proxyClient=new HttpClient(new SocketsHttpHandler { AllowAutoRedirect=false,UseCookies=false }) { Timeout=TimeSpan.FromMinutes(12) };
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (ctx, next) => {
    var origin = ctx.Request.Headers.Origin.ToString();
    var localOrigin=$"{ctx.Request.Scheme}://{ctx.Request.Host}";
    if (origin.Length > 0 && !string.Equals(origin,localOrigin,StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 403; return; }
    try { await next(); }
    catch (ArgumentException ex) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = "Registro duplicado ou referência inválida." }); }
    catch (HttpRequestException) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsJsonAsync(new { error = "Não foi possível acessar o Painel Central. Verifique se este computador está na mesma rede e se o endereço do painel está correto." }); }
    catch (TaskCanceledException) { ctx.Response.StatusCode = 504; await ctx.Response.WriteAsJsonAsync(new { error = "O Painel Central demorou para responder. Verifique a conexão de rede e tente novamente." }); }
    catch (Exception ex) { app.Logger.LogError(ex,"Erro não tratado em {Method} {Path}",ctx.Request.Method,ctx.Request.Path);ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { error = "O serviço local encontrou um erro ao concluir a operação. Feche e abra o Monalisa Financeiro e tente novamente." }); }
});
app.Use(async(ctx,next)=>{
    if(!serverMode||ctx.Connection.LocalPort!=sharedPort||!ctx.Request.Path.StartsWithSegments("/api")){await next();return;}
    var supplied=ctx.Request.Headers["X-Monalisa-Network-Key"].ToString();
    if(!FixedTimeTextEquals(supplied,networkKey)){ctx.Response.StatusCode=401;await ctx.Response.WriteAsJsonAsync(new{error="ESTA MÁQUINA NÃO ESTÁ AUTORIZADA A USAR OS DADOS COMPARTILHADOS."});return;}
    await next();
});
app.Use(async (ctx, next) => {
    if(!ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/api/health") ||
       ctx.Request.Path.StartsWithSegments("/api/license") || ctx.Request.Path.StartsWithSegments("/api/auth/bootstrap") || ctx.Request.Path.StartsWithSegments("/api/auth/login")) { await next(); return; }
    if(clientMode){await next();return;}
    var db=ctx.RequestServices.GetRequiredService<Ledger>();
    if(Environment.GetEnvironmentVariable("FINANCE_AUTH_DISABLED")=="1") { await next(); return; }
    var needsSetup=!db.HasUsers();
    var sessions=ctx.RequestServices.GetRequiredService<AuthSessions>();
    var header=ctx.Request.Headers.Authorization.ToString();
    var token=header.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)?header[7..].Trim():"";
    if(!sessions.TryGet(token,out var sessionIdentity)) {
        ctx.Response.StatusCode=needsSetup?428:401;await ctx.Response.WriteAsJsonAsync(new{error=needsSetup?"Crie o administrador responsável por esta instalação.":"Faça login para continuar."});return;
    }
    var identity=db.SessionUser(sessionIdentity.Id);
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
    if(ctx.Request.Method=="POST" && ctx.Request.Path=="/api/backup" && !db.HasAdministrativeAccess(identity)) {
        ctx.Response.StatusCode=403;await ctx.Response.WriteAsJsonAsync(new{error="O usuário financeiro não possui permissão para criar backup."});return;
    }
    if(match.Success && int.TryParse(match.Groups[1].Value,out var protectedCompanyId) && db.IsFinancialUser(identity,protectedCompanyId)) {
        var path=ctx.Request.Path.Value??"";
        if(ctx.Request.Method=="POST" && Regex.IsMatch(path,@"^/api/companies/\d+/cost-centers(?:/|$)")) {
            ctx.Response.StatusCode=403;await ctx.Response.WriteAsJsonAsync(new{error="O usuário financeiro não possui permissão para administrar centros de custo."});return;
        }
        if(ctx.Request.Method=="POST" && Regex.IsMatch(path,@"^/api/companies/\d+/plans(?:/|$)") && !db.CanManagePlanRates(identity,protectedCompanyId)) {
            ctx.Response.StatusCode=403;await ctx.Response.WriteAsJsonAsync(new{error="O administrador da empresa bloqueou a alteração de juros, multa e desconto dos planos de contas."});return;
        }
    }
    await next();
});
app.Use(async(ctx,next)=>{
    if(!ctx.Request.Path.StartsWithSegments("/api")||ctx.Request.Path.StartsWithSegments("/api/health")||ctx.Request.Path.StartsWithSegments("/api/license")||ctx.Request.Path.StartsWithSegments("/api/auth")){await next();return;}
    var license=await ctx.RequestServices.GetRequiredService<LicenseGuard>().Status();
    if(!clientMode)ctx.RequestServices.GetRequiredService<Ledger>().ApplyLicensedCompany(license);
    if(license.Status=="blocked"||license.Status=="activation_required"){ctx.Response.StatusCode=423;await ctx.Response.WriteAsJsonAsync(new{error=license.Message,code="LICENSE_BLOCKED",license});return;}
    var allowedReadOnly=ctx.Request.Path.StartsWithSegments("/api/auth")||ctx.Request.Path=="/api/backup";
    if(license.Status=="read_only"&&ctx.Request.Method!="GET"&&!allowedReadOnly){ctx.Response.StatusCode=423;await ctx.Response.WriteAsJsonAsync(new{error=license.Message.Length>0?license.Message:"Licença em modo somente consulta.",code="LICENSE_READ_ONLY",license});return;}
    ctx.Items["license"]=license;await next();
});
app.Use(async(ctx,next)=>{
    if(!clientMode||!ctx.Request.Path.StartsWithSegments("/api")||ctx.Request.Path.StartsWithSegments("/api/health")||ctx.Request.Path.StartsWithSegments("/api/license")){await next();return;}
    await ProxyToDataServer(ctx,dataServerUrl,networkKey,proxyClient);
});
app.MapGet("/api/health", () => new { status = "ok", mode = networkRole, sharedData = clientMode||serverMode, dataServer = clientMode?dataServerUrl:"" });
app.MapGet("/api/license/status", async (LicenseGuard license,Ledger db) => {var result=await license.Status();db.ApplyLicensedCompany(result);return result;});
app.MapPost("/api/license/activate", async (LicenseActivationInput x,LicenseGuard license,Ledger db) => {var result=await license.Activate(x.ActivationKey);db.ApplyLicensedCompany(result);return result;});
app.MapPost("/api/license/change", async (LicenseActivationInput x,LicenseGuard license,Ledger db) => {var result=await license.Change(x.ActivationKey);db.ApplyLicensedCompany(result);return result;});
app.MapPost("/api/license/refresh", async (LicenseGuard license,Ledger db) => {var result=await license.Status(true);db.ApplyLicensedCompany(result);return result;});
app.MapGet("/api/auth/bootstrap", (Ledger db) => new { needsSetup=!db.HasUsers() });
app.MapPost("/api/auth/bootstrap", (BootstrapInput x, Ledger db, AuthSessions sessions) => {
    var user=db.SetupAdministrator(x);var token=sessions.Create(user);return new {token,user=db.AccessContext(user)};
});
app.MapPost("/api/auth/login", (LoginInput x, Ledger db, AuthSessions sessions) => {
    var user=db.Authenticate(x);
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
app.MapGet("/api/companies/{companyId:int}/reports/advanced", (int companyId,string type,string start,string end,int? partyId,int? accountId,HttpContext context,Ledger db) => {var report=db.AdvancedReport(companyId,type,start,end,partyId,accountId);context.Response.Headers.ContentDisposition=$"inline; filename=\"{report.FileName}\"";return Results.File(report.Content,"application/pdf");});
app.MapGet("/api/companies/{companyId:int}/exports/csv", (int companyId,string type,string start,string end,int? partyId,int? accountId,HttpContext context,Ledger db) => {var file=db.ExportCsv(companyId,type,start,end,partyId,accountId);context.Response.Headers.ContentDisposition=$"attachment; filename=\"{file.FileName}\"";return Results.File(file.Content,"text/csv; charset=utf-8");});
app.MapPost("/api/companies/{companyId:int}/parties", (int companyId, PartyInput x, Ledger db) => db.Party(companyId,x));
app.MapPost("/api/companies/{companyId:int}/parties/{id:int}/edit", (int companyId,int id,PartyInput x,Ledger db) => db.EditParty(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/parties/{id:int}/delete", (int companyId,int id,Ledger db) => db.DeleteParty(companyId,id));
app.MapPost("/api/companies/{companyId:int}/accounts", (int companyId, AccountInput x, Ledger db) => db.Account(companyId,x));
app.MapPost("/api/companies/{companyId:int}/accounts/{id:int}/edit", (int companyId,int id,AccountInput x,Ledger db) => db.EditAccount(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/accounts/{id:int}/delete", (int companyId,int id,Ledger db) => db.DeleteAccount(companyId,id));
app.MapPost("/api/companies/{companyId:int}/accounts/{id:int}/adjust", (int companyId,int id,AdjustmentInput x,Ledger db) => db.Adjust(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/plans", (int companyId, PlanInput x, Ledger db) => db.Plan(companyId,x));
app.MapPost("/api/companies/{companyId:int}/plans/{id:int}/edit", (int companyId,int id,PlanInput x,Ledger db) => db.EditPlan(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/plans/{id:int}/delete", (int companyId,int id,Ledger db) => db.DeletePlan(companyId,id));
app.MapPost("/api/companies/{companyId:int}/payment-methods", (int companyId, NamedKindInput x, Ledger db) => db.PaymentMethod(companyId,x));
app.MapPost("/api/companies/{companyId:int}/payment-methods/{id:int}/edit", (int companyId,int id,NamedKindInput x,Ledger db) => db.EditPaymentMethod(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/payment-methods/{id:int}/delete", (int companyId,int id,Ledger db) => db.DeletePaymentMethod(companyId,id));
app.MapPost("/api/companies/{companyId:int}/cost-centers", (int companyId,NamedInput x,Ledger db) => db.CostCenter(companyId,x));
app.MapPost("/api/companies/{companyId:int}/cost-centers/{id:int}/edit", (int companyId,int id,NamedInput x,Ledger db) => db.EditCostCenter(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/cost-centers/{id:int}/delete", (int companyId,int id,Ledger db) => db.DeleteCostCenter(companyId,id));
app.MapPost("/api/companies/{companyId:int}/titles", (int companyId, TitleInput x,HttpContext context, Ledger db) => db.Title(companyId,x,context.Items["user"] as AuthIdentity));
app.MapPost("/api/companies/{companyId:int}/titles/{id:int}/duplicate", (int companyId,int id,DuplicateInput x,Ledger db) => db.Duplicate(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/titles/{id:int}/settle", (int companyId, int id, SettlementInput x, Ledger db) => db.Settle(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/settlements/bulk", (int companyId,BulkSettlementInput x,Ledger db) => db.BulkSettle(companyId,x));
app.MapPost("/api/companies/{companyId:int}/settlements/{id:int}/reverse", (int companyId, int id, ReasonInput x, Ledger db) => db.Reverse(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/ofx/preview", (int companyId,OfxPreviewInput x,Ledger db) => db.PreviewOfx(companyId,x));
app.MapPost("/api/companies/{companyId:int}/ofx/confirm", (int companyId,OfxConfirmInput x,Ledger db) => db.ConfirmOfx(companyId,x));
app.MapPost("/api/companies/{companyId:int}/titles/{id:int}/cancel", (int companyId,int id,CancelInput x,Ledger db) => db.Cancel(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/titles/{id:int}/edit", (int companyId,int id,EditTitleInput x,HttpContext context,Ledger db) => db.Edit(companyId,id,x,context.Items["user"] as AuthIdentity));
app.MapPost("/api/companies/{companyId:int}/titles/{id:int}/delete", (int companyId,int id,DeleteInput x,Ledger db) => db.Delete(companyId,id,x));
app.MapPost("/api/companies/{companyId:int}/transfers", (int companyId, TransferInput x, Ledger db) => db.Transfer(companyId,x));
app.MapPost("/api/companies/{companyId:int}/attachments", (int companyId,AttachmentInput x,Ledger db) => db.AddAttachment(companyId,x));
app.MapGet("/api/companies/{companyId:int}/attachments/{id:int}", (int companyId,int id,Ledger db) => {var file=db.Attachment(companyId,id);return Results.File(file.Content,file.MimeType,file.Name);});
app.MapPost("/api/companies/{companyId:int}/attachments/{id:int}/delete", (int companyId,int id,Ledger db) => db.DeleteAttachment(companyId,id));
app.MapPost("/api/backup", (Ledger db) => new { path = db.Backup() });
app.MapPost("/api/restore", (RestoreInput x,HttpContext context,Ledger db) => db.Restore(x,CurrentUser(context)));
app.MapPost("/api/import/gestores/prepare", (GestoresImportInput x,HttpContext context,Ledger db) => db.PrepareGestoresImport(x,CurrentUser(context)));
app.MapPost("/api/import/gestores/commit", (GestoresImportCommitInput x,HttpContext context,Ledger db) => db.CommitGestoresImport(x,CurrentUser(context)));
app.Services.GetRequiredService<Ledger>();
app.MapFallbackToFile("index.html");
app.Run();

static bool FixedTimeTextEquals(string supplied,string expected) {
    var left=Encoding.UTF8.GetBytes(supplied);var right=Encoding.UTF8.GetBytes(expected);
    return left.Length==right.Length&&CryptographicOperations.FixedTimeEquals(left,right);
}

static async Task ProxyToDataServer(HttpContext context,string serverUrl,string networkKey,HttpClient client) {
    var target=serverUrl+context.Request.Path+context.Request.QueryString;
    using var request=new HttpRequestMessage(new HttpMethod(context.Request.Method),target);
    var hasBody=context.Request.ContentLength>0||context.Request.Headers.TransferEncoding.Count>0;
    if(hasBody)request.Content=new StreamContent(context.Request.Body);
    foreach(var header in context.Request.Headers) {
        if(header.Key.Equals("Host",StringComparison.OrdinalIgnoreCase)||header.Key.Equals("Content-Length",StringComparison.OrdinalIgnoreCase)||header.Key.Equals("Transfer-Encoding",StringComparison.OrdinalIgnoreCase)||header.Key.Equals("Connection",StringComparison.OrdinalIgnoreCase)||header.Key.Equals("X-Monalisa-Network-Key",StringComparison.OrdinalIgnoreCase))continue;
        if(!request.Headers.TryAddWithoutValidation(header.Key,header.Value.ToArray())&&request.Content is not null)request.Content.Headers.TryAddWithoutValidation(header.Key,header.Value.ToArray());
    }
    request.Headers.TryAddWithoutValidation("X-Monalisa-Network-Key",networkKey);
    try {
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,context.RequestAborted);
        context.Response.StatusCode=(int)response.StatusCode;
        foreach(var header in response.Headers)if(!header.Key.Equals("Transfer-Encoding",StringComparison.OrdinalIgnoreCase)&&!header.Key.Equals("Connection",StringComparison.OrdinalIgnoreCase))context.Response.Headers[header.Key]=header.Value.ToArray();
        foreach(var header in response.Content.Headers)if(!header.Key.Equals("Transfer-Encoding",StringComparison.OrdinalIgnoreCase)&&!header.Key.Equals("Connection",StringComparison.OrdinalIgnoreCase))context.Response.Headers[header.Key]=header.Value.ToArray();
        await response.Content.CopyToAsync(context.Response.Body,context.RequestAborted);
    } catch(OperationCanceledException) when(!context.RequestAborted.IsCancellationRequested) {
        context.Response.Clear();context.Response.StatusCode=504;await context.Response.WriteAsJsonAsync(new{error="A MÁQUINA PRINCIPAL DEMOROU PARA RESPONDER. VERIFIQUE SE ELA ESTÁ LIGADA E CONECTADA À REDE."});
    } catch(HttpRequestException) {
        context.Response.Clear();context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{error="NÃO FOI POSSÍVEL CONECTAR À MÁQUINA PRINCIPAL. VERIFIQUE SE ELA ESTÁ LIGADA E CONECTADA À REDE."});
    }
}

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
    public string Create(AuthIdentity identity) { lock(gate) { Cleanup();var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));sessions[token]=(identity,DateTime.Now.AddHours(12));return token; } }
    public bool TryGet(string token,out AuthIdentity identity) { lock(gate) { Cleanup();if(token.Length>0&&sessions.TryGetValue(token,out var session)){sessions[token]=(session.Identity,DateTime.Now.AddHours(12));identity=session.Identity;return true;}identity=null!;return false; } }
    public void Remove(string token) { lock(gate) sessions.Remove(token); }
    void Cleanup(){foreach(var token in sessions.Where(item=>item.Value.Expires<=DateTime.Now).Select(item=>item.Key).ToList())sessions.Remove(token);}
}

record CompanyInput(string Name, string Cnpj);
record BootstrapInput(string Name,string Username,string Pin);
record LoginInput(string Username,string Pin);
record UserAccessInput(string Name,string Username,string Pin,string Role="admin",bool CanManagePlanRates=false);
record LinkUserInput(string Role="admin",bool CanManagePlanRates=false);
record AccessUpdateInput(string Role="admin",bool Active=true,bool CanManagePlanRates=false);
record PartyInput(string Name,string Document,string Type,string Phone="",string Email="",string PersonType="");
record AccountInput(string Name,long Opening,string Type="cash");
record PlanInput(string Name,decimal InterestRate=0,decimal PenaltyRate=0,decimal DiscountRate=0);
record NamedInput(string Name);
record NamedKindInput(string Name,string Kind="both");
record TitleInput(string Kind,string Description,long Amount,string Due,int? PartyId,string Category,string Notes,int Installments = 1,decimal InterestRate = 0,decimal PenaltyRate = 0,decimal DiscountRate = 0,long[]? InstallmentAmounts = null,string[]? InstallmentDues = null,string DocumentNumber = "",string IssueDate = "",string Branch = "",string CostCenter = "",int InstallmentIntervalDays = 0,int? PaymentMethodId=null);
record SettlementInput(int AccountId,long Principal,long Interest,long Penalty,long Discount,string Date,long Additional=0,int? PaymentMethodId=null);
record BulkSettlementItem(int TitleId,long Principal,long Interest=0,long Penalty=0,long Discount=0,long Additional=0);
record BulkSettlementInput(int AccountId,string Date,BulkSettlementItem[] Items,int? PaymentMethodId=null);
record OfxPreviewInput(int AccountId,string FileName,string DataBase64);
record OfxConfirmItem(long TransactionId,int TitleId);
record OfxConfirmInput(long ImportId,OfxConfirmItem[] Items);
record ParsedOfxTransaction(string FitId,string Date,long Amount,string Memo,string Type);
record ParsedOfx(string BankId,string Start,string End,List<ParsedOfxTransaction> Transactions);
record ReasonInput(string Reason);
record CancelInput(string Reason, string Scope = "single");
record EditTitleInput(string Description,long Amount,string Due,int? PartyId,string Category,string Notes,string Scope = "single",string DocumentNumber = "",string IssueDate = "",string Branch = "",string CostCenter = "",int? PaymentMethodId=null);
record DeleteInput(string Reason, string Scope = "single");
record TransferInput(int FromId,int ToId,long Amount,string Date);
record AdjustmentInput(long Amount,string Date,string Reason);
record DuplicateInput(string Due="");
record AttachmentInput(int? TitleId,int? SettlementId,string Name,string MimeType,string DataBase64);
record RestoreInput(string Name,string DataBase64);
record AttachmentFile(byte[] Content,string MimeType,string Name);
record DownloadFile(byte[] Content,string FileName);

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
            Run(c,"INSERT INTO companies(name,cnpj) VALUES('EMPRESA AGUARDANDO ATIVAÇÃO','LICENCA-PENDENTE')");
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
        currentVersion=Convert.ToInt32(Query(c,"PRAGMA user_version")[0]["user_version"]);
        if(currentVersion<11) {
            if(Query(c,"SELECT id FROM companies LIMIT 1").Count>0)Backup();
            Run(c,"""
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
        if (!Query(c,"PRAGMA table_info(parties)").Any(r => (string)r["name"]! == "personType"))
            Run(c,"ALTER TABLE parties ADD COLUMN personType TEXT NOT NULL DEFAULT ''");
        if (!Query(c,"PRAGMA table_info(accounts)").Any(r => (string)r["name"]! == "type"))
            Run(c,"ALTER TABLE accounts ADD COLUMN type TEXT NOT NULL DEFAULT 'cash'");
        if (!Query(c,"PRAGMA table_info(titles)").Any(r => (string)r["name"]! == "paymentMethodId"))
            Run(c,"ALTER TABLE titles ADD COLUMN paymentMethodId INTEGER REFERENCES payment_methods(id)");
        if (!Query(c,"PRAGMA table_info(settlements)").Any(r => (string)r["name"]! == "additional"))
            Run(c,"ALTER TABLE settlements ADD COLUMN additional INTEGER NOT NULL DEFAULT 0");
        if (!Query(c,"PRAGMA table_info(user_companies)").Any(r => (string)r["name"]! == "canManagePlanRates")) {
            if(Query(c,"SELECT id FROM companies LIMIT 1").Count>0)Backup();
            Run(c,"ALTER TABLE user_companies ADD COLUMN canManagePlanRates INTEGER NOT NULL DEFAULT 0; PRAGMA user_version=12");
        }
        if (!Query(c,"PRAGMA table_info(settlements)").Any(r => (string)r["name"]! == "paymentMethodId"))
            Run(c,"ALTER TABLE settlements ADD COLUMN paymentMethodId INTEGER REFERENCES payment_methods(id)");
        currentVersion=Convert.ToInt32(Query(c,"PRAGMA user_version")[0]["user_version"]);
        if(currentVersion<13) {
            if(Query(c,"SELECT id FROM companies LIMIT 1").Count>0)Backup();
            Run(c,"""
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
        foreach(var company in Query(c,"SELECT id FROM companies")) SeedAuxiliaries(c,Convert.ToInt32(company["id"]));
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
    static void SeedAuxiliaries(SqliteConnection c,int company) {
        foreach(var item in new[]{("DINHEIRO","both"),("PIX","both"),("BOLETO","both"),("TRANSFERÊNCIA BANCÁRIA","both"),("CARTÃO DE DÉBITO","receivable"),("CARTÃO DE CRÉDITO","receivable")})
            if(Query(c,"SELECT id FROM payment_methods WHERE companyId=@p0 AND upper(name)=upper(@p1)",company,item.Item1).Count==0)Run(c,"INSERT INTO payment_methods(companyId,name,kind) VALUES(@p0,@p1,@p2)",company,item.Item1,item.Item2);
        foreach(var name in new[]{"ADMINISTRAÇÃO","ESTOQUE","AÇOUGUE","HORTIFRÚTI","PADARIA","FRENTE DE CAIXA","MANUTENÇÃO"})
            if(Query(c,"SELECT id FROM cost_centers WHERE companyId=@p0 AND upper(name)=upper(@p1)",company,name).Count==0)Run(c,"INSERT INTO cost_centers(companyId,name) VALUES(@p0,@p1)",company,name);
    }
    object Write(Func<SqliteConnection,object> operation) { lock(gate){using var c=Open();using var tx=c.BeginTransaction();var result=operation(c);tx.Commit();return result;} }
    public bool HasUsers(){lock(gate){using var c=Open();return Query(c,"SELECT id FROM users LIMIT 1").Count>0;}}
    public void ApplyLicensedCompany(LicenseSnapshot license){
        if(string.IsNullOrWhiteSpace(license.Company)||string.IsNullOrWhiteSpace(license.Cnpj))return;
        lock(gate){
            using var c=Open();using var tx=c.BeginTransaction();
            var companies=Query(c,"SELECT id,name,cnpj FROM companies ORDER BY id");
            if(companies.Count!=1)return;
            var current=companies[0];var name=(current["name"]?.ToString()??"").Trim();var document=(current["cnpj"]?.ToString()??"").Trim();
            var placeholder=string.Equals(name,"EMPRESA DE TESTE",StringComparison.OrdinalIgnoreCase)||
                string.Equals(document,"TESTE-LOCAL",StringComparison.OrdinalIgnoreCase)||
                string.Equals(name,"EMPRESA AGUARDANDO ATIVAÇÃO",StringComparison.OrdinalIgnoreCase)||
                string.Equals(document,"LICENCA-PENDENTE",StringComparison.OrdinalIgnoreCase);
            if(!placeholder)return;
            var licensedName=Required(license.Company,"Empresa da licença");
            var licensedDocument=new string(license.Cnpj.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if(licensedDocument.Length is not (11 or 14))throw new ArgumentException("O CPF ou CNPJ vinculado à licença é inválido.");
            var id=Convert.ToInt32(Number(current,"id"));
            Run(c,"UPDATE companies SET name=@p0,cnpj=@p1 WHERE id=@p2",licensedName,licensedDocument,id);
            Audit(c,id,"Empresa vinculada à licença",new{name=licensedName,document=licensedDocument});
            tx.Commit();
        }
    }
    static string Username(string? value){var username=Required(value,"Usuário").Replace(" ","");if(username.Length<3||username.Length>40||!Regex.IsMatch(username,@"^[A-Z0-9._-]+$"))throw new ArgumentException("Use de 3 a 40 caracteres no usuário: letras, números, ponto, traço ou sublinhado.");if(string.Equals(username,"ADMIN",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("O usuário ADMIN é reservado ao Painel Central Monalisa.");return username;}
    static void ValidatePin(string? value){if(value is null||!Regex.IsMatch(value,@"^\d{6,12}$"))throw new ArgumentException("O PIN deve conter de 6 a 12 números.");}
    static (string Salt,string Hash) HashPin(string pin){var salt=RandomNumberGenerator.GetBytes(16);var hash=Rfc2898DeriveBytes.Pbkdf2(pin,salt,120000,HashAlgorithmName.SHA256,32);return(Convert.ToBase64String(salt),Convert.ToBase64String(hash));}
    static bool CheckPin(string pin,string salt,string expected){try{var hash=Rfc2898DeriveBytes.Pbkdf2(pin,Convert.FromBase64String(salt),120000,HashAlgorithmName.SHA256,32);return CryptographicOperations.FixedTimeEquals(hash,Convert.FromBase64String(expected));}catch{return false;}}
    static AuthIdentity Identity(Dictionary<string,object?> row)=>new(Convert.ToInt32(row["id"]), (string)row["name"]!, (string)row["username"]!, Number(row,"globalAdmin")==1);
    public AuthIdentity SetupAdministrator(BootstrapInput x){lock(gate){using var c=Open();using var tx=c.BeginTransaction();if(Query(c,"SELECT id FROM users LIMIT 1").Count>0)throw new ArgumentException("O administrador geral já foi configurado.");ValidatePin(x.Pin);var pin=HashPin(x.Pin);Run(c,"INSERT INTO users(name,username,pinSalt,pinHash,globalAdmin) VALUES(@p0,@p1,@p2,@p3,1)",Required(x.Name,"Nome"),Username(x.Username),pin.Salt,pin.Hash);var id=Convert.ToInt32(Number(Query(c,"SELECT last_insert_rowid() AS id")[0],"id"));Run(c,"INSERT INTO access_audit(actorUserId,action,detail) VALUES(@p0,'CRIAÇÃO DO ADMINISTRADOR GERAL',@p1)",id,JsonSerializer.Serialize(new{usuario=Username(x.Username)}));tx.Commit();return new(id,Required(x.Name,"Nome"),Username(x.Username),true);}}
    public AuthIdentity Authenticate(LoginInput x){lock(gate){using var c=Open();var username=Username(x.Username);var rows=Query(c,"SELECT * FROM users WHERE username=@p0",username);if(rows.Count==0)throw new ArgumentException("Usuário ou PIN inválido.");var row=rows[0];if(Number(row,"active")==0)throw new ArgumentException("Este acesso está bloqueado.");if(DateTime.TryParse((string)row["lockedUntil"]!,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var locked)&&locked>DateTime.UtcNow)throw new ArgumentException("Acesso temporariamente bloqueado. Aguarde alguns minutos.");if(!CheckPin(x.Pin,(string)row["pinSalt"]!,(string)row["pinHash"]!)){var attempts=Convert.ToInt32(Number(row,"failedAttempts"))+1;var until=attempts>=5?DateTime.UtcNow.AddMinutes(5).ToString("O"):"";Run(c,"UPDATE users SET failedAttempts=@p0,lockedUntil=@p1 WHERE id=@p2",attempts>=5?0:attempts,until,row["id"]);throw new ArgumentException(attempts>=5?"Muitas tentativas. O acesso foi bloqueado por cinco minutos.":"Usuário ou PIN inválido.");}Run(c,"UPDATE users SET failedAttempts=0,lockedUntil='' WHERE id=@p0",row["id"]);Run(c,"INSERT INTO access_audit(actorUserId,action,detail) VALUES(@p0,'LOGIN',@p1)",row["id"],JsonSerializer.Serialize(new{usuario=username}));return Identity(row);}}
    public AuthIdentity? SessionUser(int id){lock(gate){using var c=Open();var rows=Query(c,"SELECT * FROM users WHERE id=@p0 AND active=1",id);return rows.Count==0?null:Identity(rows[0]);}}
    public bool CanAccessCompany(int userId,int companyId){lock(gate){using var c=Open();return Query(c,"SELECT id FROM users WHERE id=@p0 AND active=1 AND globalAdmin=1",userId).Count>0||Query(c,"SELECT userId FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND active=1",userId,companyId).Count>0;}}
    public bool IsFinancialUser(AuthIdentity actor,int companyId){lock(gate){if(actor.GlobalAdmin)return false;using var c=Open();return Query(c,"SELECT userId FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND role='user' AND active=1",actor.Id,companyId).Count>0;}}
    public bool HasAdministrativeAccess(AuthIdentity actor){lock(gate){if(actor.GlobalAdmin)return true;using var c=Open();return Query(c,"SELECT userId FROM user_companies WHERE userId=@p0 AND role='admin' AND active=1 LIMIT 1",actor.Id).Count>0;}}
    public bool CanManagePlanRates(AuthIdentity actor,int companyId){lock(gate){if(actor.GlobalAdmin)return true;using var c=Open();var rows=Query(c,"SELECT role,canManagePlanRates FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND active=1",actor.Id,companyId);return rows.Any(row=>string.Equals(row["role"] as string,"admin",StringComparison.Ordinal)||Number(row,"canManagePlanRates")==1);}}
    public bool CanManageCompanyAccess(AuthIdentity actor,int companyId){lock(gate){if(actor.GlobalAdmin)return true;using var c=Open();return Query(c,"SELECT userId FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND role='admin' AND active=1",actor.Id,companyId).Count>0;}}
    public object Companies(AuthIdentity? user){lock(gate){using var c=Open();if(user is null||user.GlobalAdmin)return Query(c,"SELECT id,name,cnpj,'global' AS role FROM companies ORDER BY name");return Query(c,"SELECT c.id,c.name,c.cnpj,uc.role FROM companies c JOIN user_companies uc ON uc.companyId=c.id WHERE uc.userId=@p0 AND uc.active=1 ORDER BY c.name",user.Id);}}
    public object AccessContext(AuthIdentity actor){lock(gate){using var c=Open();var rows=Query(c,"SELECT * FROM users WHERE id=@p0 AND active=1",actor.Id);if(rows.Count==0)throw new ArgumentException("Acesso não encontrado.");var user=Identity(rows[0]);var companies=user.GlobalAdmin?Query(c,"SELECT id,name,cnpj,'global' AS role FROM companies ORDER BY name"):Query(c,"SELECT c.id,c.name,c.cnpj,uc.role FROM companies c JOIN user_companies uc ON uc.companyId=c.id WHERE uc.userId=@p0 AND uc.active=1 ORDER BY c.name",actor.Id);return new{id=user.Id,name=user.Name,username=user.Username,globalAdmin=user.GlobalAdmin,companies};}}
    public object AllUsers(AuthIdentity actor){lock(gate){if(!actor.GlobalAdmin)throw new ArgumentException("Somente o administrador geral pode consultar todos os acessos.");using var c=Open();return Query(c,"SELECT id,name,username,globalAdmin,active FROM users ORDER BY name");}}
    static void RequireCompanyAdmin(SqliteConnection c,int company,AuthIdentity actor){Company(c,company);if(actor.GlobalAdmin)return;var rows=Query(c,"SELECT role FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND active=1",actor.Id,company);if(rows.Count==0||!string.Equals(rows[0]["role"] as string,"admin",StringComparison.Ordinal))throw new ArgumentException("Somente um administrador desta empresa pode gerenciar acessos.");}
    public object CompanyUsers(int company,AuthIdentity actor){lock(gate){using var c=Open();RequireCompanyAdmin(c,company,actor);return Query(c,"SELECT u.id,u.name,u.username,u.active AS userActive,uc.role,uc.active,uc.canManagePlanRates FROM users u JOIN user_companies uc ON uc.userId=u.id WHERE uc.companyId=@p0 ORDER BY u.name",company);}}
    public object CreateCompanyUser(int company,UserAccessInput x,AuthIdentity actor)=>Write(c=>{RequireCompanyAdmin(c,company,actor);var role=x.Role.ToLowerInvariant();if(role!="admin"&&role!="user")throw new ArgumentException("Tipo de acesso inválido.");if(role=="admin"&&!actor.GlobalAdmin)throw new ArgumentException("Somente o administrador geral pode criar outro administrador.");ValidatePin(x.Pin);var username=Username(x.Username);if(Query(c,"SELECT id FROM users WHERE username=@p0",username).Count>0)throw new ArgumentException("Este nome de usuário já está em uso.");var pin=HashPin(x.Pin);Run(c,"INSERT INTO users(name,username,pinSalt,pinHash) VALUES(@p0,@p1,@p2,@p3)",Required(x.Name,"Nome"),username,pin.Salt,pin.Hash);var id=Number(Query(c,"SELECT last_insert_rowid() AS id")[0],"id");var canRates=role=="user"&&x.CanManagePlanRates?1:0;Run(c,"INSERT INTO user_companies(userId,companyId,role,canManagePlanRates) VALUES(@p0,@p1,@p2,@p3)",id,company,role,canRates);Run(c,"INSERT INTO access_audit(actorUserId,companyId,action,detail) VALUES(@p0,@p1,'CRIAÇÃO DE ACESSO',@p2)",actor.Id,company,JsonSerializer.Serialize(new{id,username,role,canManagePlanRates=canRates==1}));return new{id};});
    public object LinkCompanyUser(int company,int userId,LinkUserInput x,AuthIdentity actor)=>Write(c=>{if(!actor.GlobalAdmin)throw new ArgumentException("Somente o administrador geral pode vincular uma pessoa a outra empresa.");RequireCompanyAdmin(c,company,actor);var role=x.Role.ToLowerInvariant();if(role!="admin"&&role!="user")throw new ArgumentException("Tipo de acesso inválido.");var user=Query(c,"SELECT id,globalAdmin FROM users WHERE id=@p0 AND active=1",userId);if(user.Count==0)throw new ArgumentException("Usuário não encontrado.");if(Number(user[0],"globalAdmin")==1)throw new ArgumentException("O administrador geral já possui acesso a todas as empresas.");if(Query(c,"SELECT userId FROM user_companies WHERE userId=@p0 AND companyId=@p1",userId,company).Count>0)throw new ArgumentException("Esta pessoa já possui vínculo com a empresa.");var canRates=role=="user"&&x.CanManagePlanRates?1:0;Run(c,"INSERT INTO user_companies(userId,companyId,role,canManagePlanRates) VALUES(@p0,@p1,@p2,@p3)",userId,company,role,canRates);Run(c,"INSERT INTO access_audit(actorUserId,companyId,action,detail) VALUES(@p0,@p1,'VÍNCULO DE ACESSO',@p2)",actor.Id,company,JsonSerializer.Serialize(new{userId,role,canManagePlanRates=canRates==1}));return new{ok=true};});
    public object UpdateCompanyUser(int company,int userId,AccessUpdateInput x,AuthIdentity actor)=>Write(c=>{RequireCompanyAdmin(c,company,actor);var role=x.Role.ToLowerInvariant();if(role!="admin"&&role!="user")throw new ArgumentException("Tipo de acesso inválido.");if((role=="admin"||userId==actor.Id)&&!actor.GlobalAdmin)throw new ArgumentException("Somente o administrador geral pode alterar este acesso.");if(Query(c,"SELECT userId FROM user_companies WHERE userId=@p0 AND companyId=@p1",userId,company).Count==0)throw new ArgumentException("Acesso não encontrado nesta empresa.");var canRates=role=="user"&&x.CanManagePlanRates?1:0;Run(c,"UPDATE user_companies SET role=@p0,active=@p1,canManagePlanRates=@p2 WHERE userId=@p3 AND companyId=@p4",role,x.Active?1:0,canRates,userId,company);Run(c,"INSERT INTO access_audit(actorUserId,companyId,action,detail) VALUES(@p0,@p1,'ALTERAÇÃO DE ACESSO',@p2)",actor.Id,company,JsonSerializer.Serialize(new{userId,role,x.Active,canManagePlanRates=canRates==1}));return new{ok=true};});
    public object CreateCompany(CompanyInput x) => Write(c=> {
        var name=Required(x.Name,"Nome");var cnpj=new string((x.Cnpj??"").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if(cnpj.Length!=14)throw new ArgumentException("Informe o CNPJ com 14 caracteres.");
        Run(c,"INSERT INTO companies(name,cnpj) VALUES(@p0,@p1)",name,cnpj);
        var id=Number(Query(c,"SELECT last_insert_rowid() AS id")[0],"id");
        Run(c,"INSERT INTO accounts(companyId,name,opening,type) VALUES(@p0,'CAIXA',0,'cash')",id);SeedPlans(c,Convert.ToInt32(id));SeedAuxiliaries(c,Convert.ToInt32(id));return new {id};
    });
    public object Party(int company,PartyInput x)=>Write(c=> { Company(c,company);var personType=ValidateParty(x);Run(c,"INSERT INTO parties(companyId,name,document,type,phone,email,personType) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6)",company,Required(x.Name,"Nome"),CleanDocument(x.Document),x.Type,RequiredOrEmpty(x.Phone),RequiredOrEmpty(x.Email),personType);var id=Number(Query(c,"SELECT last_insert_rowid() AS id")[0],"id");Audit(c,company,"Cadastro de pessoa",x.Name);return new {ok=true,id}; });
    static string CleanDocument(string? value)=>new string((value??"").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    static string ValidateParty(PartyInput x){
        if(x.Type!="customer"&&x.Type!="supplier"&&x.Type!="both")throw new ArgumentException("Tipo de pessoa inválido.");if(!string.IsNullOrWhiteSpace(x.Email)&&!x.Email.Contains('@'))throw new ArgumentException("Informe um e-mail válido.");
        var document=CleanDocument(x.Document);var personType=(x.PersonType??"").Trim().ToLowerInvariant();if(personType.Length==0)personType=document.Length==11?"individual":document.Length==14?"company":"";
        if(personType is not ("" or "individual" or "company"))throw new ArgumentException("Selecione pessoa física ou jurídica.");
        if(document.Length>0&&personType=="individual"&&document.Length!=11)throw new ArgumentException("O CPF deve conter 11 números.");
        if(document.Length>0&&personType=="company"&&document.Length!=14)throw new ArgumentException("O CNPJ deve conter 14 caracteres.");return personType;
    }
    public object EditParty(int company,int id,PartyInput x)=>Write(c=> {Existing(c,"parties",id,company);var personType=ValidateParty(x);Run(c,"UPDATE parties SET name=@p0,document=@p1,type=@p2,phone=@p3,email=@p4,personType=@p5 WHERE id=@p6",Required(x.Name,"Nome"),CleanDocument(x.Document),x.Type,RequiredOrEmpty(x.Phone),RequiredOrEmpty(x.Email),personType,id);Audit(c,company,"Edição de pessoa",new{id,input=x});return new {ok=true};});
    public object DeleteParty(int company,int id)=>Write(c=> {Existing(c,"parties",id,company);var linked=Query(c,"SELECT description,due FROM titles WHERE partyId=@p0 AND deleted=0 ORDER BY due,id LIMIT 5",id);if(linked.Count>0)throw new ArgumentException("Não foi possível excluir. Este cadastro está vinculado aos lançamentos: "+LinkedTitles(linked)+".");var name=(string)Query(c,"SELECT name FROM parties WHERE id=@p0",id)[0]["name"]!;Run(c,"UPDATE titles SET partyId=NULL WHERE partyId=@p0 AND deleted=1",id);Run(c,"DELETE FROM parties WHERE id=@p0",id);Audit(c,company,"Exclusão de pessoa",new{id,name});return new{ok=true};});
    static string AccountType(string? value){var type=(value??"cash").ToLowerInvariant();if(type is not ("cash" or "bank" or "wallet"))throw new ArgumentException("Tipo de conta inválido.");return type;}
    public object Account(int company,AccountInput x)=>Write(c=> {Company(c,company);if(x.Opening < -999999999999 || x.Opening > 999999999999)throw new ArgumentException("Saldo fora do limite.");Run(c,"INSERT INTO accounts(companyId,name,opening,type) VALUES(@p0,@p1,@p2,@p3)",company,Required(x.Name,"Nome"),x.Opening,AccountType(x.Type));Audit(c,company,"Cadastro de conta",x);return new {ok=true};});
    public object EditAccount(int company,int id,AccountInput x)=>Write(c=> {Existing(c,"accounts",id,company);if(x.Opening < -999999999999 || x.Opening > 999999999999)throw new ArgumentException("Saldo fora do limite.");Run(c,"UPDATE accounts SET name=@p0,opening=@p1,type=@p2 WHERE id=@p3",Required(x.Name,"Nome"),x.Opening,AccountType(x.Type),id);Audit(c,company,"Edição de conta",new{id,input=x});return new{ok=true};});
    public object DeleteAccount(int company,int id)=>Write(c=> {Existing(c,"accounts",id,company);var linked=Query(c,"SELECT description,date FROM movements WHERE accountId=@p0 ORDER BY date,id LIMIT 5",id);if(linked.Count>0)throw new ArgumentException("Não foi possível excluir. Esta conta está vinculada às movimentações: "+LinkedMovements(linked)+".");if(Query(c,"SELECT id FROM accounts WHERE companyId=@p0",company).Count<=1)throw new ArgumentException("Mantenha ao menos uma conta financeira para registrar as baixas.");var name=(string)Query(c,"SELECT name FROM accounts WHERE id=@p0",id)[0]["name"]!;Run(c,"DELETE FROM accounts WHERE id=@p0",id);Audit(c,company,"Exclusão de conta",new{id,name});return new{ok=true};});
    public object Plan(int company,PlanInput x)=>Write(c=> {Company(c,company);var rates=new[]{x.InterestRate,x.PenaltyRate,x.DiscountRate};if(rates.Any(v=>v<0||v>100))throw new ArgumentException("Os percentuais devem ficar entre 0 e 100.");Run(c,"INSERT INTO plans(companyId,name,interestRate,penaltyRate,discountRate) VALUES(@p0,@p1,@p2,@p3,@p4)",company,Required(x.Name,"Plano de contas"),x.InterestRate,x.PenaltyRate,x.DiscountRate);Audit(c,company,"Cadastro de plano de contas",x);return new {ok=true};});
    public object EditPlan(int company,int id,PlanInput x)=>Write(c=> {Existing(c,"plans",id,company);var rates=new[]{x.InterestRate,x.PenaltyRate,x.DiscountRate};if(rates.Any(v=>v<0||v>100))throw new ArgumentException("Os percentuais devem ficar entre 0 e 100.");var name=Required(x.Name,"Plano de contas");var old=(string)Query(c,"SELECT name FROM plans WHERE id=@p0",id)[0]["name"]!;Run(c,"UPDATE plans SET name=@p0,interestRate=@p1,penaltyRate=@p2,discountRate=@p3 WHERE id=@p4",name,x.InterestRate,x.PenaltyRate,x.DiscountRate,id);Run(c,"UPDATE titles SET category=@p0,interestRate=@p1,penaltyRate=@p2,discountRate=@p3 WHERE companyId=@p4 AND category=@p5 AND deleted=0 AND cancelled=0",name,x.InterestRate,x.PenaltyRate,x.DiscountRate,company,old);Audit(c,company,"Edição de plano de contas",new {id,old,input=x});return new {ok=true};});
    public object DeletePlan(int company,int id)=>Write(c=> {Existing(c,"plans",id,company);var name=(string)Query(c,"SELECT name FROM plans WHERE id=@p0",id)[0]["name"]!;var linked=Query(c,"SELECT description,due FROM titles WHERE companyId=@p0 AND category=@p1 AND deleted=0 ORDER BY due,id LIMIT 5",company,name);if(linked.Count>0)throw new ArgumentException("Não foi possível excluir. Este plano de contas está vinculado aos lançamentos: "+LinkedTitles(linked)+".");Run(c,"DELETE FROM plans WHERE id=@p0",id);Audit(c,company,"Exclusão de plano de contas",new{id,name});return new{ok=true};});
    static string MethodKind(string? value){var kind=(value??"both").ToLowerInvariant();if(kind is not ("both" or "payable" or "receivable"))throw new ArgumentException("Uso da forma de pagamento inválido.");return kind;}
    public object PaymentMethod(int company,NamedKindInput x)=>Write(c=>{Company(c,company);Run(c,"INSERT INTO payment_methods(companyId,name,kind) VALUES(@p0,@p1,@p2)",company,Required(x.Name,"Forma de pagamento"),MethodKind(x.Kind));Audit(c,company,"Cadastro de forma de pagamento",x);return new{ok=true};});
    public object EditPaymentMethod(int company,int id,NamedKindInput x)=>Write(c=>{Existing(c,"payment_methods",id,company);var name=Required(x.Name,"Forma de pagamento");Run(c,"UPDATE payment_methods SET name=@p0,kind=@p1 WHERE id=@p2",name,MethodKind(x.Kind),id);Audit(c,company,"Edição de forma de pagamento",new{id,name});return new{ok=true};});
    public object DeletePaymentMethod(int company,int id)=>Write(c=>{Existing(c,"payment_methods",id,company);if(Query(c,"SELECT id FROM titles WHERE paymentMethodId=@p0 AND deleted=0 UNION SELECT id FROM settlements WHERE paymentMethodId=@p0 LIMIT 1",id).Count>0)throw new ArgumentException("Esta forma está vinculada a lançamentos ou baixas.");var name=(string)Query(c,"SELECT name FROM payment_methods WHERE id=@p0",id)[0]["name"]!;Run(c,"DELETE FROM payment_methods WHERE id=@p0",id);Audit(c,company,"Exclusão de forma de pagamento",new{id,name});return new{ok=true};});
    public object CostCenter(int company,NamedInput x)=>Write(c=>{Company(c,company);var name=Required(x.Name,"Centro de custo");Run(c,"INSERT INTO cost_centers(companyId,name) VALUES(@p0,@p1)",company,name);Audit(c,company,"Cadastro de centro de custo",new{name});return new{ok=true};});
    public object EditCostCenter(int company,int id,NamedInput x)=>Write(c=>{Existing(c,"cost_centers",id,company);var old=(string)Query(c,"SELECT name FROM cost_centers WHERE id=@p0",id)[0]["name"]!;var name=Required(x.Name,"Centro de custo");Run(c,"UPDATE cost_centers SET name=@p0 WHERE id=@p1",name,id);Run(c,"UPDATE titles SET costCenter=@p0 WHERE companyId=@p1 AND costCenter=@p2",name,company,old);Audit(c,company,"Edição de centro de custo",new{id,old,name});return new{ok=true};});
    public object DeleteCostCenter(int company,int id)=>Write(c=>{Existing(c,"cost_centers",id,company);var name=(string)Query(c,"SELECT name FROM cost_centers WHERE id=@p0",id)[0]["name"]!;var linked=Query(c,"SELECT description,due FROM titles WHERE companyId=@p0 AND costCenter=@p1 AND deleted=0 LIMIT 5",company,name);if(linked.Count>0)throw new ArgumentException("Este centro de custo está vinculado aos lançamentos: "+LinkedTitles(linked)+".");Run(c,"DELETE FROM cost_centers WHERE id=@p0",id);Audit(c,company,"Exclusão de centro de custo",new{id,name});return new{ok=true};});
    const string TitlesSql="""
        SELECT t.*,p.name AS partyName,pm.name AS paymentMethodName,COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) AS settled,
        t.amount-COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) AS remaining
        FROM titles t LEFT JOIN parties p ON p.id=t.partyId LEFT JOIN payment_methods pm ON pm.id=t.paymentMethodId WHERE t.companyId=@p0 AND t.deleted=0 ORDER BY t.due,t.id
        """;
    public object State(int company,AuthIdentity? actor=null) { lock(gate){using var c=Open();Company(c,company);
        var financialUser=actor is not null&&!actor.GlobalAdmin&&Query(c,"SELECT role FROM user_companies WHERE userId=@p0 AND companyId=@p1 AND active=1",actor.Id,company).Any(row=>string.Equals(row["role"] as string,"user",StringComparison.Ordinal));
        return new {
        titles=Query(c,TitlesSql,company),
        parties=Query(c,"SELECT * FROM parties WHERE companyId=@p0 ORDER BY name",company),
        accounts=Query(c,"SELECT a.*,a.opening+COALESCE((SELECT SUM(m.amount) FROM movements m WHERE m.accountId=a.id),0) AS balance FROM accounts a WHERE a.companyId=@p0 ORDER BY a.name",company),
        plans=Query(c,"SELECT * FROM plans WHERE companyId=@p0 ORDER BY name",company),
        paymentMethods=Query(c,"SELECT * FROM payment_methods WHERE companyId=@p0 ORDER BY name",company),
        costCenters=Query(c,"SELECT * FROM cost_centers WHERE companyId=@p0 ORDER BY name",company),
        attachments=Query(c,"SELECT id,titleId,settlementId,name,mimeType,created,length(data) AS size FROM attachments WHERE companyId=@p0 ORDER BY id DESC",company),
        settlements=Query(c,"SELECT s.*,a.name AS accountName,pm.name AS paymentMethodName FROM settlements s JOIN accounts a ON a.id=s.accountId LEFT JOIN payment_methods pm ON pm.id=s.paymentMethodId WHERE s.companyId=@p0 ORDER BY s.id DESC",company),
        movements=financialUser?[]:Query(c,"SELECT m.*,a.name AS accountName FROM movements m JOIN accounts a ON a.id=m.accountId WHERE m.companyId=@p0 ORDER BY m.date DESC,m.id DESC",company),
        audit=financialUser?[]:Query(c,"SELECT * FROM audit WHERE companyId=@p0 ORDER BY id DESC LIMIT 100",company),
        permissions=new {role=financialUser?"financial":"administrator",canViewCashFlow=!financialUser,canViewHistory=!financialUser,canManageAccess=!financialUser,canCreateBackup=!financialUser,canManageCostCenters=!financialUser,canManagePlanRates=!financialUser||actor is not null&&CanManagePlanRates(actor,company)}
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
        long Cash(Dictionary<string,object?> row)=>Number(row,"principal")+Number(row,"interest")+Number(row,"penalty")+Number(row,"additional")-Number(row,"discount");
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
                (string)row["description"]!, (string)row["accountName"]!, Cash(row), Principal:Number(row,"principal"), Interest:Number(row,"interest"), Penalty:Number(row,"penalty"), Discount:Number(row,"discount"), Additional:Number(row,"additional"))).ToList(),
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
                       s.date AS paymentDate,s.principal,s.interest,s.penalty,s.discount,s.additional,
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
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.additional ELSE 0 END),0) AS additional,
                       t.amount-COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.principal ELSE 0 END),0) AS remaining
                FROM titles t LEFT JOIN parties p ON p.id=t.partyId LEFT JOIN settlements s ON s.titleId=t.id
                WHERE t.companyId=@p0 AND t.kind='payable' AND t.deleted=0 AND t.cancelled=0 AND {range}
                GROUP BY t.id
                HAVING t.amount>COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.principal ELSE 0 END),0)
                ORDER BY t.due,t.id
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
                       s.date AS paymentDate,s.principal,s.interest,s.penalty,s.discount,s.additional,
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
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.additional ELSE 0 END),0) AS additional,
                       t.amount-COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.principal ELSE 0 END),0) AS remaining
                FROM titles t LEFT JOIN parties p ON p.id=t.partyId LEFT JOIN settlements s ON s.titleId=t.id
                WHERE t.companyId=@p0 AND t.kind='receivable' AND t.deleted=0 AND t.cancelled=0 AND {range}
                GROUP BY t.id
                HAVING t.amount>COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.principal ELSE 0 END),0)
                ORDER BY t.due,t.id
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
    GenericReportData AdvancedData(SqliteConnection c,int company,string type,string startValue,string endValue,int? partyId,int? accountId){
        var start=Date(startValue);var end=Date(endValue);if(end<start)throw new ArgumentException("A data final não pode ser anterior à inicial.");if(end.DayNumber-start.DayNumber>1095)throw new ArgumentException("Escolha um período de até três anos.");var endExclusive=end.AddDays(1).ToString("yyyy-MM-dd");var startText=start.ToString("yyyy-MM-dd");var companyRow=Query(c,"SELECT name,cnpj FROM companies WHERE id=@p0",company)[0];var rows=new List<IReadOnlyList<string>>();var headers=new List<string>();var title="";long total=0;List<GenericReportGroup>? groups=null;
        string M(long value)=>(value/100m).ToString("C",CultureInfo.GetCultureInfo("pt-BR"));string D(string value)=>Date(value).ToString("dd/MM/yyyy");
        if(type=="overdue"){
            title="TÍTULOS VENCIDOS E TEMPO DE ATRASO";headers.AddRange(new[]{"VENCIMENTO","ATRASO","TIPO","PESSOA","DESCRIÇÃO","PLANO","VALOR","EM ABERTO"});
            foreach(var row in Query(c,"""
                SELECT t.*,COALESCE(p.name,'SEM PESSOA') AS partyName,t.amount-COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) AS remaining
                FROM titles t LEFT JOIN parties p ON p.id=t.partyId WHERE t.companyId=@p0 AND t.deleted=0 AND t.cancelled=0 AND t.due<@p1
                AND t.amount>COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) ORDER BY t.due
                """,company,DateTime.Today.ToString("yyyy-MM-dd"))){var remaining=Number(row,"remaining");var days=DateOnly.FromDateTime(DateTime.Today).DayNumber-Date((string)row["due"]!).DayNumber;rows.Add(new[]{D((string)row["due"]!),$"{days} DIA(S)",(string)row["kind"]! != "payable"?"A RECEBER":"A PAGAR",(string)row["partyName"]!,(string)row["description"]!,(string)row["category"]!,M(Number(row,"amount")),M(remaining)});total+=remaining;}
        }else if(type=="party"){
            title="HISTÓRICO FINANCEIRO POR CLIENTE E FORNECEDOR";headers.AddRange(new[]{"VENCIMENTO","PESSOA","TIPO","DESCRIÇÃO","PLANO","VALOR","EM ABERTO"});var filter=partyId.HasValue?" AND t.partyId=@p3":"";var values=partyId.HasValue?new object?[]{company,startText,endExclusive,partyId}:new object?[]{company,startText,endExclusive};
            var partyGroups=new Dictionary<string,List<IReadOnlyList<string>>>();var partyTotals=new Dictionary<string,long>();
            foreach(var row in Query(c,$"""SELECT t.*,COALESCE(p.name,'SEM PESSOA') AS partyName,t.amount-COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) AS remaining FROM titles t LEFT JOIN parties p ON p.id=t.partyId WHERE t.companyId=@p0 AND t.deleted=0 AND t.due>=@p1 AND t.due<@p2 {filter} ORDER BY partyName,t.due""",values)){var key=(string)row["partyName"]!;var remaining=Number(row,"remaining");var line=new[]{D((string)row["due"]!),key,(string)row["kind"]! != "payable"?"A RECEBER":"A PAGAR",(string)row["description"]!,(string)row["category"]!,M(Number(row,"amount")),M(remaining)};rows.Add(line);if(!partyGroups.TryGetValue(key,out var list)){list=[];partyGroups[key]=list;partyTotals[key]=0;}list.Add(line);partyTotals[key]+=remaining;total+=remaining;}
            groups=partyGroups.OrderBy(item=>item.Key).Select(item=>new GenericReportGroup($"CLIENTE / FORNECEDOR: {item.Key}",item.Value,$"SALDO DO CADASTRO: {M(partyTotals[item.Key])}")).ToList();
        }else if(type=="cost-center"){
            title="RECEITAS E DESPESAS POR CENTRO DE CUSTO";headers.AddRange(new[]{"VENCIMENTO","CENTRO DE CUSTO","TIPO","DESCRIÇÃO","PLANO","VALOR","EM ABERTO"});
            foreach(var row in Query(c,"""SELECT t.*,t.amount-COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) AS remaining FROM titles t WHERE t.companyId=@p0 AND t.deleted=0 AND t.due>=@p1 AND t.due<@p2 ORDER BY costCenter,due""",company,startText,endExclusive)){rows.Add(new[]{D((string)row["due"]!),string.IsNullOrWhiteSpace(row["costCenter"] as string)?"SEM CENTRO":(string)row["costCenter"]!,(string)row["kind"]! != "payable"?"RECEITA":"DESPESA",(string)row["description"]!,(string)row["category"]!,M(Number(row,"amount")),M(Number(row,"remaining"))});total+=Number(row,"amount");}
        }else if(type=="account"){
            title="EXTRATO POR CONTA FINANCEIRA";headers.AddRange(new[]{"DATA","CONTA","MOVIMENTO","ENTRADA","SAÍDA","SALDO DO MOVIMENTO"});var filter=accountId.HasValue?" AND m.accountId=@p3":"";var values=accountId.HasValue?new object?[]{company,startText,endExclusive,accountId}:new object?[]{company,startText,endExclusive};
            var accountGroups=new Dictionary<string,List<IReadOnlyList<string>>>();var accountTotals=new Dictionary<string,long>();
            foreach(var row in Query(c,$"""SELECT m.*,a.name AS accountName FROM movements m JOIN accounts a ON a.id=m.accountId WHERE m.companyId=@p0 AND m.date>=@p1 AND m.date<@p2 {filter} ORDER BY a.name,m.date,m.id""",values)){var amount=Number(row,"amount");var key=(string)row["accountName"]!;var line=new[]{D((string)row["date"]!),key,(string)row["description"]!,amount>0?M(amount):"-",amount<0?M(-amount):"-",M(amount)};rows.Add(line);if(!accountGroups.TryGetValue(key,out var list)){list=[];accountGroups[key]=list;accountTotals[key]=0;}list.Add(line);accountTotals[key]+=amount;total+=amount;}
            groups=accountGroups.OrderBy(item=>item.Key).Select(item=>new GenericReportGroup($"CONTA FINANCEIRA: {item.Key}",item.Value,$"MOVIMENTO LÍQUIDO: {M(accountTotals[item.Key])}")).ToList();
        }else if(type=="cash-flow"){
            title="FLUXO DE CAIXA PREVISTO E REALIZADO";headers.AddRange(new[]{"DATA","ORIGEM","DESCRIÇÃO","ENTRADA","SAÍDA","SITUAÇÃO"});
            foreach(var row in Query(c,"""SELECT due AS date,'PREVISTO' AS origin,description,kind,amount FROM titles WHERE companyId=@p0 AND deleted=0 AND cancelled=0 AND due>=@p1 AND due<@p2 UNION ALL SELECT date,'REALIZADO',description,CASE WHEN amount>=0 THEN 'receivable' ELSE 'payable' END,abs(amount) FROM movements WHERE companyId=@p0 AND date>=@p1 AND date<@p2 ORDER BY date""",company,startText,endExclusive)){var amount=Number(row,"amount");var incoming=(string)row["kind"]! != "payable";rows.Add(new[]{D((string)row["date"]!),(string)row["origin"]!,(string)row["description"]!,incoming?M(amount):"-",incoming?"-":M(amount),(string)row["origin"]!});total+=incoming?amount:-amount;}
        }else throw new ArgumentException("Tipo de relatório avançado inválido.");
        return new((string)companyRow["name"]!,(string)companyRow["cnpj"]!,title,$"{start:dd/MM/yyyy} A {end:dd/MM/yyyy}",headers,rows,$"TOTAL DO RELATÓRIO: {M(total)}",groups);
    }
    public PdfReport AdvancedReport(int company,string type,string start,string end,int? partyId,int? accountId){lock(gate){using var c=Open();Company(c,company);var data=AdvancedData(c,company,type,start,end,partyId,accountId);return new(GenericReportPdf.Generate(data),$"{type}-{start}-a-{end}.pdf");}}
    public DownloadFile ExportCsv(int company,string type,string start,string end,int? partyId,int? accountId){lock(gate){using var c=Open();Company(c,company);var data=AdvancedData(c,company,type,start,end,partyId,accountId);static string Escape(string value)=>"\""+value.Replace("\"","\"\"")+"\"";var lines=new List<string>();if(data.Groups is {Count:>0}){foreach(var group in data.Groups){if(lines.Count>0)lines.Add("");lines.Add(Escape(group.Name));lines.Add(string.Join(';',data.Headers.Select(Escape)));lines.AddRange(group.Rows.Select(row=>string.Join(';',row.Select(Escape))));lines.Add(Escape(group.Summary));}}else{lines.Add(string.Join(';',data.Headers.Select(Escape)));lines.AddRange(data.Rows.Select(row=>string.Join(';',row.Select(Escape))));}lines.Add("");lines.Add(Escape(data.Summary));return new(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\r\n",lines))).ToArray(),$"{type}-{start}-a-{end}.csv");}}
    static PayableReportRow ReportRow(Dictionary<string,object?> row,bool receivable) {
        var principal=Number(row,"principal");var interest=Number(row,"interest");var penalty=Number(row,"penalty");var discount=Number(row,"discount");var additional=Number(row,"additional");var remaining=Number(row,"remaining");
        var status=remaining<=0?(receivable?"RECEBIDO":"PAGO"):principal>0?"PARCIAL":string.CompareOrdinal((string)row["due"]!,DateTime.Today.ToString("yyyy-MM-dd"))<0?"VENCIDO":"EM ABERTO";
        return new PayableReportRow(Number(row,"id"),(string)row["due"]!,row["paymentDate"] as string,(string)row["supplier"]!,(string)row["documentNumber"]!,(string)row["description"]!,(string)row["category"]!,Number(row,"amount"),principal,remaining,interest,penalty,discount,additional,principal+interest+penalty+additional-discount,status);
    }
    public object Title(int company,TitleInput x,AuthIdentity? actor=null)=>Write(c=> {
        Company(c,company);if(x.Kind!="payable"&&x.Kind!="receivable")throw new ArgumentException("Tipo inválido.");Money(x.Amount,true);var due=Date(x.Due);var description=Required(x.Description,"Descrição");
        var document=(x.DocumentNumber??"").Trim().ToUpperInvariant();var issue=string.IsNullOrWhiteSpace(x.IssueDate)?"":Date(x.IssueDate).ToString("yyyy-MM-dd");
        if(document.Length>0 && Query(c,"SELECT id FROM titles WHERE companyId=@p0 AND deleted=0 AND documentNumber=@p1 AND partyId IS @p2 LIMIT 1",company,document,x.PartyId).Count>0)throw new ArgumentException("Este documento já foi lançado para o mesmo fornecedor ou cliente.");
        var planName=(string.IsNullOrWhiteSpace(x.Category)?(x.Kind=="payable"?"DESPESAS GERAIS":"RECEITAS DE SERVIÇOS"):x.Category.Trim()).ToUpperInvariant();
        var plan=Query(c,"SELECT * FROM plans WHERE companyId=@p0 AND lower(name)=lower(@p1)",company,planName).SingleOrDefault();
        if(plan is null){Run(c,"INSERT INTO plans(companyId,name) VALUES(@p0,@p1)",company,planName);plan=Query(c,"SELECT * FROM plans WHERE id=last_insert_rowid()")[0];}
        if(x.Installments<1||x.Installments>120||x.Amount<x.Installments)throw new ArgumentException("Informe entre 1 e 120 parcelas, com ao menos um centavo por parcela.");
        if(x.InstallmentIntervalDays<0||x.InstallmentIntervalDays>365)throw new ArgumentException("O intervalo deve ficar entre 1 e 365 dias, ou zero para mensal.");
        var custom=x.InstallmentAmounts is not null||x.InstallmentDues is not null;
        if(custom && (x.InstallmentAmounts is null||x.InstallmentDues is null||x.InstallmentAmounts.Length!=x.Installments||x.InstallmentDues.Length!=x.Installments))throw new ArgumentException("Complete o valor e o vencimento de todas as parcelas.");
        if(custom && (x.InstallmentAmounts!.Any(v=>v<=0)||x.InstallmentAmounts!.Sum()!=x.Amount))throw new ArgumentException("A soma das parcelas deve ser igual ao valor total.");
        var customDues=custom?x.InstallmentDues!.Select(Date).ToArray():Array.Empty<DateOnly>();
        if(x.PartyId.HasValue)Existing(c,"parties",x.PartyId.Value,company);
        if(x.PaymentMethodId.HasValue){Existing(c,"payment_methods",x.PaymentMethodId.Value,company);var method=Query(c,"SELECT kind FROM payment_methods WHERE id=@p0",x.PaymentMethodId.Value)[0]["kind"] as string;if(method!="both"&&method!=x.Kind)throw new ArgumentException("A forma selecionada não atende este tipo de lançamento.");}
        var costCenter=RequiredOrEmpty(x.CostCenter);var existingCostCenter=costCenter.Length==0||Query(c,"SELECT id FROM cost_centers WHERE companyId=@p0 AND upper(name)=upper(@p1)",company,costCenter).Count>0;if(!existingCostCenter&&actor is not null&&IsFinancialUser(actor,company))throw new ArgumentException("Selecione um centro de custo já cadastrado pelo administrador.");if(!existingCostCenter)Run(c,"INSERT INTO cost_centers(companyId,name) VALUES(@p0,@p1)",company,costCenter);
        var group=x.Installments>1 ? Guid.NewGuid().ToString("N") : null;
        for(var i=0;i<x.Installments;i++) Run(c,"INSERT INTO titles(companyId,kind,description,amount,due,partyId,category,notes,installmentGroup,installmentNumber,installmentCount,interestRate,penaltyRate,discountRate,documentNumber,issueDate,branch,costCenter,paymentMethodId) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17,@p18)",company,x.Kind,description+(x.Installments>1?$" ({i+1}/{x.Installments})":""),custom?x.InstallmentAmounts![i]:x.Amount/x.Installments+(i<x.Amount%x.Installments?1:0),(custom?customDues[i]:x.InstallmentIntervalDays==7?due.AddDays(i*7):due.AddMonths(i)).ToString("yyyy-MM-dd"),x.PartyId,planName,(x.Notes??"").ToUpperInvariant(),group,i+1,x.Installments,plan["interestRate"],plan["penaltyRate"],plan["discountRate"],document,issue,RequiredOrEmpty(x.Branch),costCenter,x.PaymentMethodId);
        Audit(c,company,"Lançamento",x);return new {ok=true};
    });
    long SettleCore(SqliteConnection c,int company,int id,SettlementInput x){
        Existing(c,"titles",id,company);Existing(c,"accounts",x.AccountId,company);var date=Date(x.Date);if(date>DateOnly.FromDateTime(DateTime.Today))throw new ArgumentException("A baixa não pode ter data futura.");
        Money(x.Principal,true);Money(x.Interest);Money(x.Penalty);Money(x.Discount);Money(x.Additional);
        if(x.PaymentMethodId.HasValue)Existing(c,"payment_methods",x.PaymentMethodId.Value,company);
        var title=Query(c,TitlesSql,company).Single(r=>Number(r,"id")==id);
        if(Number(title,"cancelled")!=0||x.Principal>Number(title,"remaining"))throw new ArgumentException("Valor excede o saldo em aberto ou título está cancelado.");
        if(x.Discount>x.Principal)throw new ArgumentException("Desconto não pode exceder o principal da baixa.");
        var cash=checked(x.Principal+x.Interest+x.Penalty+x.Additional-x.Discount);
        Run(c,"INSERT INTO settlements(companyId,titleId,accountId,principal,interest,penalty,discount,date,additional,paymentMethodId) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9)",company,id,x.AccountId,x.Principal,x.Interest,x.Penalty,x.Discount,x.Date,x.Additional,x.PaymentMethodId);
        var sid=Number(Query(c,"SELECT last_insert_rowid() AS id")[0],"id");
        Run(c,"INSERT INTO movements(companyId,accountId,amount,date,description,settlementId) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)",company,x.AccountId,(string)title["kind"]! == "payable"?-cash:cash,x.Date,title["description"],sid);
        Audit(c,company,"Baixa",new {titleId=id,settlementId=sid,input=x});return sid;
    }
    public object Settle(int company,int id,SettlementInput x)=>Write(c=>new{id=SettleCore(c,company,id,x)});
    public object BulkSettle(int company,BulkSettlementInput x)=>Write(c=>{
        if(x.Items is null||x.Items.Length<2||x.Items.Length>200)throw new ArgumentException("Selecione de 2 a 200 títulos para a baixa em lote.");if(x.Items.Select(i=>i.TitleId).Distinct().Count()!=x.Items.Length)throw new ArgumentException("Há títulos repetidos na seleção.");
        Existing(c,"accounts",x.AccountId,company);Date(x.Date);var ids=new List<long>();long total=0;
        foreach(var item in x.Items){var input=new SettlementInput(x.AccountId,item.Principal,item.Interest,item.Penalty,item.Discount,x.Date,item.Additional,x.PaymentMethodId);ids.Add(SettleCore(c,company,item.TitleId,input));total=checked(total+item.Principal+item.Interest+item.Penalty+item.Additional-item.Discount);}
        Audit(c,company,"Baixa em lote",new{count=ids.Count,total,settlementIds=ids});return new{ok=true,count=ids.Count,total,settlementIds=ids};
    });
    static string OfxTag(string text,string tag) {
        var match=Regex.Match(text,$@"<{Regex.Escape(tag)}>\s*([^<\r\n]+)",RegexOptions.IgnoreCase);
        return match.Success?match.Groups[1].Value.Trim():"";
    }
    static string OfxDate(string value,bool required) {
        var digits=new string((value??"").TakeWhile(char.IsDigit).ToArray());
        if(digits.Length>=8&&DateOnly.TryParseExact(digits[..8],"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))return date.ToString("yyyy-MM-dd");
        if(required)throw new ArgumentException("O OFX contém uma movimentação com data inválida.");return "";
    }
    static ParsedOfx ParseOfx(byte[] content) {
        if(content.Length==0||content.Length>10*1024*1024)throw new ArgumentException("O arquivo OFX deve ter até 10 MB.");
        var header=Encoding.ASCII.GetString(content,0,Math.Min(content.Length,500));
        var text=header.Contains("UTF-8",StringComparison.OrdinalIgnoreCase)?Encoding.UTF8.GetString(content):Encoding.Latin1.GetString(content);
        if(!text.Contains("<OFX",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("O arquivo selecionado não é um OFX válido.");
        var bankId=OfxTag(text,"BANKID");var start=OfxDate(OfxTag(text,"DTSTART"),false);var end=OfxDate(OfxTag(text,"DTEND"),false);
        var transactions=new List<ParsedOfxTransaction>();var index=0;
        foreach(Match block in Regex.Matches(text,@"(?is)<STMTTRN>(.*?)(?:</STMTTRN>|(?=<STMTTRN>)|(?=</BANKTRANLIST>))")) {
            index++;var value=block.Groups[1].Value;var rawAmount=OfxTag(value,"TRNAMT");
            if(!decimal.TryParse(rawAmount,NumberStyles.Number|NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var amount))continue;
            var cents=checked((long)Math.Round(amount*100m,0,MidpointRounding.AwayFromZero));if(cents==0)continue;
            var date=OfxDate(OfxTag(value,"DTPOSTED"),true);var memo=(OfxTag(value,"MEMO")+" "+OfxTag(value,"NAME")).Trim().ToUpperInvariant();if(memo.Length==0)memo="MOVIMENTO BANCÁRIO";
            var fitId=OfxTag(value,"FITID").Trim().ToUpperInvariant();if(fitId.Length==0)fitId=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{date}|{cents}|{memo}|{index}")));
            transactions.Add(new(fitId.Length>200?fitId[..200]:fitId,date,cents,memo.Length>500?memo[..500]:memo,OfxTag(value,"TRNTYPE").ToUpperInvariant()));
        }
        if(transactions.Count==0)throw new ArgumentException("Nenhuma movimentação foi encontrada no OFX.");
        return new(bankId,start,end,transactions);
    }
    static HashSet<string> MatchWords(string value) {
        var normalized=value.Normalize(NormalizationForm.FormD);
        var clean=new string(normalized.Where(ch=>CharUnicodeInfo.GetUnicodeCategory(ch)!=UnicodeCategory.NonSpacingMark).Select(ch=>char.IsLetterOrDigit(ch)?char.ToUpperInvariant(ch):' ').ToArray());
        return clean.Split(' ',StringSplitOptions.RemoveEmptyEntries).Where(word=>word.Length>=3&&!new[]{"PAGAMENTO","TRANSFERENCIA","DEBITO","PIX","BANCO","ITAU","TED","DOC"}.Contains(word)).ToHashSet();
    }
    public object PreviewOfx(int company,OfxPreviewInput x)=>Write(c=> {
        Company(c,company);Existing(c,"accounts",x.AccountId,company);var account=Query(c,"SELECT * FROM accounts WHERE id=@p0",x.AccountId)[0];
        byte[] content;try{content=Convert.FromBase64String(x.DataBase64??"");}catch{throw new ArgumentException("Não foi possível ler o arquivo OFX.");}
        var parsed=ParseOfx(content);var debits=parsed.Transactions.Where(t=>t.Amount<0).ToList();if(debits.Count==0)throw new ArgumentException("Este OFX não possui débitos para conciliar com contas a pagar.");
        var fingerprint=Convert.ToHexString(SHA256.HashData(content));var existingImport=Query(c,"SELECT id FROM ofx_imports WHERE companyId=@p0 AND accountId=@p1 AND fingerprint=@p2",company,x.AccountId,fingerprint).SingleOrDefault();
        long importId;if(existingImport is null){Run(c,"INSERT INTO ofx_imports(companyId,accountId,fileName,fingerprint,bankId,periodStart,periodEnd) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6)",company,x.AccountId,Required(x.FileName,"Nome do arquivo"),fingerprint,parsed.BankId,parsed.Start,parsed.End);importId=Number(Query(c,"SELECT last_insert_rowid() id")[0],"id");}else importId=Number(existingImport,"id");
        foreach(var item in debits)if(Query(c,"SELECT id FROM ofx_transactions WHERE companyId=@p0 AND accountId=@p1 AND fitId=@p2",company,x.AccountId,item.FitId).Count==0)Run(c,"INSERT INTO ofx_transactions(importId,companyId,accountId,fitId,date,amount,memo,transactionType) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)",importId,company,x.AccountId,item.FitId,item.Date,item.Amount,item.Memo,item.Type);
        var openTitles=Query(c,"""SELECT t.id,t.description,t.due,COALESCE(p.name,'SEM FORNECEDOR') partyName,t.amount-COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) remaining FROM titles t LEFT JOIN parties p ON p.id=t.partyId WHERE t.companyId=@p0 AND t.kind='payable' AND t.deleted=0 AND t.cancelled=0 AND t.amount>COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) ORDER BY t.due,t.id""",company);
        var rows=new List<object>();var duplicates=0;var suggested=0;
        foreach(var parsedItem in debits){var stored=Query(c,"SELECT * FROM ofx_transactions WHERE companyId=@p0 AND accountId=@p1 AND fitId=@p2",company,x.AccountId,parsedItem.FitId)[0];var otherImport=Number(stored,"importId")!=importId;var reconciled=stored["settlementId"] is not null;var duplicate=otherImport||reconciled;if(duplicate)duplicates++;var cash=-parsedItem.Amount;var words=MatchWords(parsedItem.Memo);
            var ranked=openTitles.Select(title=>{var remaining=Number(title,"remaining");var days=Math.Abs(Date((string)title["due"]!).DayNumber-Date(parsedItem.Date).DayNumber);var common=words.Intersect(MatchWords($"{title["description"]} {title["partyName"]}")).Count();var exact=remaining==cash;var score=(exact?100:Math.Abs(remaining-cash)<=Math.Max(100,cash/100)?35:0)+(days<=3?25:days<=7?15:days<=30?5:0)+Math.Min(30,common*10);return new{title,score,exact,days};}).OrderByDescending(v=>v.score).ThenBy(v=>v.days).ToList();
            var best=ranked.FirstOrDefault();var recommend=!duplicate&&best is not null&&best.exact;if(recommend)suggested++;
            rows.Add(new{id=Number(stored,"id"),date=parsedItem.Date,amount=cash,memo=parsedItem.Memo,duplicate,reconciled,suggestedTitleId=recommend?(long?)Number(best!.title,"id"):null,suggestedDescription=recommend?(string)best!.title["description"]!:"",suggestedParty=recommend?(string)best!.title["partyName"]!:"",suggestedDue=recommend?(string)best!.title["due"]!:"",suggestedRemaining=recommend?Number(best!.title,"remaining"):0});}
        Audit(c,company,"Importação OFX",new{importId,file=x.FileName,accountId=x.AccountId,debits=debits.Count,duplicates,suggested});return new{importId,fileName=x.FileName,accountId=x.AccountId,accountName=account["name"],bankId=parsed.BankId,periodStart=parsed.Start,periodEnd=parsed.End,total=debits.Count,duplicates,suggested,transactions=rows};
    });
    public object ConfirmOfx(int company,OfxConfirmInput x)=>Write(c=> {
        Company(c,company);if(x.Items is null||x.Items.Length==0||x.Items.Length>500)throw new ArgumentException("Selecione ao menos uma movimentação para baixar.");if(x.Items.Select(i=>i.TransactionId).Distinct().Count()!=x.Items.Length)throw new ArgumentException("Há movimentações repetidas na seleção.");
        var import=Query(c,"SELECT * FROM ofx_imports WHERE id=@p0 AND companyId=@p1",x.ImportId,company).SingleOrDefault()??throw new ArgumentException("Importação OFX não encontrada.");var accountId=Convert.ToInt32(Number(import,"accountId"));var settlementIds=new List<long>();long total=0;
        foreach(var item in x.Items){var transaction=Query(c,"SELECT * FROM ofx_transactions WHERE id=@p0 AND importId=@p1 AND companyId=@p2",item.TransactionId,x.ImportId,company).SingleOrDefault()??throw new ArgumentException("Movimentação OFX não pertence a esta importação.");if(transaction["settlementId"] is not null)throw new ArgumentException("Uma das movimentações selecionadas já foi conciliada.");var cash=-Number(transaction,"amount");if(cash<=0)throw new ArgumentException("Somente débitos podem baixar contas a pagar nesta versão.");var title=Query(c,TitlesSql,company).SingleOrDefault(t=>Number(t,"id")==item.TitleId)??throw new ArgumentException("Conta a pagar não encontrada.");if((title["kind"] as string)!="payable"||Number(title,"cancelled")!=0||Number(title,"remaining")<=0)throw new ArgumentException("Selecione uma conta a pagar que esteja em aberto.");var principal=Math.Min(cash,Number(title,"remaining"));var additional=Math.Max(0,cash-principal);var settlementId=SettleCore(c,company,item.TitleId,new(accountId,principal,0,0,0,(string)transaction["date"]!,additional));Run(c,"UPDATE ofx_transactions SET settlementId=@p0 WHERE id=@p1",settlementId,item.TransactionId);settlementIds.Add(settlementId);total=checked(total+cash);}
        Audit(c,company,"Conciliação OFX",new{importId=x.ImportId,count=x.Items.Length,total,settlementIds});return new{ok=true,count=x.Items.Length,total,settlementIds};
    });
    public object Duplicate(int company,int id,DuplicateInput x)=>Write(c=>{
        Existing(c,"titles",id,company);var t=Query(c,"SELECT * FROM titles WHERE id=@p0 AND deleted=0",id).SingleOrDefault()??throw new ArgumentException("Lançamento não encontrado.");var due=string.IsNullOrWhiteSpace(x.Due)?(string)t["due"]!:Date(x.Due).ToString("yyyy-MM-dd");var description=Regex.Replace((string)t["description"]!,@" \(\d+/\d+\)$","")+" - CÓPIA";
        Run(c,"INSERT INTO titles(companyId,kind,description,amount,due,partyId,category,notes,installmentNumber,installmentCount,interestRate,penaltyRate,discountRate,issueDate,branch,costCenter,paymentMethodId) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,1,1,@p8,@p9,@p10,@p11,@p12,@p13,@p14)",company,t["kind"],description,t["amount"],due,t["partyId"],t["category"],t["notes"],t["interestRate"],t["penaltyRate"],t["discountRate"],t["issueDate"],t["branch"],t["costCenter"],t["paymentMethodId"]);var newId=Number(Query(c,"SELECT last_insert_rowid() AS id")[0],"id");Audit(c,company,"Duplicação",new{sourceId=id,titleId=newId,due});return new{ok=true,id=newId};
    });
    public object Reverse(int company,int id,ReasonInput x)=>Write(c=> {
        Existing(c,"settlements",id,company);var reason=Required(x.Reason,"Motivo");var s=Query(c,"SELECT * FROM settlements WHERE id=@p0",id)[0];
        if(Number(s,"reversed")!=0)throw new ArgumentException("Esta baixa já foi estornada.");
        Run(c,"UPDATE settlements SET reversed=1,reason=@p0 WHERE id=@p1",reason,id);
        Run(c,"UPDATE ofx_transactions SET settlementId=NULL WHERE settlementId=@p0",id);
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
    public object Edit(int company,int id,EditTitleInput x,AuthIdentity? actor=null)=>Write(c=> {
        if(x.Scope!="single" && x.Scope!="all_open")throw new ArgumentException("Escolha editar somente esta parcela ou todas as parcelas sem baixa.");
        Money(x.Amount,true);var due=Date(x.Due);var description=Required(x.Description,"Descrição");
        if(x.PartyId.HasValue)Existing(c,"parties",x.PartyId.Value,company);
        if(x.PaymentMethodId.HasValue)Existing(c,"payment_methods",x.PaymentMethodId.Value,company);
        var costCenter=RequiredOrEmpty(x.CostCenter);var existingCostCenter=costCenter.Length==0||Query(c,"SELECT id FROM cost_centers WHERE companyId=@p0 AND upper(name)=upper(@p1)",company,costCenter).Count>0;if(!existingCostCenter&&actor is not null&&IsFinancialUser(actor,company))throw new ArgumentException("Selecione um centro de custo já cadastrado pelo administrador.");if(!existingCostCenter)Run(c,"INSERT INTO cost_centers(companyId,name) VALUES(@p0,@p1)",company,costCenter);
        var all=Query(c,TitlesSql,company);var selected=all.SingleOrDefault(r=>Number(r,"id")==id)??throw new ArgumentException("Lançamento não encontrado.");
        if(Number(selected,"cancelled")!=0||Number(selected,"settled")!=0)throw new ArgumentException("Só é possível editar um lançamento sem baixa e que não esteja cancelado.");
        var group=selected["installmentGroup"] as string;
        if(x.Scope=="all_open" && group is null)throw new ArgumentException("Este lançamento não possui parcelas vinculadas.");
        var targets=x.Scope=="all_open" ? all.Where(r=>(string?)r["installmentGroup"]==group && Number(r,"cancelled")==0 && Number(r,"settled")==0).ToList() : new(){selected};
        var selectedNumber=Convert.ToInt32(selected["installmentNumber"]);
        foreach(var target in targets) {
            var number=Convert.ToInt32(target["installmentNumber"]);var count=Convert.ToInt32(target["installmentCount"]);
            var label=description+(count>1?$" ({number}/{count})":"");var targetDue=due.AddMonths(number-selectedNumber).ToString("yyyy-MM-dd");
            Run(c,"UPDATE titles SET description=@p0,amount=@p1,due=@p2,partyId=@p3,category=@p4,notes=@p5,documentNumber=@p6,issueDate=@p7,branch=@p8,costCenter=@p9,paymentMethodId=@p10 WHERE id=@p11",label,x.Amount,targetDue,x.PartyId,(x.Category??"").ToUpperInvariant(),(x.Notes??"").ToUpperInvariant(),RequiredOrEmpty(x.DocumentNumber),string.IsNullOrWhiteSpace(x.IssueDate)?"":Date(x.IssueDate).ToString("yyyy-MM-dd"),RequiredOrEmpty(x.Branch),costCenter,x.PaymentMethodId,Number(target,"id"));
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
    public object Adjust(int company,int accountId,AdjustmentInput x)=>Write(c=>{
        Existing(c,"accounts",accountId,company);if(x.Amount==0)throw new ArgumentException("Informe um valor diferente de zero.");Money(Math.Abs(x.Amount),true);var date=Date(x.Date);if(date>DateOnly.FromDateTime(DateTime.Today))throw new ArgumentException("O ajuste não pode ter data futura.");var reason=Required(x.Reason,"Justificativa");
        Run(c,"INSERT INTO movements(companyId,accountId,amount,date,description) VALUES(@p0,@p1,@p2,@p3,@p4)",company,accountId,x.Amount,date.ToString("yyyy-MM-dd"),"AJUSTE DE SALDO: "+reason);Audit(c,company,"Ajuste de saldo",new{accountId,x.Amount,date=x.Date,reason});return new{ok=true};
    });
    public object AddAttachment(int company,AttachmentInput x)=>Write(c=>{
        Company(c,company);if((x.TitleId.HasValue?1:0)+(x.SettlementId.HasValue?1:0)!=1)throw new ArgumentException("Vincule o anexo a um lançamento ou a uma baixa.");if(x.TitleId.HasValue)Existing(c,"titles",x.TitleId.Value,company);if(x.SettlementId.HasValue)Existing(c,"settlements",x.SettlementId.Value,company);
        byte[] data;try{data=Convert.FromBase64String(x.DataBase64??"");}catch(FormatException){throw new ArgumentException("Arquivo inválido.");}if(data.Length==0||data.Length>10*1024*1024)throw new ArgumentException("O anexo deve ter até 10 MB.");var name=Path.GetFileName(Required(x.Name,"Nome do arquivo"));var mime=string.IsNullOrWhiteSpace(x.MimeType)?"application/octet-stream":x.MimeType.Trim().ToLowerInvariant();
        Run(c,"INSERT INTO attachments(companyId,titleId,settlementId,name,mimeType,data) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)",company,x.TitleId,x.SettlementId,name,mime,data);var id=Number(Query(c,"SELECT last_insert_rowid() AS id")[0],"id");Audit(c,company,"Anexo incluído",new{id,x.TitleId,x.SettlementId,name,size=data.Length});return new{ok=true,id};
    });
    public AttachmentFile Attachment(int company,int id){lock(gate){using var c=Open();Existing(c,"attachments",id,company);var row=Query(c,"SELECT name,mimeType,data FROM attachments WHERE id=@p0",id)[0];return new((byte[])row["data"]!,(string)row["mimeType"]!,(string)row["name"]!);}}
    public object DeleteAttachment(int company,int id)=>Write(c=>{Existing(c,"attachments",id,company);var name=(string)Query(c,"SELECT name FROM attachments WHERE id=@p0",id)[0]["name"]!;Run(c,"DELETE FROM attachments WHERE id=@p0",id);Audit(c,company,"Anexo excluído",new{id,name});return new{ok=true};});
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
    public string Backup() {lock(gate){var folder=Path.Combine(Path.GetDirectoryName(path)!,"backups");Directory.CreateDirectory(folder);var target=Path.Combine(folder,$"financeiro-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");using var src=Open();using var dest=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=target,Pooling=false}.ToString());dest.Open();src.BackupDatabase(dest);return target;}}
    public object PrepareGestoresImport(GestoresImportInput x,AuthIdentity actor){if(!actor.GlobalAdmin)throw new ArgumentException("Somente o administrador geral pode recuperar um backup do Gestores.");lock(gate)return GestoresBackupImporter.Prepare(path,x);}
    public object CommitGestoresImport(GestoresImportCommitInput x,AuthIdentity actor){
        if(!actor.GlobalAdmin)throw new ArgumentException("Somente o administrador geral pode recuperar um backup do Gestores.");
        lock(gate){var prepared=GestoresBackupImporter.PreparedPath(path,x.Token);using(var check=new SqliteConnection($"Data Source={prepared};Mode=ReadOnly")){check.Open();if(!string.Equals(Query(check,"PRAGMA integrity_check")[0].Values.FirstOrDefault()?.ToString(),"ok",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("O backup convertido não passou na verificação de integridade.");foreach(var table in new[]{"companies","titles","accounts","settlements","movements","audit","users"})if(Query(check,"SELECT name FROM sqlite_master WHERE type='table' AND name=@p0",table).Count==0)throw new ArgumentException("O backup convertido está incompleto.");}
            var safety=Backup();SqliteConnection.ClearAllPools();foreach(var suffix in new[]{"-wal","-shm"}){var side=path+suffix;if(File.Exists(side))File.Delete(side);}File.Copy(prepared,path,true);using(var restored=Open()){var companyRow=Query(restored,"SELECT id FROM companies ORDER BY id LIMIT 1").FirstOrDefault();if(companyRow is not null)Audit(restored,Convert.ToInt32(companyRow["id"]),"Recuperação do Gestores",new{copiaDeSeguranca=safety});}try{File.Delete(prepared);}catch{}return new{ok=true,safetyBackup=safety};
        }
    }
    public object Restore(RestoreInput x,AuthIdentity actor){lock(gate){
        if(!actor.GlobalAdmin)throw new ArgumentException("Somente o administrador geral pode restaurar um backup completo.");
        byte[] data;try{data=Convert.FromBase64String(x.DataBase64??"");}catch(FormatException){throw new ArgumentException("Arquivo de backup inválido.");}if(data.Length<4096||data.Length>100*1024*1024)throw new ArgumentException("O backup deve ter entre 4 KB e 100 MB.");
        var temp=Path.Combine(Path.GetDirectoryName(path)!,$"restore-{Guid.NewGuid():N}.db");File.WriteAllBytes(temp,data);try{using(var check=new SqliteConnection($"Data Source={temp};Mode=ReadOnly")){check.Open();var integrity=Query(check,"PRAGMA integrity_check");if(integrity.Count==0||!string.Equals(integrity[0].Values.FirstOrDefault()?.ToString(),"ok",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("O arquivo não passou na verificação de integridade.");foreach(var table in new[]{"companies","titles","accounts","settlements","movements","audit","users"})if(Query(check,"SELECT name FROM sqlite_master WHERE type='table' AND name=@p0",table).Count==0)throw new ArgumentException("O arquivo não é um backup compatível do Monalisa Financeiro.");}
            var safety=Backup();SqliteConnection.ClearAllPools();foreach(var suffix in new[]{"-wal","-shm"}){var side=path+suffix;if(File.Exists(side))File.Delete(side);}File.Copy(temp,path,true);using(var restored=Open()){var companyRow=Query(restored,"SELECT id FROM companies ORDER BY id LIMIT 1").FirstOrDefault();if(companyRow is not null)Audit(restored,Convert.ToInt32(companyRow["id"]),"Restauração de backup",new{arquivo=Path.GetFileName(x.Name),copiaDeSeguranca=safety});}return new{ok=true,safetyBackup=safety};
        }finally{if(File.Exists(temp))File.Delete(temp);}
    }}
}

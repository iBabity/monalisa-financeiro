using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("FINANCE_URL") ?? "http://127.0.0.1:5187");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 700L * 1024 * 1024);
builder.Services.AddSingleton<Ledger>();
builder.Services.AddSingleton<AuthSessions>();
builder.Services.AddSingleton<LicenseGuard>();
builder.Services.AddSingleton<PluggySandbox>();
var app = builder.Build();
var localNoLogin = Environment.GetEnvironmentVariable("MONALISA_NO_LOGIN") == "1";
var networkRole = (Environment.GetEnvironmentVariable("MONALISA_NETWORK_ROLE") ?? "standalone").Trim().ToLowerInvariant();
var dataServerUrl = (Environment.GetEnvironmentVariable("MONALISA_DATA_SERVER_URL") ?? "").Trim().TrimEnd('/');
var networkKey = (Environment.GetEnvironmentVariable("MONALISA_NETWORK_KEY") ?? "").Trim();
var sharedPort = int.TryParse(Environment.GetEnvironmentVariable("MONALISA_NETWORK_SHARED_PORT"), out var configuredSharedPort) ? configuredSharedPort : 5188;
var clientMode = networkRole == "client" && Uri.TryCreate(dataServerUrl, UriKind.Absolute, out _) && networkKey.Length >= 24;
var serverMode = networkRole == "server" && networkKey.Length >= 24;
var proxyClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(12) };
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (ctx, next) =>
{
    var origin = ctx.Request.Headers.Origin.ToString();
    var localOrigin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    if (origin.Length > 0 && !string.Equals(origin, localOrigin, StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 403; return; }
    try { await next(); }
    catch (ArgumentException ex) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = "Registro duplicado ou referência inválida." }); }
    catch (HttpRequestException) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsJsonAsync(new { error = "Não foi possível acessar o Painel Central. Verifique se este computador está na mesma rede e se o endereço do painel está correto." }); }
    catch (TaskCanceledException) { ctx.Response.StatusCode = 504; await ctx.Response.WriteAsJsonAsync(new { error = "O Painel Central demorou para responder. Verifique a conexão de rede e tente novamente." }); }
    catch (Exception ex) { app.Logger.LogError(ex, "Erro não tratado em {Method} {Path}", ctx.Request.Method, ctx.Request.Path); ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { error = "O serviço local encontrou um erro ao concluir a operação. Feche e abra o Monalisa Financeiro e tente novamente." }); }
});
app.Use(async (ctx, next) =>
{
    if (!serverMode || ctx.Connection.LocalPort != sharedPort || !ctx.Request.Path.StartsWithSegments("/api")) { await next(); return; }
    if (IsSameOriginBrowserRequest(ctx)) { await next(); return; }
    var supplied = ctx.Request.Headers["X-Monalisa-Network-Key"].ToString();
    if (!FixedTimeTextEquals(supplied, networkKey)) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "ESTA MÁQUINA NÃO ESTÁ AUTORIZADA A USAR OS DADOS COMPARTILHADOS." }); return; }
    await next();
});
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/api/health") ||
       ctx.Request.Path.StartsWithSegments("/api/license") || ctx.Request.Path.StartsWithSegments("/api/auth/bootstrap") || ctx.Request.Path.StartsWithSegments("/api/auth/login") || ctx.Request.Path.StartsWithSegments("/api/auth/local")) { await next(); return; }
    if (clientMode) { await next(); return; }
    var db = ctx.RequestServices.GetRequiredService<Ledger>();
    if (Environment.GetEnvironmentVariable("FINANCE_AUTH_DISABLED") == "1") { await next(); return; }
    var needsSetup = !db.HasUsers();
    var sessions = ctx.RequestServices.GetRequiredService<AuthSessions>();
    var header = ctx.Request.Headers.Authorization.ToString();
    var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : "";
    if (!sessions.TryGet(token, out var sessionIdentity))
    {
        ctx.Response.StatusCode = needsSetup ? 428 : 401; await ctx.Response.WriteAsJsonAsync(new { error = needsSetup ? "Crie o administrador responsável por esta instalação." : "Faça login para continuar." }); return;
    }
    var identity = db.SessionUser(sessionIdentity.Id);
    if (identity is null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "Faça login para continuar." }); return; }
    ctx.Items["user"] = identity;
    var match = Regex.Match(ctx.Request.Path.Value ?? "", @"^/api/companies/(\d+)");
    if (match.Success && int.TryParse(match.Groups[1].Value, out var companyId) && !identity.GlobalAdmin && !db.CanAccessCompany(identity.Id, companyId))
    {
        ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "Você não possui acesso a esta empresa." }); return;
    }
    if (ctx.Request.Method == "POST" && ctx.Request.Path == "/api/companies" && !identity.GlobalAdmin)
    {
        ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "Somente o administrador geral pode criar empresas." }); return;
    }
    if (ctx.Request.Path == "/api/users" && !identity.GlobalAdmin)
    {
        ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "Somente o administrador geral pode consultar todos os acessos." }); return;
    }
    if (match.Success && Regex.IsMatch(ctx.Request.Path.Value ?? "", @"^/api/companies/\d+/users(?:/|$)") &&
       int.TryParse(match.Groups[1].Value, out var accessCompanyId) && !db.CanManageCompanyAccess(identity, accessCompanyId))
    {
        ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "Este perfil não possui acesso à administração da empresa." }); return;
    }
    if (ctx.Request.Method == "POST" && ctx.Request.Path == "/api/backup" && !db.HasAdministrativeAccess(identity))
    {
        ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "O usuário financeiro não possui permissão para criar backup." }); return;
    }
    if (match.Success && int.TryParse(match.Groups[1].Value, out var protectedCompanyId) && db.IsFinancialUser(identity, protectedCompanyId))
    {
        var path = ctx.Request.Path.Value ?? "";
        if (ctx.Request.Method == "POST" && Regex.IsMatch(path, @"^/api/companies/\d+/cost-centers(?:/|$)"))
        {
            ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "O usuário financeiro não possui permissão para administrar centros de custo." }); return;
        }
        if (ctx.Request.Method == "POST" && Regex.IsMatch(path, @"^/api/companies/\d+/plans(?:/|$)") && !db.CanManagePlanRates(identity, protectedCompanyId))
        {
            ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "O administrador da empresa bloqueou a alteração de juros, multa e desconto dos planos de contas." }); return;
        }
    }
    await next();
});
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/api/health") || ctx.Request.Path.StartsWithSegments("/api/license") || ctx.Request.Path.StartsWithSegments("/api/auth")) { await next(); return; }
    var license = await ctx.RequestServices.GetRequiredService<LicenseGuard>().Status();
    if (!clientMode) ctx.RequestServices.GetRequiredService<Ledger>().ApplyLicensedCompany(license);
    if (license.Status == "blocked" || license.Status == "activation_required") { ctx.Response.StatusCode = 423; await ctx.Response.WriteAsJsonAsync(new { error = license.Message, code = "LICENSE_BLOCKED", license }); return; }
    var allowedReadOnly = ctx.Request.Path.StartsWithSegments("/api/auth") || ctx.Request.Path == "/api/backup";
    if (license.Status == "read_only" && ctx.Request.Method != "GET" && !allowedReadOnly) { ctx.Response.StatusCode = 423; await ctx.Response.WriteAsJsonAsync(new { error = license.Message.Length > 0 ? license.Message : "Licença em modo somente consulta.", code = "LICENSE_READ_ONLY", license }); return; }
    ctx.Items["license"] = license; await next();
});
app.Use(async (ctx, next) =>
{
    if (!clientMode || !ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/api/health") || ctx.Request.Path.StartsWithSegments("/api/license")) { await next(); return; }
    await ProxyToDataServer(ctx, dataServerUrl, networkKey, proxyClient);
});
app.MapGet("/api/health", () => new { status = "ok", mode = networkRole, sharedData = clientMode || serverMode, dataServer = clientMode ? dataServerUrl : "" });
app.MapGet("/api/health/mobile-access", () => new
{
    enabled = serverMode,
    url = serverMode ? (Environment.GetEnvironmentVariable("MONALISA_NETWORK_SERVER_URL") ?? "").Trim().TrimEnd('/') : ""
});
app.MapGet("/api/license/status", async (LicenseGuard license, Ledger db) => { var result = await license.Status(); db.ApplyLicensedCompany(result); return result; });
app.MapPost("/api/license/activate", async (LicenseActivationInput x, LicenseGuard license, Ledger db) => { var result = await license.Activate(x.ActivationKey); db.ApplyLicensedCompany(result); return result; });
app.MapPost("/api/license/change", async (LicenseActivationInput x, LicenseGuard license, Ledger db) => { var result = await license.Change(x.ActivationKey); db.ApplyLicensedCompany(result); return result; });
app.MapPost("/api/license/refresh", async (LicenseGuard license, Ledger db) => { var result = await license.Status(true); db.ApplyLicensedCompany(result); return result; });
app.MapGet("/api/auth/bootstrap", (Ledger db) => new { needsSetup = !localNoLogin && !db.HasUsers(), noLogin = localNoLogin });
app.MapPost("/api/auth/bootstrap", (BootstrapInput x, Ledger db, AuthSessions sessions) =>
{
    var user = db.SetupAdministrator(x); var token = sessions.Create(user); return new { token, user = db.AccessContext(user) };
});
app.MapPost("/api/auth/login", (LoginInput x, Ledger db, AuthSessions sessions) =>
{
    var user = db.Authenticate(x);
    var token = sessions.Create(user); return new { token, user = db.AccessContext(user) };
});
app.MapPost("/api/auth/local", (Ledger db, AuthSessions sessions) =>
{
    if (!localNoLogin) throw new ArgumentException("O acesso automático não está habilitado nesta instalação.");
    var user = db.LocalAdministrator(); var token = sessions.Create(user); return new { token, user = db.AccessContext(user) };
});
app.MapPost("/api/auth/logout", (HttpContext context, AuthSessions sessions) =>
{
    var header = context.Request.Headers.Authorization.ToString(); if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) sessions.Remove(header[7..].Trim()); return new { ok = true };
});
app.MapGet("/api/auth/me", (HttpContext context, Ledger db) => db.AccessContext(CurrentUser(context)));
app.MapGet("/api/users", (HttpContext context, Ledger db) => db.AllUsers(CurrentUser(context)));
app.MapGet("/api/companies", (HttpContext context, Ledger db) => db.Companies(context.Items["user"] as AuthIdentity));
app.MapGet("/api/cnpj/{cnpj}", async (string cnpj) => await LookupCnpj(cnpj));
app.MapPost("/api/companies", (CompanyInput x, Ledger db) => db.CreateCompany(x));
app.MapGet("/api/companies/{companyId:int}/state", (int companyId, HttpContext context, Ledger db) => db.State(companyId, context.Items["user"] as AuthIdentity));
app.MapGet("/api/companies/{companyId:int}/users", (int companyId, HttpContext context, Ledger db) => db.CompanyUsers(companyId, CurrentUser(context)));
app.MapPost("/api/companies/{companyId:int}/users", (int companyId, UserAccessInput x, HttpContext context, Ledger db) => db.CreateCompanyUser(companyId, x, CurrentUser(context)));
app.MapPost("/api/companies/{companyId:int}/users/{userId:int}/link", (int companyId, int userId, LinkUserInput x, HttpContext context, Ledger db) => db.LinkCompanyUser(companyId, userId, x, CurrentUser(context)));
app.MapPost("/api/companies/{companyId:int}/users/{userId:int}/access", (int companyId, int userId, AccessUpdateInput x, HttpContext context, Ledger db) => db.UpdateCompanyUser(companyId, userId, x, CurrentUser(context)));
app.MapGet("/api/companies/{companyId:int}/reports/financial", (int companyId, string start, string end, HttpContext context, Ledger db) =>
{
    var report = db.FinancialReport(companyId, start, end);
    context.Response.Headers.ContentDisposition = $"inline; filename=\"{report.FileName}\"";
    return Results.File(report.Content, "application/pdf");
});
app.MapGet("/api/companies/{companyId:int}/reports/payables", (int companyId, string month, string type, HttpContext context, Ledger db) =>
{
    var report = db.PayableReport(companyId, month, type);
    context.Response.Headers.ContentDisposition = $"inline; filename=\"{report.FileName}\"";
    return Results.File(report.Content, "application/pdf");
});
app.MapGet("/api/companies/{companyId:int}/reports/receivables", (int companyId, string month, string type, HttpContext context, Ledger db) =>
{
    var report = db.ReceivableReport(companyId, month, type);
    context.Response.Headers.ContentDisposition = $"inline; filename=\"{report.FileName}\"";
    return Results.File(report.Content, "application/pdf");
});
app.MapGet("/api/companies/{companyId:int}/reports/advanced", (int companyId, string type, string start, string end, int? partyId, int? accountId, HttpContext context, Ledger db) => { var report = db.AdvancedReport(companyId, type, start, end, partyId, accountId); context.Response.Headers.ContentDisposition = $"inline; filename=\"{report.FileName}\""; return Results.File(report.Content, "application/pdf"); });
app.MapGet("/api/companies/{companyId:int}/exports/csv", (int companyId, string type, string start, string end, int? partyId, int? accountId, HttpContext context, Ledger db) => { var file = db.ExportCsv(companyId, type, start, end, partyId, accountId); context.Response.Headers.ContentDisposition = $"attachment; filename=\"{file.FileName}\""; return Results.File(file.Content, "text/csv; charset=utf-8"); });
app.MapPost("/api/companies/{companyId:int}/parties", (int companyId, PartyInput x, Ledger db) => db.Party(companyId, x));
app.MapPost("/api/companies/{companyId:int}/parties/{id:int}/edit", (int companyId, int id, PartyInput x, Ledger db) => db.EditParty(companyId, id, x));
app.MapPost("/api/companies/{companyId:int}/parties/{id:int}/delete", (int companyId, int id, Ledger db) => db.DeleteParty(companyId, id));
app.MapPost("/api/companies/{companyId:int}/accounts", (int companyId, AccountInput x, Ledger db) => db.Account(companyId, x));
app.MapPost("/api/companies/{companyId:int}/accounts/{id:int}/edit", (int companyId, int id, AccountInput x, Ledger db) => db.EditAccount(companyId, id, x));
app.MapPost("/api/companies/{companyId:int}/accounts/{id:int}/delete", (int companyId, int id, Ledger db) => db.DeleteAccount(companyId, id));
app.MapPost("/api/companies/{companyId:int}/accounts/{id:int}/adjust", (int companyId, int id, AdjustmentInput x, Ledger db) => db.Adjust(companyId, id, x));
app.MapPost("/api/companies/{companyId:int}/plans", (int companyId, PlanInput x, Ledger db) => db.Plan(companyId, x));
app.MapPost("/api/companies/{companyId:int}/plans/{id:int}/edit", (int companyId, int id, PlanInput x, Ledger db) => db.EditPlan(companyId, id, x));
app.MapPost("/api/companies/{companyId:int}/plans/{id:int}/delete", (int companyId, int id, Ledger db) => db.DeletePlan(companyId, id));
app.MapPost("/api/companies/{companyId:int}/payment-methods", (int companyId, NamedKindInput x, Ledger db) => db.PaymentMethod(companyId, x));
app.MapPost("/api/companies/{companyId:int}/payment-methods/{id:int}/edit", (int companyId, int id, NamedKindInput x, Ledger db) => db.EditPaymentMethod(companyId, id, x));
app.MapPost("/api/companies/{companyId:int}/payment-methods/{id:int}/delete", (int companyId, int id, Ledger db) => db.DeletePaymentMethod(companyId, id));
app.MapPost("/api/companies/{companyId:int}/cost-centers", (int companyId, NamedInput x, Ledger db) => db.CostCenter(companyId, x));
app.MapPost("/api/companies/{companyId:int}/cost-centers/{id:int}/edit", (int companyId, int id, NamedInput x, Ledger db) => db.EditCostCenter(companyId, id, x));
app.MapPost("/api/companies/{companyId:int}/cost-centers/{id:int}/delete", (int companyId, int id, Ledger db) => db.DeleteCostCenter(companyId, id));
app.MapPost("/api/companies/{companyId:int}/credit-cards", (int companyId, CreditCardInput x, Ledger db) => db.CreditCard(companyId, x));
app.MapPost("/api/companies/{companyId:int}/credit-cards/{cardId:int}/edit", (int companyId, int cardId, CreditCardInput x, Ledger db) => db.EditCreditCard(companyId, cardId, x));
app.MapPost("/api/companies/{companyId:int}/credit-cards/{cardId:int}/purchases", (int companyId, int cardId, CreditCardPurchaseInput x, Ledger db) => db.CreditCardPurchase(companyId, cardId, x));
app.MapPost("/api/companies/{companyId:int}/credit-card-purchases/{purchaseGroup}/delete", (int companyId, string purchaseGroup, CreditCardPurchaseDeleteInput x, Ledger db) => db.DeleteCreditCardPurchase(companyId, purchaseGroup, x));
app.MapPost("/api/companies/{companyId:int}/credit-card-recurrences/refresh", (int companyId, Ledger db) => db.RefreshCreditCardRecurrences(companyId));
app.MapPost("/api/companies/{companyId:int}/credit-card-recurrences/{recurrenceId:int}/stop", (int companyId, int recurrenceId, ReasonInput x, Ledger db) => db.StopCreditCardRecurrence(companyId, recurrenceId, x));
app.MapPost("/api/companies/{companyId:int}/titles", (int companyId, TitleInput x, HttpContext context, Ledger db) => db.Title(companyId, x, context.Items["user"] as AuthIdentity));
app.MapPost("/api/companies/{companyId:int}/titles/{id:int}/duplicate", (int companyId, int id, DuplicateInput x, Ledger db) => db.Duplicate(companyId, id, x));
app.MapPost("/api/companies/{companyId:int}/titles/{id:int}/settle", (int companyId, int id, SettlementInput x, Ledger db) => db.Settle(companyId, id, x));
app.MapPost("/api/companies/{companyId:int}/settlements/bulk", (int companyId, BulkSettlementInput x, Ledger db) => db.BulkSettle(companyId, x));
app.MapPost("/api/companies/{companyId:int}/settlements/{id:int}/reverse", (int companyId, int id, ReasonInput x, Ledger db) => db.Reverse(companyId, id, x));
app.MapPost("/api/companies/{companyId:int}/ofx/preview", (int companyId, OfxPreviewInput x, Ledger db) => db.PreviewOfx(companyId, x));
app.MapPost("/api/companies/{companyId:int}/ofx/confirm", (int companyId, OfxConfirmInput x, Ledger db) => db.ConfirmOfx(companyId, x));
app.MapPost("/api/companies/{companyId:int}/open-finance/sandbox/connect", async (int companyId, HttpContext ctx, Ledger db, PluggySandbox pluggy) =>
{
    RequireLocalSandbox(ctx, db, companyId);
    return await pluggy.Connect(companyId);
});
app.MapGet("/api/companies/{companyId:int}/open-finance/sandbox", async (int companyId, HttpContext ctx, Ledger db, PluggySandbox pluggy) =>
{
    RequireLocalSandbox(ctx, db, companyId);
    return await pluggy.Snapshot(companyId);
});
app.MapPost("/api/companies/{companyId:int}/titles/{id:int}/cancel", (int companyId, int id, CancelInput x, Ledger db) => db.Cancel(companyId, id, x));
app.MapPost("/api/companies/{companyId:int}/titles/{id:int}/edit", (int companyId, int id, EditTitleInput x, HttpContext context, Ledger db) => db.Edit(companyId, id, x, context.Items["user"] as AuthIdentity));
app.MapPost("/api/companies/{companyId:int}/titles/{id:int}/delete", (int companyId, int id, DeleteInput x, Ledger db) => db.Delete(companyId, id, x));
app.MapPost("/api/companies/{companyId:int}/transfers", (int companyId, TransferInput x, Ledger db) => db.Transfer(companyId, x));
app.MapPost("/api/companies/{companyId:int}/attachments", (int companyId, AttachmentInput x, Ledger db) => db.AddAttachment(companyId, x));
app.MapGet("/api/companies/{companyId:int}/attachments/{id:int}", (int companyId, int id, Ledger db) => { var file = db.Attachment(companyId, id); return Results.File(file.Content, file.MimeType, file.Name); });
app.MapPost("/api/companies/{companyId:int}/attachments/{id:int}/delete", (int companyId, int id, Ledger db) => db.DeleteAttachment(companyId, id));
app.MapPost("/api/backup", (Ledger db) => new { path = db.Backup() });
app.MapPost("/api/restore", (RestoreInput x, HttpContext context, Ledger db) => db.Restore(x, CurrentUser(context)));
app.MapPost("/api/import/gestores/prepare", (GestoresImportInput x, HttpContext context, Ledger db) => db.PrepareGestoresImport(x, CurrentUser(context)));
app.MapPost("/api/import/gestores/commit", (GestoresImportCommitInput x, HttpContext context, Ledger db) => db.CommitGestoresImport(x, CurrentUser(context)));
app.Services.GetRequiredService<Ledger>();
app.MapFallbackToFile("index.html");
app.Run();

static bool FixedTimeTextEquals(string supplied, string expected)
{
    var left = Encoding.UTF8.GetBytes(supplied); var right = Encoding.UTF8.GetBytes(expected);
    return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
}

static void RequireLocalSandbox(HttpContext context, Ledger db, int companyId)
{
    if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)) throw new ArgumentException("O teste Open Finance está disponível somente neste computador.");
    if (context.Items["user"] is AuthIdentity actor && db.IsFinancialUser(actor, companyId)) throw new ArgumentException("Apenas o administrador pode testar a conexão bancária.");
    if (db.Read($"SELECT id FROM companies WHERE id={companyId}") is not List<Dictionary<string, object?>> rows || rows.Count == 0) throw new ArgumentException("Empresa não encontrada.");
}
static bool IsSameOriginBrowserRequest(HttpContext context)
{
    if (string.Equals(context.Request.Headers["Sec-Fetch-Site"].ToString(), "same-origin", StringComparison.OrdinalIgnoreCase)) return true;
    var expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
    var origin = context.Request.Headers.Origin.ToString().TrimEnd('/');
    if (origin.Length > 0 && string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase)) return true;
    var referer = context.Request.Headers.Referer.ToString();
    return Uri.TryCreate(referer, UriKind.Absolute, out var refererUri)
        && string.Equals(refererUri.GetLeftPart(UriPartial.Authority), expectedOrigin, StringComparison.OrdinalIgnoreCase);
}

static async Task ProxyToDataServer(HttpContext context, string serverUrl, string networkKey, HttpClient client)
{
    var target = serverUrl + context.Request.Path + context.Request.QueryString;
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
    var hasBody = context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0;
    if (hasBody) request.Content = new StreamContent(context.Request.Body);
    foreach (var header in context.Request.Headers)
    {
        if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("X-Monalisa-Network-Key", StringComparison.OrdinalIgnoreCase)) continue;
        if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && request.Content is not null) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
    }
    request.Headers.TryAddWithoutValidation("X-Monalisa-Network-Key", networkKey);
    try
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers) if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) context.Response.Headers[header.Key] = header.Value.ToArray();
        foreach (var header in response.Content.Headers) if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) context.Response.Headers[header.Key] = header.Value.ToArray();
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    {
        context.Response.Clear(); context.Response.StatusCode = 504; await context.Response.WriteAsJsonAsync(new { error = "A MÁQUINA PRINCIPAL DEMOROU PARA RESPONDER. VERIFIQUE SE ELA ESTÁ LIGADA E CONECTADA À REDE." });
    }
    catch (HttpRequestException)
    {
        context.Response.Clear(); context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "NÃO FOI POSSÍVEL CONECTAR À MÁQUINA PRINCIPAL. VERIFIQUE SE ELA ESTÁ LIGADA E CONECTADA À REDE." });
    }
}

static async Task<object> LookupCnpj(string input)
{
    var cnpj = new string(input.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    if (cnpj.Length != 14) throw new ArgumentException("Informe o CNPJ completo para consultar.");
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MonalisaFinanceiro/1.0");
        using var response = await client.GetAsync($"https://brasilapi.com.br/api/cnpj/v1/{Uri.EscapeDataString(cnpj)}");
        if (!response.IsSuccessStatusCode) throw new ArgumentException("CNPJ não encontrado. Confira o número ou preencha os dados manualmente.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = json.RootElement;
        string Value(string key) => root.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        return new { cnpj, name = Value("razao_social"), tradeName = Value("nome_fantasia"), status = Value("descricao_situacao_cadastral"), city = Value("municipio"), state = Value("uf") };
    }
    catch (ArgumentException) { throw; }
    catch { throw new ArgumentException("Não foi possível consultar o CNPJ agora. Verifique a internet ou preencha os dados manualmente."); }
}

static AuthIdentity CurrentUser(HttpContext context) => context.Items["user"] as AuthIdentity
    ?? throw new ArgumentException("Faça login para continuar.");

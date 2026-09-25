using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder=WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("MONALISA_LICENSE_BIND")??"http://127.0.0.1:5200");
builder.Services.AddSingleton<LicenseStore>();
builder.Services.AddSingleton<AdminAccess>();
builder.Services.AddSingleton<AdminSessions>();
builder.Services.AddSingleton<LicenseSigner>();
var app=builder.Build();

app.Use(async(ctx,next)=>{try{await next();}catch(ArgumentException ex){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error=ex.Message});}catch(SqliteException ex)when(ex.SqliteErrorCode==19){ctx.Response.StatusCode=409;await ctx.Response.WriteAsJsonAsync(new{error="CNPJ ou licença já cadastrados."});}});
app.Use(async(ctx,next)=>{
    if(!ctx.Request.Path.StartsWithSegments("/api/admin")||ctx.Request.Path=="/api/admin/login"){await next();return;}
    var header=ctx.Request.Headers.Authorization.ToString();var token=header.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)?header[7..].Trim():"";
    if(!ctx.RequestServices.GetRequiredService<AdminSessions>().Valid(token)){ctx.Response.StatusCode=401;await ctx.Response.WriteAsJsonAsync(new{error="Entre novamente no Painel Central."});return;}
    await next();
});
app.UseDefaultFiles();app.UseStaticFiles();
app.MapGet("/api/health",()=>new{status="ok",service="monalisa-licensing"});
app.MapPost("/api/admin/login",(AdminLogin x,AdminAccess access,AdminSessions sessions)=>{access.Validate(x.Username,x.Pin);return new{token=sessions.Create(),user="ADMIN"};});
app.MapGet("/api/admin/licenses",(LicenseStore store)=>store.All());
app.MapPost("/api/admin/licenses",(CreateLicense x,LicenseStore store)=>store.Create(x));
app.MapPost("/api/admin/licenses/{id:int}/status",(int id,ChangeStatus x,LicenseStore store)=>store.ChangeStatus(id,x));
app.MapPost("/api/admin/licenses/{id:int}/reset-installation",(int id,LicenseStore store)=>store.ResetInstallation(id));
app.MapGet("/api/admin/audit",(LicenseStore store)=>store.Audit());
app.MapPost("/api/license/check",(LicenseCheck x,LicenseStore store,LicenseSigner signer)=>store.Check(x,signer));
app.MapFallbackToFile("index.html");
app.Services.GetRequiredService<LicenseStore>();
app.Run();

record AdminLogin(string Username,string Pin);
record CreateLicense(string Company,string Cnpj,string Notes="");
record ChangeStatus(string Status,string Message="");
record LicenseCheck(string Key,string InstallationId,string AppVersion="");
record LicensePayload(int LicenseId,string Company,string Cnpj,string InstallationId,string Status,string Message,string CheckedAt,string ValidUntil);

sealed class AdminSessions {
    readonly Dictionary<string,DateTime> sessions=[];readonly object gate=new();
    public string Create(){lock(gate){Cleanup();var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));sessions[token]=DateTime.UtcNow.AddHours(8);return token;}}
    public bool Valid(string token){lock(gate){Cleanup();if(token.Length==0||!sessions.ContainsKey(token))return false;sessions[token]=DateTime.UtcNow.AddHours(8);return true;}}
    void Cleanup(){foreach(var key in sessions.Where(x=>x.Value<=DateTime.UtcNow).Select(x=>x.Key).ToList())sessions.Remove(key);}
}

sealed class AdminAccess {
    readonly Dictionary<string,string> hashes;readonly object gate=new();int failures;DateTime lockedUntil;
    public AdminAccess(){var assembly=typeof(AdminAccess).Assembly;var resource=assembly.GetManifestResourceNames().Single(x=>x.EndsWith("master-access-codes.json",StringComparison.OrdinalIgnoreCase));using var stream=assembly.GetManifestResourceStream(resource)!;hashes=JsonSerializer.Deserialize<Dictionary<string,string>>(stream)!;}
    public void Validate(string? username,string? pin){lock(gate){if(DateTime.Now<lockedUntil)throw new ArgumentException("Acesso temporariamente bloqueado.");var day=DateTime.Today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);var valid=string.Equals(username?.Trim(),"ADMIN",StringComparison.OrdinalIgnoreCase)&&pin is not null&&Regex.IsMatch(pin,@"^\d{10}$")&&hashes.TryGetValue(day,out var expected)&&CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected),SHA256.HashData(Encoding.UTF8.GetBytes($"{day}:{pin}")));if(!valid){failures++;if(failures>=5){failures=0;lockedUntil=DateTime.Now.AddMinutes(5);throw new ArgumentException("Muitas tentativas. Aguarde cinco minutos.");}throw new ArgumentException("Usuário ou senha inválidos.");}failures=0;lockedUntil=DateTime.MinValue;}}
}

sealed class LicenseSigner {
    readonly ECDsa key;readonly object gate=new();
    public LicenseSigner(){var configured=Environment.GetEnvironmentVariable("MONALISA_LICENSE_PRIVATE_KEY");var path=Path.GetFullPath(configured??Path.Combine(Directory.GetCurrentDirectory(),"..",".private-license","license-signing-private.pem"));if(!File.Exists(path))throw new InvalidOperationException("Chave privada de licenciamento não encontrada.");key=ECDsa.Create();key.ImportFromPem(File.ReadAllText(path));}
    public string Sign(LicensePayload payload){lock(gate){var json=JsonSerializer.SerializeToUtf8Bytes(payload);var signature=key.SignData(json,HashAlgorithmName.SHA256);return $"{Base64(json)}.{Base64(signature)}";}}
    static string Base64(byte[] value)=>Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
}

sealed class LicenseStore {
    readonly string path;readonly object gate=new();
    public LicenseStore(){path=Path.GetFullPath(Environment.GetEnvironmentVariable("MONALISA_LICENSE_DB")??Path.Combine(Directory.GetCurrentDirectory(),"data","licenses.db"));Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var c=Open();Run(c,"""
      CREATE TABLE IF NOT EXISTS licenses(id INTEGER PRIMARY KEY,company TEXT NOT NULL,cnpj TEXT NOT NULL UNIQUE,keyHash TEXT NOT NULL UNIQUE,status TEXT NOT NULL DEFAULT 'active',message TEXT NOT NULL DEFAULT '',installationId TEXT NOT NULL DEFAULT '',appVersion TEXT NOT NULL DEFAULT '',created TEXT NOT NULL,updated TEXT NOT NULL,lastSeen TEXT NOT NULL DEFAULT '');
      CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,licenseId INTEGER,action TEXT NOT NULL,detail TEXT NOT NULL,created TEXT NOT NULL);
    """);}
    SqliteConnection Open(){var c=new SqliteConnection($"Data Source={path}");c.Open();return c;}
    static void Run(SqliteConnection c,string sql,params object?[] values){using var cmd=c.CreateCommand();cmd.CommandText=sql;for(var i=0;i<values.Length;i++)cmd.Parameters.AddWithValue($"@p{i}",values[i]??DBNull.Value);cmd.ExecuteNonQuery();}
    static List<Dictionary<string,object?>> Query(SqliteConnection c,string sql,params object?[] values){using var cmd=c.CreateCommand();cmd.CommandText=sql;for(var i=0;i<values.Length;i++)cmd.Parameters.AddWithValue($"@p{i}",values[i]??DBNull.Value);using var reader=cmd.ExecuteReader();var list=new List<Dictionary<string,object?>>();while(reader.Read()){var row=new Dictionary<string,object?>();for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);list.Add(row);}return list;}
    static string Now()=>DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture);
    static string Cnpj(string value){var cnpj=new string((value??"").Where(char.IsDigit).ToArray());if(cnpj.Length!=14)throw new ArgumentException("Informe um CNPJ com 14 números.");return cnpj;}
    static string Required(string? value,string field){var clean=value?.Trim().ToUpperInvariant()??"";if(clean.Length==0)throw new ArgumentException($"Informe {field}.");return clean;}
    static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static void Status(string value){if(value is not("active" or "warning" or "read_only" or "blocked"))throw new ArgumentException("Situação de licença inválida.");}
    public object All(){lock(gate){using var c=Open();return Query(c,"SELECT id,company,cnpj,status,message,installationId,appVersion,created,updated,lastSeen FROM licenses ORDER BY company");}}
    public object Audit(){lock(gate){using var c=Open();return Query(c,"SELECT a.id,a.licenseId,l.company,a.action,a.detail,a.created FROM audit a LEFT JOIN licenses l ON l.id=a.licenseId ORDER BY a.id DESC LIMIT 300");}}
    public object Create(CreateLicense x){lock(gate){using var c=Open();var key="MLF-"+Convert.ToHexString(RandomNumberGenerator.GetBytes(16));var now=Now();Run(c,"INSERT INTO licenses(company,cnpj,keyHash,status,message,created,updated) VALUES(@p0,@p1,@p2,'active','',@p3,@p3)",Required(x.Company,"o nome da empresa"),Cnpj(x.Cnpj),Hash(key),now);var id=Convert.ToInt32(Query(c,"SELECT last_insert_rowid() id")[0]["id"]);Run(c,"INSERT INTO audit(licenseId,action,detail,created) VALUES(@p0,'LICENÇA CRIADA',@p1,@p2)",id,JsonSerializer.Serialize(new{x.Notes}),now);return new{id,activationKey=key};}}
    public object ChangeStatus(int id,ChangeStatus x){lock(gate){Status(x.Status);using var c=Open();if(Query(c,"SELECT id FROM licenses WHERE id=@p0",id).Count==0)throw new ArgumentException("Licença não encontrada.");var now=Now();Run(c,"UPDATE licenses SET status=@p0,message=@p1,updated=@p2 WHERE id=@p3",x.Status,RequiredMessage(x.Message),now,id);Run(c,"INSERT INTO audit(licenseId,action,detail,created) VALUES(@p0,'SITUAÇÃO ALTERADA',@p1,@p2)",id,JsonSerializer.Serialize(new{x.Status,x.Message}),now);return new{ok=true};}}
    static string RequiredMessage(string? value)=>value?.Trim().ToUpperInvariant()??"";
    public object ResetInstallation(int id){lock(gate){using var c=Open();if(Query(c,"SELECT id FROM licenses WHERE id=@p0",id).Count==0)throw new ArgumentException("Licença não encontrada.");var now=Now();Run(c,"UPDATE licenses SET installationId='',updated=@p0 WHERE id=@p1",now,id);Run(c,"INSERT INTO audit(licenseId,action,detail,created) VALUES(@p0,'VÍNCULO DE MÁQUINA REMOVIDO','{}',@p1)",id,now);return new{ok=true};}}
    public object Check(LicenseCheck x,LicenseSigner signer){lock(gate){var installation=Required(x.InstallationId,"a identificação da instalação");using var c=Open();var rows=Query(c,"SELECT * FROM licenses WHERE keyHash=@p0",Hash(x.Key?.Trim()??""));if(rows.Count==0)throw new ArgumentException("Chave de ativação inválida.");var row=rows[0];var id=Convert.ToInt32(row["id"]);var bound=row["installationId"]?.ToString()??"";if(bound.Length>0&&!string.Equals(bound,installation,StringComparison.Ordinal))throw new ArgumentException("Esta licença já está vinculada a outra instalação.");var now=DateTime.UtcNow;var nowText=now.ToString("O",CultureInfo.InvariantCulture);if(bound.Length==0)Run(c,"UPDATE licenses SET installationId=@p0,appVersion=@p1,lastSeen=@p2,updated=@p2 WHERE id=@p3",installation,x.AppVersion??"",nowText,id);else Run(c,"UPDATE licenses SET appVersion=@p0,lastSeen=@p1 WHERE id=@p2",x.AppVersion??"",nowText,id);var status=row["status"]!.ToString()!;var validUntil=now.AddDays(3).ToString("O",CultureInfo.InvariantCulture);var payload=new LicensePayload(id,row["company"]!.ToString()!,row["cnpj"]!.ToString()!,installation,status,row["message"]?.ToString()??"",nowText,validUntil);return new{token=signer.Sign(payload),status,payload.Message,payload.Company,payload.Cnpj,validUntil};}}
}

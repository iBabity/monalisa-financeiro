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

app.Use(async(ctx,next)=>{try{await next();}catch(ArgumentException ex){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error=ex.Message});}catch(SqliteException ex)when(ex.SqliteErrorCode==19){ctx.Response.StatusCode=409;await ctx.Response.WriteAsJsonAsync(new{error="Licença duplicada ou referência inválida."});}});
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
app.MapPost("/api/admin/licenses/{id:int}/regenerate-key",(int id,LicenseStore store)=>store.RegenerateKey(id));
app.MapGet("/api/admin/audit",(LicenseStore store)=>store.Audit());
app.MapPost("/api/license/check",(LicenseCheck x,LicenseStore store,LicenseSigner signer)=>store.Check(x,signer));
app.MapFallbackToFile("index.html");
app.Services.GetRequiredService<LicenseStore>();
app.Run();

record AdminLogin(string Username,string Pin);
record CreateLicense(string Name="",string Document="",string HolderType="company",string MachineName="",string Notes="",string Company="",string Cnpj="");
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
    readonly ECDsa key;readonly byte[] storageKey;readonly object gate=new();
    public LicenseSigner(){var configured=Environment.GetEnvironmentVariable("MONALISA_LICENSE_PRIVATE_KEY");var path=Path.GetFullPath(configured??Path.Combine(Directory.GetCurrentDirectory(),"..",".private-license","license-signing-private.pem"));if(!File.Exists(path))throw new InvalidOperationException("Chave privada de licenciamento não encontrada.");var pem=File.ReadAllText(path);key=ECDsa.Create();key.ImportFromPem(pem);storageKey=SHA256.HashData(Encoding.UTF8.GetBytes("MONALISA-LICENSE-STORAGE-V1\n"+pem));}
    public string Sign(LicensePayload payload){lock(gate){var json=JsonSerializer.SerializeToUtf8Bytes(payload);var signature=key.SignData(json,HashAlgorithmName.SHA256);return $"{Base64(json)}.{Base64(signature)}";}}
    public string Protect(string value){var nonce=RandomNumberGenerator.GetBytes(12);var plain=Encoding.UTF8.GetBytes(value);var cipher=new byte[plain.Length];var tag=new byte[16];using var aes=new AesGcm(storageKey,16);aes.Encrypt(nonce,plain,cipher,tag);return $"{Base64(nonce)}.{Base64(cipher)}.{Base64(tag)}";}
    public string Unprotect(string value){if(string.IsNullOrWhiteSpace(value))return "";try{var parts=value.Split('.');if(parts.Length!=3)return "";var nonce=Unbase64(parts[0]);var cipher=Unbase64(parts[1]);var tag=Unbase64(parts[2]);var plain=new byte[cipher.Length];using var aes=new AesGcm(storageKey,16);aes.Decrypt(nonce,cipher,tag,plain);return Encoding.UTF8.GetString(plain);}catch{return "";}}
    static string Base64(byte[] value)=>Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
    static byte[] Unbase64(string value){var normalized=value.Replace('-','+').Replace('_','/');return Convert.FromBase64String(normalized.PadRight(normalized.Length+(4-normalized.Length%4)%4,'='));}
}

sealed class LicenseStore {
    readonly string path;readonly LicenseSigner signer;readonly object gate=new();
    public LicenseStore(LicenseSigner signer){this.signer=signer;path=Path.GetFullPath(Environment.GetEnvironmentVariable("MONALISA_LICENSE_DB")??Path.Combine(Directory.GetCurrentDirectory(),"data","licenses.db"));Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var c=Open();Run(c,"""
      CREATE TABLE IF NOT EXISTS licenses(id INTEGER PRIMARY KEY,company TEXT NOT NULL,cnpj TEXT NOT NULL,holderType TEXT NOT NULL DEFAULT 'company',machineName TEXT NOT NULL DEFAULT 'MÁQUINA 1',keyHash TEXT NOT NULL UNIQUE,keyProtected TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'active',message TEXT NOT NULL DEFAULT '',installationId TEXT NOT NULL DEFAULT '',appVersion TEXT NOT NULL DEFAULT '',created TEXT NOT NULL,updated TEXT NOT NULL,lastSeen TEXT NOT NULL DEFAULT '');
      CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,licenseId INTEGER,action TEXT NOT NULL,detail TEXT NOT NULL,created TEXT NOT NULL);
    """);var columns=Query(c,"PRAGMA table_info(licenses)");if(!columns.Any(row=>string.Equals(row["name"]?.ToString(),"keyProtected",StringComparison.OrdinalIgnoreCase))){Run(c,"ALTER TABLE licenses ADD COLUMN keyProtected TEXT NOT NULL DEFAULT ''");columns=Query(c,"PRAGMA table_info(licenses)");}if(!columns.Any(row=>string.Equals(row["name"]?.ToString(),"holderType",StringComparison.OrdinalIgnoreCase)))MigrateMultipleMachines(c);}
    SqliteConnection Open(){var c=new SqliteConnection($"Data Source={path}");c.Open();return c;}
    static void Run(SqliteConnection c,string sql,params object?[] values){using var cmd=c.CreateCommand();cmd.CommandText=sql;for(var i=0;i<values.Length;i++)cmd.Parameters.AddWithValue($"@p{i}",values[i]??DBNull.Value);cmd.ExecuteNonQuery();}
    static List<Dictionary<string,object?>> Query(SqliteConnection c,string sql,params object?[] values){using var cmd=c.CreateCommand();cmd.CommandText=sql;for(var i=0;i<values.Length;i++)cmd.Parameters.AddWithValue($"@p{i}",values[i]??DBNull.Value);using var reader=cmd.ExecuteReader();var list=new List<Dictionary<string,object?>>();while(reader.Read()){var row=new Dictionary<string,object?>();for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);list.Add(row);}return list;}
    static string Now()=>DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture);
    static void MigrateMultipleMachines(SqliteConnection c){using var tx=c.BeginTransaction();Run(c,"ALTER TABLE licenses RENAME TO licenses_single_machine");Run(c,"CREATE TABLE licenses(id INTEGER PRIMARY KEY,company TEXT NOT NULL,cnpj TEXT NOT NULL,holderType TEXT NOT NULL DEFAULT 'company',machineName TEXT NOT NULL DEFAULT 'MÁQUINA 1',keyHash TEXT NOT NULL UNIQUE,keyProtected TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'active',message TEXT NOT NULL DEFAULT '',installationId TEXT NOT NULL DEFAULT '',appVersion TEXT NOT NULL DEFAULT '',created TEXT NOT NULL,updated TEXT NOT NULL,lastSeen TEXT NOT NULL DEFAULT '')");Run(c,"INSERT INTO licenses(id,company,cnpj,holderType,machineName,keyHash,keyProtected,status,message,installationId,appVersion,created,updated,lastSeen) SELECT id,company,cnpj,CASE WHEN length(cnpj)=11 THEN 'person' ELSE 'company' END,'MÁQUINA 1',keyHash,keyProtected,status,message,installationId,appVersion,created,updated,lastSeen FROM licenses_single_machine");Run(c,"DROP TABLE licenses_single_machine");tx.Commit();}
    static (string Digits,string Type) Document(string value,string requestedType){var digits=new string((value??"").Where(char.IsDigit).ToArray());var type=(requestedType??"").Trim().ToLowerInvariant();if(type is not("person" or "company"))type=digits.Length==11?"person":"company";var expected=type=="person"?11:14;if(digits.Length!=expected)throw new ArgumentException(type=="person"?"Informe um CPF com 11 números.":"Informe um CNPJ com 14 números.");return(digits,type);}
    static string Required(string? value,string field){var clean=value?.Trim().ToUpperInvariant()??"";if(clean.Length==0)throw new ArgumentException($"Informe {field}.");return clean;}
    static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static void Status(string value){if(value is not("active" or "warning" or "read_only" or "blocked"))throw new ArgumentException("Situação de licença inválida.");}
    public object All(){lock(gate){using var c=Open();return Query(c,"SELECT id,company,cnpj,holderType,machineName,status,message,installationId,appVersion,created,updated,lastSeen,keyProtected FROM licenses ORDER BY company,machineName,id").Select(row=>new{id=Convert.ToInt32(row["id"]),name=row["company"],document=row["cnpj"],holderType=row["holderType"],machineName=row["machineName"],status=row["status"],message=row["message"],installationId=row["installationId"],appVersion=row["appVersion"],created=row["created"],updated=row["updated"],lastSeen=row["lastSeen"],activationKey=signer.Unprotect(row["keyProtected"]?.ToString()??""),inUse=!string.IsNullOrWhiteSpace(row["installationId"]?.ToString())}).ToList();}}
    public object Audit(){lock(gate){using var c=Open();return Query(c,"SELECT a.id,a.licenseId,l.company,a.action,a.detail,a.created FROM audit a LEFT JOIN licenses l ON l.id=a.licenseId ORDER BY a.id DESC LIMIT 300");}}
    public object Create(CreateLicense x){lock(gate){using var c=Open();var name=Required(string.IsNullOrWhiteSpace(x.Name)?x.Company:x.Name,"o nome da pessoa ou empresa");var document=Document(string.IsNullOrWhiteSpace(x.Document)?x.Cnpj:x.Document,x.HolderType);var machine=Required(string.IsNullOrWhiteSpace(x.MachineName)?"MÁQUINA 1":x.MachineName,"o nome da máquina");if(Query(c,"SELECT id FROM licenses WHERE cnpj=@p0 AND upper(machineName)=upper(@p1)",document.Digits,machine).Count>0)throw new ArgumentException("Já existe uma licença com este nome de máquina para este CPF ou CNPJ.");var key="MLF-"+Convert.ToHexString(RandomNumberGenerator.GetBytes(16));var now=Now();Run(c,"INSERT INTO licenses(company,cnpj,holderType,machineName,keyHash,keyProtected,status,message,created,updated) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,'active','',@p6,@p6)",name,document.Digits,document.Type,machine,Hash(key),signer.Protect(key),now);var id=Convert.ToInt32(Query(c,"SELECT last_insert_rowid() id")[0]["id"]);Run(c,"INSERT INTO audit(licenseId,action,detail,created) VALUES(@p0,'LICENÇA DE MÁQUINA CRIADA',@p1,@p2)",id,JsonSerializer.Serialize(new{x.Notes,machine}),now);return new{id,activationKey=key};}}
    public object ChangeStatus(int id,ChangeStatus x){lock(gate){Status(x.Status);using var c=Open();if(Query(c,"SELECT id FROM licenses WHERE id=@p0",id).Count==0)throw new ArgumentException("Licença não encontrada.");var now=Now();Run(c,"UPDATE licenses SET status=@p0,message=@p1,updated=@p2 WHERE id=@p3",x.Status,RequiredMessage(x.Message),now,id);Run(c,"INSERT INTO audit(licenseId,action,detail,created) VALUES(@p0,'SITUAÇÃO ALTERADA',@p1,@p2)",id,JsonSerializer.Serialize(new{x.Status,x.Message}),now);return new{ok=true};}}
    static string RequiredMessage(string? value)=>value?.Trim().ToUpperInvariant()??"";
    public object RegenerateKey(int id){lock(gate){using var c=Open();var rows=Query(c,"SELECT installationId FROM licenses WHERE id=@p0",id);if(rows.Count==0)throw new ArgumentException("Licença não encontrada.");if(!string.IsNullOrWhiteSpace(rows[0]["installationId"]?.ToString()))throw new ArgumentException("Esta licença está em uso. Libere a máquina antes de gerar outra chave.");var key="MLF-"+Convert.ToHexString(RandomNumberGenerator.GetBytes(16));var now=Now();Run(c,"UPDATE licenses SET keyHash=@p0,keyProtected=@p1,updated=@p2 WHERE id=@p3",Hash(key),signer.Protect(key),now,id);Run(c,"INSERT INTO audit(licenseId,action,detail,created) VALUES(@p0,'NOVA CHAVE GERADA','{}',@p1)",id,now);return new{id,activationKey=key};}}
    public object ResetInstallation(int id){lock(gate){using var c=Open();if(Query(c,"SELECT id FROM licenses WHERE id=@p0",id).Count==0)throw new ArgumentException("Licença não encontrada.");var now=Now();Run(c,"UPDATE licenses SET installationId='',updated=@p0 WHERE id=@p1",now,id);Run(c,"INSERT INTO audit(licenseId,action,detail,created) VALUES(@p0,'VÍNCULO DE MÁQUINA REMOVIDO','{}',@p1)",id,now);return new{ok=true};}}
    public object Check(LicenseCheck x,LicenseSigner signer){lock(gate){var installation=Required(x.InstallationId,"a identificação da instalação");using var c=Open();var rows=Query(c,"SELECT * FROM licenses WHERE keyHash=@p0",Hash(x.Key?.Trim()??""));if(rows.Count==0)throw new ArgumentException("Chave de ativação inválida.");var row=rows[0];var id=Convert.ToInt32(row["id"]);var bound=row["installationId"]?.ToString()??"";if(bound.Length>0&&!string.Equals(bound,installation,StringComparison.Ordinal))throw new ArgumentException("Esta licença já está vinculada a outra instalação.");var now=DateTime.UtcNow;var nowText=now.ToString("O",CultureInfo.InvariantCulture);if(bound.Length==0)Run(c,"UPDATE licenses SET installationId=@p0,appVersion=@p1,lastSeen=@p2,updated=@p2 WHERE id=@p3",installation,x.AppVersion??"",nowText,id);else Run(c,"UPDATE licenses SET appVersion=@p0,lastSeen=@p1 WHERE id=@p2",x.AppVersion??"",nowText,id);var status=row["status"]!.ToString()!;var validUntil=now.AddDays(3).ToString("O",CultureInfo.InvariantCulture);var payload=new LicensePayload(id,row["company"]!.ToString()!,row["cnpj"]!.ToString()!,installation,status,row["message"]?.ToString()??"",nowText,validUntil);return new{token=signer.Sign(payload),status,payload.Message,payload.Company,payload.Cnpj,validUntil};}}
}

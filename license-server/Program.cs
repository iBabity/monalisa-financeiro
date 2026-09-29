using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder=WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("MONALISA_LICENSE_BIND")??"http://127.0.0.1:5200");
builder.WebHost.ConfigureKestrel(options=>options.Limits.MaxRequestBodySize=300L*1024*1024);
builder.Services.AddSingleton<LicenseStore>();
builder.Services.AddSingleton<UpdateStore>();
builder.Services.AddSingleton<AdminAccess>();
builder.Services.AddSingleton<AdminSessions>();
builder.Services.AddSingleton<LicenseSigner>();
var app=builder.Build();

app.Use(async(ctx,next)=>{try{await next();}catch(ArgumentException ex){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error=ex.Message});}catch(SqliteException ex)when(ex.SqliteErrorCode==19){ctx.Response.StatusCode=409;await ctx.Response.WriteAsJsonAsync(new{error="Licença duplicada ou referência inválida."});}});
app.Use(async(ctx,next)=>{
    if(!ctx.Request.Path.StartsWithSegments("/api/admin")||ctx.Request.Path=="/api/admin/login"){await next();return;}
#if DEBUG
    if(Environment.GetEnvironmentVariable("MONALISA_ADMIN_DISABLED")=="1"){await next();return;}
#endif
    var header=ctx.Request.Headers.Authorization.ToString();var token=header.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)?header[7..].Trim():"";
    if(!ctx.RequestServices.GetRequiredService<AdminSessions>().Valid(token)){ctx.Response.StatusCode=401;await ctx.Response.WriteAsJsonAsync(new{error="Entre novamente no Painel Central."});return;}
    await next();
});
app.UseDefaultFiles();app.UseStaticFiles(new StaticFileOptions{OnPrepareResponse=context=>context.Context.Response.Headers.CacheControl="no-store, no-cache, must-revalidate"});
app.MapGet("/api/health",()=>new{status="ok",service="monalisa-licensing"});
app.MapPost("/api/admin/login",(AdminLogin x,AdminAccess access,AdminSessions sessions)=>{access.Validate(x.Username,x.Pin);return new{token=sessions.Create(),user="ADMIN"};});
app.MapGet("/api/admin/licenses",(LicenseStore store)=>store.All());
app.MapPost("/api/admin/licenses",(CreateLicense x,LicenseStore store)=>store.Create(x));
app.MapPost("/api/admin/licenses/{id:int}/status",(int id,ChangeStatus x,LicenseStore store)=>store.ChangeStatus(id,x));
app.MapPost("/api/admin/licenses/{id:int}/reset-installation",(int id,LicenseStore store)=>store.ResetInstallation(id));
app.MapPost("/api/admin/licenses/{id:int}/regenerate-key",(int id,LicenseStore store)=>store.RegenerateKey(id));
app.MapGet("/api/admin/audit",(LicenseStore store)=>store.Audit());
app.MapGet("/api/admin/updates",(UpdateStore store)=>store.All());
app.MapPost("/api/admin/updates",async(HttpRequest request,UpdateStore store)=>{
    if(!request.HasFormContentType)throw new ArgumentException("Envie o instalador da atualização.");
    var form=await request.ReadFormAsync();var file=form.Files.GetFile("file")??throw new ArgumentException("Selecione o instalador da atualização.");
    return await store.Publish(form["version"].ToString(),form["notes"].ToString(),file);
}).DisableAntiforgery();
app.MapPost("/api/license/check",(LicenseCheck x,LicenseStore store,LicenseSigner signer)=>store.Check(x,signer));
app.MapPost("/api/license/release",(LicenseRelease x,LicenseStore store)=>store.Release(x));
app.MapGet("/api/update/manifest",(string? version,UpdateStore store)=>store.Manifest(version??""));
app.MapGet("/api/update/download/{version}",(string version,UpdateStore store)=>{var package=store.Package(version);return Results.File(package.Path,"application/vnd.microsoft.portable-executable",package.Name,enableRangeProcessing:true);});
app.MapFallbackToFile("index.html");
app.Services.GetRequiredService<LicenseStore>();
app.Run();

record AdminLogin(string Username,string Pin);
record CreateLicense(string Name="",string Document="",string HolderType="company",string MachineName="",string Notes="",string Company="",string Cnpj="");
record ChangeStatus(string Status,string Message="");
record LicenseCheck(string Key,string InstallationId,string AppVersion="",string NetworkRole="",string NetworkServerUrl="",string NetworkKey="");
record LicenseRelease(string Key,string InstallationId);
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
      CREATE TABLE IF NOT EXISTS shared_networks(document TEXT PRIMARY KEY,serverLicenseId INTEGER NOT NULL DEFAULT 0,serverUrl TEXT NOT NULL,networkKeyProtected TEXT NOT NULL,updated TEXT NOT NULL);
    """);var columns=Query(c,"PRAGMA table_info(licenses)");if(!columns.Any(row=>string.Equals(row["name"]?.ToString(),"keyProtected",StringComparison.OrdinalIgnoreCase))){Run(c,"ALTER TABLE licenses ADD COLUMN keyProtected TEXT NOT NULL DEFAULT ''");columns=Query(c,"PRAGMA table_info(licenses)");}if(!columns.Any(row=>string.Equals(row["name"]?.ToString(),"holderType",StringComparison.OrdinalIgnoreCase)))MigrateMultipleMachines(c);var networkColumns=Query(c,"PRAGMA table_info(shared_networks)");if(!networkColumns.Any(row=>string.Equals(row["name"]?.ToString(),"serverLicenseId",StringComparison.OrdinalIgnoreCase)))Run(c,"ALTER TABLE shared_networks ADD COLUMN serverLicenseId INTEGER NOT NULL DEFAULT 0");}
    SqliteConnection Open(){var c=new SqliteConnection($"Data Source={path}");c.Open();return c;}
    static void Run(SqliteConnection c,string sql,params object?[] values){using var cmd=c.CreateCommand();cmd.CommandText=sql;for(var i=0;i<values.Length;i++)cmd.Parameters.AddWithValue($"@p{i}",values[i]??DBNull.Value);cmd.ExecuteNonQuery();}
    static List<Dictionary<string,object?>> Query(SqliteConnection c,string sql,params object?[] values){using var cmd=c.CreateCommand();cmd.CommandText=sql;for(var i=0;i<values.Length;i++)cmd.Parameters.AddWithValue($"@p{i}",values[i]??DBNull.Value);using var reader=cmd.ExecuteReader();var list=new List<Dictionary<string,object?>>();while(reader.Read()){var row=new Dictionary<string,object?>();for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);list.Add(row);}return list;}
    static string Now()=>DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture);
    static void MigrateMultipleMachines(SqliteConnection c){using var tx=c.BeginTransaction();Run(c,"ALTER TABLE licenses RENAME TO licenses_single_machine");Run(c,"CREATE TABLE licenses(id INTEGER PRIMARY KEY,company TEXT NOT NULL,cnpj TEXT NOT NULL,holderType TEXT NOT NULL DEFAULT 'company',machineName TEXT NOT NULL DEFAULT 'MÁQUINA 1',keyHash TEXT NOT NULL UNIQUE,keyProtected TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'active',message TEXT NOT NULL DEFAULT '',installationId TEXT NOT NULL DEFAULT '',appVersion TEXT NOT NULL DEFAULT '',created TEXT NOT NULL,updated TEXT NOT NULL,lastSeen TEXT NOT NULL DEFAULT '')");Run(c,"INSERT INTO licenses(id,company,cnpj,holderType,machineName,keyHash,keyProtected,status,message,installationId,appVersion,created,updated,lastSeen) SELECT id,company,cnpj,CASE WHEN length(cnpj)=11 THEN 'person' ELSE 'company' END,'MÁQUINA 1',keyHash,keyProtected,status,message,installationId,appVersion,created,updated,lastSeen FROM licenses_single_machine");Run(c,"DROP TABLE licenses_single_machine");tx.Commit();}
    static (string Digits,string Type) Document(string value,string requestedType){var digits=new string((value??"").Where(char.IsDigit).ToArray());var type=(requestedType??"").Trim().ToLowerInvariant();if(type is not("person" or "company"))type=digits.Length==11?"person":"company";var expected=type=="person"?11:14;if(digits.Length!=expected)throw new ArgumentException(type=="person"?"Informe um CPF com 11 números.":"Informe um CNPJ com 14 números.");return(digits,type);}
    static string Required(string? value,string field){var clean=value?.Trim().ToUpperInvariant()??"";if(clean.Length==0)throw new ArgumentException($"Informe {field}.");return clean;}
    static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static void Status(string value){if(value is not("active" or "warning" or "read_only" or "blocked"))throw new ArgumentException("Situação de licença inválida.");}
    public object All(){lock(gate){using var c=Open();return Query(c,"SELECT id,company,cnpj,holderType,machineName,status,message,installationId,appVersion,created,updated,lastSeen,keyProtected FROM licenses ORDER BY company,machineName,id").Select(row=>{var linked=!string.IsNullOrWhiteSpace(row["installationId"]?.ToString());return new{id=Convert.ToInt32(row["id"]),name=row["company"],document=row["cnpj"],holderType=row["holderType"],machineName=row["machineName"],status=row["status"],message=row["message"],installationId=row["installationId"],appVersion=row["appVersion"],created=row["created"],updated=row["updated"],lastSeen=row["lastSeen"],activationKey=signer.Unprotect(row["keyProtected"]?.ToString()??""),linked,inUse=linked};}).ToList();}}
    public object Audit(){lock(gate){using var c=Open();return Query(c,"SELECT a.id,a.licenseId,l.company,a.action,a.detail,a.created FROM audit a LEFT JOIN licenses l ON l.id=a.licenseId ORDER BY a.id DESC LIMIT 300");}}
    public object Create(CreateLicense x){lock(gate){using var c=Open();var name=Required(string.IsNullOrWhiteSpace(x.Name)?x.Company:x.Name,"o nome da pessoa ou empresa");var document=Document(string.IsNullOrWhiteSpace(x.Document)?x.Cnpj:x.Document,x.HolderType);var machine=Required(string.IsNullOrWhiteSpace(x.MachineName)?"MÁQUINA 1":x.MachineName,"o nome da máquina");if(Query(c,"SELECT id FROM licenses WHERE cnpj=@p0 AND upper(machineName)=upper(@p1)",document.Digits,machine).Count>0)throw new ArgumentException("Já existe uma licença com este nome de máquina para este CPF ou CNPJ.");var key="MLF-"+Convert.ToHexString(RandomNumberGenerator.GetBytes(16));var now=Now();Run(c,"INSERT INTO licenses(company,cnpj,holderType,machineName,keyHash,keyProtected,status,message,created,updated) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,'active','',@p6,@p6)",name,document.Digits,document.Type,machine,Hash(key),signer.Protect(key),now);var id=Convert.ToInt32(Query(c,"SELECT last_insert_rowid() id")[0]["id"]);Run(c,"INSERT INTO audit(licenseId,action,detail,created) VALUES(@p0,'LICENÇA DE MÁQUINA CRIADA',@p1,@p2)",id,JsonSerializer.Serialize(new{x.Notes,machine}),now);return new{id,activationKey=key};}}
    public object ChangeStatus(int id,ChangeStatus x){lock(gate){Status(x.Status);using var c=Open();if(Query(c,"SELECT id FROM licenses WHERE id=@p0",id).Count==0)throw new ArgumentException("Licença não encontrada.");var now=Now();Run(c,"UPDATE licenses SET status=@p0,message=@p1,updated=@p2 WHERE id=@p3",x.Status,RequiredMessage(x.Message),now,id);Run(c,"INSERT INTO audit(licenseId,action,detail,created) VALUES(@p0,'SITUAÇÃO ALTERADA',@p1,@p2)",id,JsonSerializer.Serialize(new{x.Status,x.Message}),now);return new{ok=true};}}
    static string RequiredMessage(string? value)=>value?.Trim().ToUpperInvariant()??"";
    public object RegenerateKey(int id){lock(gate){using var c=Open();var rows=Query(c,"SELECT installationId FROM licenses WHERE id=@p0",id);if(rows.Count==0)throw new ArgumentException("Licença não encontrada.");if(!string.IsNullOrWhiteSpace(rows[0]["installationId"]?.ToString()))throw new ArgumentException("Esta licença está em uso. Libere a máquina antes de gerar outra chave.");var key="MLF-"+Convert.ToHexString(RandomNumberGenerator.GetBytes(16));var now=Now();Run(c,"UPDATE licenses SET keyHash=@p0,keyProtected=@p1,updated=@p2 WHERE id=@p3",Hash(key),signer.Protect(key),now,id);Run(c,"INSERT INTO audit(licenseId,action,detail,created) VALUES(@p0,'NOVA CHAVE GERADA','{}',@p1)",id,now);return new{id,activationKey=key};}}
    public object ResetInstallation(int id){lock(gate){using var c=Open();if(Query(c,"SELECT id FROM licenses WHERE id=@p0",id).Count==0)throw new ArgumentException("Licença não encontrada.");var now=Now();Run(c,"UPDATE licenses SET installationId='',appVersion='',lastSeen='',updated=@p0 WHERE id=@p1",now,id);Run(c,"INSERT INTO audit(licenseId,action,detail,created) VALUES(@p0,'VÍNCULO DE MÁQUINA REMOVIDO','{}',@p1)",id,now);return new{ok=true};}}
    public object Check(LicenseCheck x,LicenseSigner signer){lock(gate){
        var installation=Required(x.InstallationId,"a identificação da instalação");using var c=Open();
        var rows=Query(c,"SELECT * FROM licenses WHERE keyHash=@p0",Hash(x.Key?.Trim()??""));if(rows.Count==0)throw new ArgumentException("Chave de ativação inválida.");
        var row=rows[0];var id=Convert.ToInt32(row["id"]);var bound=row["installationId"]?.ToString()??"";
        if(bound.Length>0&&!string.Equals(bound,installation,StringComparison.Ordinal))throw new ArgumentException("Esta licença já está vinculada a outra instalação.");
        var now=DateTime.UtcNow;var nowText=now.ToString("O",CultureInfo.InvariantCulture);
        if(bound.Length==0)Run(c,"UPDATE licenses SET installationId=@p0,appVersion=@p1,lastSeen=@p2,updated=@p2 WHERE id=@p3",installation,x.AppVersion??"",nowText,id);else Run(c,"UPDATE licenses SET appVersion=@p0,lastSeen=@p1 WHERE id=@p2",x.AppVersion??"",nowText,id);
        var document=row["cnpj"]!.ToString()!;var role=(x.NetworkRole??"").Trim().ToLowerInvariant();
        if(role=="server"&&ValidSharedNetwork(x.NetworkServerUrl,x.NetworkKey))Run(c,"INSERT INTO shared_networks(document,serverLicenseId,serverUrl,networkKeyProtected,updated) VALUES(@p0,@p1,@p2,@p3,@p4) ON CONFLICT(document) DO UPDATE SET serverLicenseId=excluded.serverLicenseId,serverUrl=excluded.serverUrl,networkKeyProtected=excluded.networkKeyProtected,updated=excluded.updated",document,id,x.NetworkServerUrl.Trim().TrimEnd('/'),signer.Protect(x.NetworkKey.Trim()),nowText);
        var firstId=Convert.ToInt32(Query(c,"SELECT id FROM licenses WHERE cnpj=@p0 ORDER BY id LIMIT 1",document)[0]["id"]);
        var shared=Query(c,"SELECT serverLicenseId,serverUrl,networkKeyProtected FROM shared_networks WHERE document=@p0",document).FirstOrDefault();
        var registeredServerId=shared is null?0:Convert.ToInt32(shared["serverLicenseId"]);var secondary=registeredServerId>0?registeredServerId!=id:id!=firstId;
        if(!secondary&&role=="standalone"){Run(c,"DELETE FROM shared_networks WHERE document=@p0",document);shared=null;}
        if(!secondary)shared=null;
        var serverUrl=shared is null?"":shared["serverUrl"]?.ToString()??"";var networkKey=shared is null?"":signer.Unprotect(shared["networkKeyProtected"]?.ToString()??"");
        var status=row["status"]!.ToString()!;var validUntil=now.AddDays(3).ToString("O",CultureInfo.InvariantCulture);var payload=new LicensePayload(id,row["company"]!.ToString()!,document,installation,status,row["message"]?.ToString()??"",nowText,validUntil);
        return new{token=signer.Sign(payload),status,payload.Message,payload.Company,payload.Cnpj,validUntil,isSecondary=secondary,networkMode=serverUrl.Length>0&&networkKey.Length>=24?"client":role,networkServerUrl=serverUrl,networkKey};
    }}
    public object Release(LicenseRelease x){lock(gate){var installation=Required(x.InstallationId,"a identificação da instalação");using var c=Open();var rows=Query(c,"SELECT id,installationId FROM licenses WHERE keyHash=@p0",Hash(x.Key?.Trim()??""));if(rows.Count==0)throw new ArgumentException("Chave de ativação atual inválida.");var row=rows[0];if(!string.Equals(row["installationId"]?.ToString(),installation,StringComparison.Ordinal))throw new ArgumentException("Esta licença não pertence à instalação atual.");var id=Convert.ToInt32(row["id"]);var now=Now();Run(c,"UPDATE licenses SET installationId='',appVersion='',lastSeen='',updated=@p0 WHERE id=@p1",now,id);Run(c,"INSERT INTO audit(licenseId,action,detail,created) VALUES(@p0,'LICENÇA TROCADA NA MÁQUINA','{}',@p1)",id,now);return new{ok=true};}}
    static bool ValidSharedNetwork(string? serverUrl,string? networkKey){if(string.IsNullOrWhiteSpace(serverUrl)||string.IsNullOrWhiteSpace(networkKey)||networkKey.Trim().Length<24||!Uri.TryCreate(serverUrl.Trim(),UriKind.Absolute,out var uri)||uri.Scheme!="http"||!System.Net.IPAddress.TryParse(uri.Host,out var address))return false;var bytes=address.GetAddressBytes();return bytes.Length==4&&(bytes[0]==10||(bytes[0]==172&&bytes[1]>=16&&bytes[1]<=31)||(bytes[0]==192&&bytes[1]==168));}
}

record UpdatePackage(string Path,string Name);

sealed class UpdateStore {
    readonly string databasePath;
    readonly string packageDirectory;
    readonly SemaphoreSlim gate=new(1,1);

    public UpdateStore(){
        databasePath=Path.GetFullPath(Environment.GetEnvironmentVariable("MONALISA_LICENSE_DB")??Path.Combine(Directory.GetCurrentDirectory(),"data","licenses.db"));
        packageDirectory=Path.GetFullPath(Environment.GetEnvironmentVariable("MONALISA_UPDATE_DIR")??Path.Combine(Directory.GetCurrentDirectory(),"..",".private-license","updates"));
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);Directory.CreateDirectory(packageDirectory);
        using var c=Open();Run(c,"""
          CREATE TABLE IF NOT EXISTS app_updates(
            id INTEGER PRIMARY KEY,
            version TEXT NOT NULL UNIQUE,
            fileName TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            size INTEGER NOT NULL,
            notes TEXT NOT NULL DEFAULT '',
            mandatory INTEGER NOT NULL DEFAULT 1,
            published TEXT NOT NULL
          );
        """);
    }

    SqliteConnection Open(){var c=new SqliteConnection($"Data Source={databasePath}");c.Open();return c;}
    static void Run(SqliteConnection c,string sql,params object?[] values){using var cmd=c.CreateCommand();cmd.CommandText=sql;for(var i=0;i<values.Length;i++)cmd.Parameters.AddWithValue($"@p{i}",values[i]??DBNull.Value);cmd.ExecuteNonQuery();}
    static List<Dictionary<string,object?>> Query(SqliteConnection c,string sql,params object?[] values){using var cmd=c.CreateCommand();cmd.CommandText=sql;for(var i=0;i<values.Length;i++)cmd.Parameters.AddWithValue($"@p{i}",values[i]??DBNull.Value);using var reader=cmd.ExecuteReader();var list=new List<Dictionary<string,object?>>();while(reader.Read()){var row=new Dictionary<string,object?>();for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);list.Add(row);}return list;}
    static string ValidVersion(string value){var clean=(value??"").Trim();if(!Regex.IsMatch(clean,@"^\d+\.\d+\.\d+$"))throw new ArgumentException("Informe a versão com três números, por exemplo 1.1.0.");return clean;}
    static Version Parsed(string value){var match=Regex.Match(value??"",@"\d+\.\d+\.\d+");return match.Success&&Version.TryParse(match.Value,out var parsed)?parsed:new Version(0,0,0);}
    static object Row(Dictionary<string,object?> row)=>new{version=row["version"],sha256=row["sha256"],size=Convert.ToInt64(row["size"]),notes=row["notes"],mandatory=true,published=row["published"]};

    public object All(){using var c=Open();return Query(c,"SELECT version,sha256,size,notes,published FROM app_updates ORDER BY id DESC").Select(Row).ToList();}

    public object Manifest(string current){
        using var c=Open();var rows=Query(c,"SELECT version,sha256,size,notes,published FROM app_updates ORDER BY id DESC LIMIT 1");
        if(rows.Count==0)return new{available=false,mandatory=false,currentVersion=current,latestVersion="",message="NENHUMA ATUALIZAÇÃO PUBLICADA."};
        var row=rows[0];var latest=row["version"]!.ToString()!;var available=Parsed(latest)>Parsed(current);
        return new{available,mandatory=available,currentVersion=current,latestVersion=latest,sha256=row["sha256"],size=Convert.ToInt64(row["size"]),notes=row["notes"],published=row["published"],downloadPath=$"/api/update/download/{Uri.EscapeDataString(latest)}",message=available?"ATUALIZAÇÃO OBRIGATÓRIA DISPONÍVEL.":"ESTA MÁQUINA ESTÁ ATUALIZADA."};
    }

    public async Task<object> Publish(string version,string notes,IFormFile file){
        version=ValidVersion(version);if(file.Length<=0)throw new ArgumentException("O instalador enviado está vazio.");if(file.Length>250L*1024*1024)throw new ArgumentException("O instalador ultrapassa o limite de 250 MB.");if(!string.Equals(Path.GetExtension(file.FileName),".exe",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Envie um instalador do Windows no formato EXE.");
        await gate.WaitAsync();try{
            var fileName=$"Monalisa-Financeiro-{version}.exe";var target=Path.Combine(packageDirectory,fileName);var temporary=target+".upload";
            try{await using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)){await file.CopyToAsync(output);}string hash;await using(var input=File.OpenRead(temporary)){hash=Convert.ToHexString(await SHA256.HashDataAsync(input));}File.Move(temporary,target,true);using var c=Open();Run(c,"INSERT INTO app_updates(version,fileName,sha256,size,notes,mandatory,published) VALUES(@p0,@p1,@p2,@p3,@p4,1,@p5) ON CONFLICT(version) DO UPDATE SET fileName=excluded.fileName,sha256=excluded.sha256,size=excluded.size,notes=excluded.notes,mandatory=1,published=excluded.published",version,fileName,hash,file.Length,(notes??"").Trim().ToUpperInvariant(),DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture));return new{ok=true,version,sha256=hash,size=file.Length,mandatory=true};}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }finally{gate.Release();}
    }

    public UpdatePackage Package(string version){version=ValidVersion(version);using var c=Open();var rows=Query(c,"SELECT fileName,sha256 FROM app_updates WHERE version=@p0",version);if(rows.Count==0)throw new ArgumentException("Versão de atualização não encontrada.");var name=rows[0]["fileName"]!.ToString()!;var path=Path.GetFullPath(Path.Combine(packageDirectory,name));if(!path.StartsWith(packageDirectory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!File.Exists(path))throw new ArgumentException("O arquivo desta atualização não está disponível.");var actual=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));if(!string.Equals(actual,rows[0]["sha256"]!.ToString(),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("O arquivo da atualização foi alterado e não pode ser distribuído.");return new(path,name);}
}

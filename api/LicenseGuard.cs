using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

record LicenseActivationInput(string ActivationKey);
record LicenseSnapshot(string Status,string Message,string Company,string Cnpj,string ValidUntil,bool Offline,bool Configured);
record StoredLicense(string InstallationId,string ProtectedKey,string Token,string LastCheck);
record RemoteLicenseResponse(string Token);
record SignedLicensePayload(int LicenseId,string Company,string Cnpj,string InstallationId,string Status,string Message,string CheckedAt,string ValidUntil);
sealed class LicenseServerRejectedException(string message):Exception(message);

sealed class LicenseGuard {
    readonly string serverUrl;
    readonly string configPath;
    readonly string installationId;
    readonly ECDsa publicKey;
    readonly HttpClient client=new(){Timeout=TimeSpan.FromSeconds(10)};
    readonly SemaphoreSlim mutex=new(1,1);
    StoredLicense stored;
    LicenseSnapshot? cached;

    public LicenseGuard() {
        serverUrl=(Environment.GetEnvironmentVariable("MONALISA_LICENSE_URL")??"").Trim().TrimEnd('/');
        if(serverUrl.Length>0&&!serverUrl.StartsWith("https://",StringComparison.OrdinalIgnoreCase)&&!serverUrl.StartsWith("http://127.0.0.1",StringComparison.OrdinalIgnoreCase)&&!serverUrl.StartsWith("http://localhost",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("O servidor de licenças deve usar HTTPS.");
        var configuredDb=Environment.GetEnvironmentVariable("FINANCE_DB");
        var databasePath=Path.GetFullPath(configuredDb??Path.Combine(AppContext.BaseDirectory,"data","financeiro.db"));
        var dataDirectory=Path.GetDirectoryName(databasePath)!;
        Directory.CreateDirectory(dataDirectory);configPath=Path.Combine(dataDirectory,$"{Path.GetFileNameWithoutExtension(databasePath)}.license.json");
        stored=Load();installationId=stored.InstallationId;
        var assembly=typeof(LicenseGuard).Assembly;var resource=assembly.GetManifestResourceNames().Single(name=>name.EndsWith("license-public.pem",StringComparison.OrdinalIgnoreCase));using var stream=assembly.GetManifestResourceStream(resource)!;using var reader=new StreamReader(stream);publicKey=ECDsa.Create();publicKey.ImportFromPem(reader.ReadToEnd());
    }

    public async Task<LicenseSnapshot> Status(bool force=false) {
#if DEBUG
        if(Environment.GetEnvironmentVariable("MONALISA_LICENSE_DISABLED")=="1")return new("active","MODO LOCAL DE DESENVOLVIMENTO","DESENVOLVIMENTO","",DateTime.UtcNow.AddDays(1).ToString("O"),false,true);
#endif
        if(serverUrl.Length==0)return new("activation_required","O endereço do servidor de licenças ainda não foi configurado.","","","",false,false);
        if(stored.ProtectedKey.Length==0)return new("activation_required","Informe a chave de ativação fornecida pela Monalisa.","","","",false,false);
        await mutex.WaitAsync();try {
            if(!force&&cached is not null&&DateTime.TryParse(stored.LastCheck,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var last)&&last>DateTime.UtcNow.AddMinutes(-5))return cached;
            try {
                var key=Unprotect(stored.ProtectedKey);var response=await client.PostAsJsonAsync($"{serverUrl}/api/license/check",new{key,installationId,appVersion="1.0"});
                var body=await response.Content.ReadFromJsonAsync<JsonElement>();if(!response.IsSuccessStatusCode)throw new LicenseServerRejectedException(body.TryGetProperty("error",out var error)?error.GetString()??"Licença recusada pelo servidor.":"Licença recusada pelo servidor.");
                var token=body.GetProperty("token").GetString()??throw new InvalidOperationException("Resposta de licença inválida.");var snapshot=Verify(token,false);stored=stored with{Token=token,LastCheck=DateTime.UtcNow.ToString("O")};Save();cached=snapshot;return snapshot;
            } catch(LicenseServerRejectedException ex) {
                cached=new("blocked",ex.Message,"","","",false,true);return cached;
            } catch(Exception ex) {
                if(stored.Token.Length>0) { var old=Verify(stored.Token,true);if(DateTime.TryParse(old.ValidUntil,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var until)&&until>DateTime.UtcNow){cached=old with{Offline=true,Message=old.Message.Length>0?old.Message:"Sem comunicação com o servidor. Usando a licença já validada."};return cached;} }
                cached=new("read_only",$"Sem comunicação com o servidor de licenças. {ex.Message}","","","",true,true);return cached;
            }
        } finally { mutex.Release(); }
    }

    public async Task<LicenseSnapshot> Activate(string activationKey) {
        if(serverUrl.Length==0)throw new ArgumentException("O servidor de licenças ainda não foi configurado nesta instalação.");
        var key=activationKey?.Trim().ToUpperInvariant()??"";if(!key.StartsWith("MLF-")||key.Length<20)throw new ArgumentException("Informe uma chave de ativação válida.");
        var previous=stored;stored=stored with{ProtectedKey=Protect(key),Token="",LastCheck=""};Save();cached=null;
        try { var result=await Status(true);if(result.Status is "activation_required" or "blocked"||result.Offline)throw new ArgumentException(result.Message);return result; }
        catch { stored=previous;Save();cached=null;throw; }
    }

    StoredLicense Load(){try{if(File.Exists(configPath)){var value=JsonSerializer.Deserialize<StoredLicense>(File.ReadAllText(configPath),new JsonSerializerOptions(JsonSerializerDefaults.Web));if(value is not null&&value.InstallationId.Length>0)return value;}}catch{}return new(Guid.NewGuid().ToString("N").ToUpperInvariant(),"","","");}
    void Save(){var temp=configPath+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(stored,new JsonSerializerOptions(JsonSerializerDefaults.Web)));File.Move(temp,configPath,true);}
    static string Protect(string value){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("O Monalisa Financeiro é exclusivo para Windows.");return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value),null,DataProtectionScope.LocalMachine));}
    static string Unprotect(string value){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("O Monalisa Financeiro é exclusivo para Windows.");return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),null,DataProtectionScope.LocalMachine));}
    LicenseSnapshot Verify(string token,bool offline){var parts=token.Split('.');if(parts.Length!=2)throw new InvalidOperationException("Comprovante de licença inválido.");var payload=Decode(parts[0]);var signature=Decode(parts[1]);if(!publicKey.VerifyData(payload,signature,HashAlgorithmName.SHA256))throw new InvalidOperationException("Assinatura da licença inválida.");var value=JsonSerializer.Deserialize<SignedLicensePayload>(payload,new JsonSerializerOptions(JsonSerializerDefaults.Web))??throw new InvalidOperationException("Licença inválida.");if(!string.Equals(value.InstallationId,installationId,StringComparison.Ordinal))throw new InvalidOperationException("Licença vinculada a outra instalação.");return new(value.Status,value.Message,value.Company,value.Cnpj,value.ValidUntil,offline,true);}
    static byte[] Decode(string value){var normalized=value.Replace('-','+').Replace('_','/');normalized+=new string('=',(4-normalized.Length%4)%4);return Convert.FromBase64String(normalized);}
}

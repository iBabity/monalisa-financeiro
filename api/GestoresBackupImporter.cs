using Microsoft.Data.Sqlite;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Readers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

record GestoresImportInput(string Name, string DataBase64, string Password = "");
record GestoresImportCommitInput(string Token);
record GestoresImportPreview(
    string Token, string Company, int Companies, int Parties, int Plans, int CostCenters,
    int PaymentMethods, int Accounts, int Payables, long PayablesTotal,
    int Receivables, long ReceivablesTotal, int Settlements, long SettlementsTotal);

static class GestoresBackupImporter {
    static readonly string[] Tables = [
        "cad_filiais", "pes_pessoa", "pes_documento", "pes_telefone", "pes_email",
        "cad_plano_contas", "cad_centro_custos", "cad_forma_pagamento",
        "ban_conta_corrente", "cx_caixa", "ctp_documento", "ctp_documento_baixa",
        "crb_documento", "crb_documento_baixa"
    ];

    public static GestoresImportPreview Prepare(string currentDatabase, GestoresImportInput input) {
        byte[] content;
        try { content=Convert.FromBase64String(input.DataBase64??""); }
        catch(FormatException) { throw new ArgumentException("O arquivo do Gestores é inválido."); }
        if(content.Length<1024||content.Length>500*1024*1024)throw new ArgumentException("O backup do Gestores deve ter entre 1 KB e 500 MB.");
        var name=Path.GetFileName(input.Name??"");var extension=Path.GetExtension(name).ToLowerInvariant();
        if(extension is not (".zip" or ".backup"))throw new ArgumentException("Selecione o arquivo .zip ou .backup gerado pelo Gestores.");

        var baseFolder=Path.Combine(Path.GetDirectoryName(currentDatabase)!,"imports");Directory.CreateDirectory(baseFolder);
        var token=Guid.NewGuid().ToString("N");var work=Path.Combine(baseFolder,"work-"+token);Directory.CreateDirectory(work);
        var source=Path.Combine(work,"source"+extension);File.WriteAllBytes(source,content);
        var dump=extension==".backup"?source:ExtractZip(source,work,input.Password);
        var restore=FindPgRestore();var sqlFolder=Path.Combine(work,"tables");Directory.CreateDirectory(sqlFolder);
        foreach(var table in Tables)ExtractTable(restore,dump,sqlFolder,table);

        var prepared=Path.Combine(baseFolder,"gestores-"+token+".db");
        try { return ConvertToMonalisa(currentDatabase,prepared,sqlFolder,token); }
        catch { TryDelete(prepared);throw; }
        finally { try{Directory.Delete(work,true);}catch{} }
    }

    public static string PreparedPath(string currentDatabase,string token) {
        var normalized=(token??"").Trim().ToLowerInvariant();
        if(!Regex.IsMatch(normalized,"^[a-f0-9]{32}$",RegexOptions.IgnoreCase))throw new ArgumentException("Importação do Gestores inválida ou expirada.");
        var path=Path.Combine(Path.GetDirectoryName(currentDatabase)!,"imports","gestores-"+normalized+".db");
        if(!File.Exists(path))throw new ArgumentException("A conferência do backup expirou. Selecione o arquivo novamente.");return path;
    }

    static string ExtractZip(string source,string work,string suppliedPassword) {
        var password=string.IsNullOrWhiteSpace(suppliedPassword)?LocalArchivePassword():suppliedPassword;
        try {
            using var archive=ZipArchive.OpenArchive(source,new ReaderOptions{Password=password,LeaveStreamOpen=false});
            var entry=archive.Entries.FirstOrDefault(e=>!e.IsDirectory&&e.Key?.EndsWith(".backup",StringComparison.OrdinalIgnoreCase)==true)
                ??throw new ArgumentException("O ZIP não contém um arquivo .backup do Gestores.");
            var target=Path.Combine(work,"gestores.backup");
            using(var sourceStream=entry.OpenEntryStream())using(var targetStream=File.Create(target))sourceStream.CopyTo(targetStream);
            return target;
        } catch(ArgumentException){throw;} catch(Exception ex){throw new ArgumentException("Não foi possível abrir o ZIP do Gestores. Confira a senha do backup. "+ex.Message);}
    }

    static string LocalArchivePassword() {
        try {
            if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
            var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Monalisa","Backup","archive-access.dpapi");
            var text=Regex.Replace(File.ReadAllText(path),@"\s","");
            var clear=ProtectedData.Unprotect(Convert.FromBase64String(text),null,DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        } catch { throw new ArgumentException("Informe a senha usada para proteger o ZIP do backup do Gestores."); }
    }

    static string FindPgRestore() {
        var bundled=Path.Combine(AppContext.BaseDirectory,"tools","postgresql","pg_restore.exe");if(File.Exists(bundled))return bundled;
        foreach(var version in new[]{"17","16","15","14","13","12"}){var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"PostgreSQL",version,"bin","pg_restore.exe");if(File.Exists(path))return path;}
        throw new ArgumentException("O componente de leitura do backup do Gestores não foi encontrado nesta instalação.");
    }

    static void ExtractTable(string executable,string dump,string folder,string table) {
        var output=Path.Combine(folder,table+".sql");var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
        foreach(var arg in new[]{"--data-only","--no-owner","--no-privileges","--table",table,"--file",output,dump})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new ArgumentException("Não foi possível iniciar a leitura do backup do Gestores.");
        var error=process.StandardError.ReadToEnd();if(!process.WaitForExit(120000)){try{process.Kill(true);}catch{}throw new ArgumentException("A leitura do backup do Gestores excedeu o tempo esperado.");}
        if(process.ExitCode!=0)throw new ArgumentException("O arquivo não é um backup PostgreSQL compatível do Gestores. "+error.Trim());
    }

    static GestoresImportPreview ConvertToMonalisa(string current,string output,string sqlFolder,string token) {
        var data=Tables.ToDictionary(name=>name,name=>ReadCopy(Path.Combine(sqlFolder,name+".sql")));
        using(var source=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=current,Mode=SqliteOpenMode.ReadOnly}.ToString()))
        using(var destination=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=output,Pooling=false}.ToString())){source.Open();destination.Open();source.BackupDatabase(destination);}
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=output,ForeignKeys=false,Pooling=false}.ToString());db.Open();
        Exec(db,"CREATE TABLE IF NOT EXISTS attachments(id INTEGER PRIMARY KEY,companyId INTEGER NOT NULL REFERENCES companies(id),titleId INTEGER REFERENCES titles(id),settlementId INTEGER REFERENCES settlements(id),name TEXT NOT NULL,mimeType TEXT NOT NULL,data BLOB NOT NULL,created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP)");
        foreach(var table in new[]{"attachments","movements","settlements","audit","titles","parties","accounts","plans","payment_methods","cost_centers","user_companies","access_audit","companies"})Exec(db,$"DELETE FROM {table}");
        Exec(db,"DELETE FROM users WHERE globalAdmin=0");

        var companyMap=new Dictionary<int,long>();var companyNames=new Dictionary<int,string>();
        foreach(var row in data["cad_filiais"].Where(Active)){
            var branch=Int(row,"cod_filial");var name=Upper(row,"razao_social");if(name.Length==0)name=Upper(row,"fantasia");var cnpj=Digits(row,"cnpj");if(cnpj.Length==0)cnpj="GESTORES-FILIAL-"+branch;
            Exec(db,"INSERT INTO companies(name,cnpj) VALUES(@p0,@p1)",name,cnpj);companyMap[branch]=LastId(db);companyNames[branch]=Upper(row,"fantasia");
        }
        if(companyMap.Count==0)throw new ArgumentException("O backup do Gestores não contém empresa ativa.");

        var payPeople=data["ctp_documento"].Where(Active).Select(r=>Key(r,"cod_filial","id_pessoa")).ToHashSet();
        var receivePeople=data["crb_documento"].Where(Active).Select(r=>Key(r,"cod_filial","id_pessoa")).ToHashSet();
        var documents=new Dictionary<string,string>();foreach(var row in data["pes_documento"].Where(r=>Text(r,"id_tip_documento") is "2" or "3")){var key=Key(row,"cod_filial","id_pessoa");if(!documents.ContainsKey(key)||Text(row,"id_tip_documento")=="3")documents[key]=Digits(row,"num_doc");}
        var phones=new Dictionary<string,string>();foreach(var row in data["pes_telefone"]){var key=Key(row,"cod_filial","id_pessoa");if(!phones.ContainsKey(key)||Text(row,"flg_def")=="S")phones[key]=Text(row,"num_tel");}
        var emails=new Dictionary<string,string>();foreach(var row in data["pes_email"]){var key=Key(row,"cod_filial","id_pessoa");if(!emails.ContainsKey(key))emails[key]=Upper(row,"email");}
        var partyMap=new Dictionary<string,long>();
        foreach(var row in data["pes_pessoa"].Where(Active)){var branch=Int(row,"cod_filial");if(!companyMap.ContainsKey(branch))continue;var key=Key(row,"cod_filial","id_pessoa");var type=payPeople.Contains(key)&&receivePeople.Contains(key)?"both":payPeople.Contains(key)?"supplier":receivePeople.Contains(key)?"customer":Text(row,"tipo")=="F"?"supplier":"customer";var document=documents.GetValueOrDefault(key,"");var personType=document.Length==14?"company":document.Length==11?"individual":"";Exec(db,"INSERT INTO parties(companyId,name,document,type,phone,email,personType) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6)",companyMap[branch],Upper(row,"razao_social"),document,type,phones.GetValueOrDefault(key,""),emails.GetValueOrDefault(key,Upper(row,"email")),personType);partyMap[key]=LastId(db);}

        var planMap=new Dictionary<string,string>();var planNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in data["cad_plano_contas"].Where(Active)){var branch=Int(row,"cod_filial");if(!companyMap.ContainsKey(branch))continue;var name=Upper(row,"desc_conta");if(name.Length==0)continue;var unique=branch+":"+name;if(planNames.Add(unique)){Exec(db,"INSERT INTO plans(companyId,name,kind,interestRate,penaltyRate,discountRate) VALUES(@p0,@p1,'payable',0,0,0)",companyMap[branch],name);Exec(db,"INSERT INTO plans(companyId,name,kind,interestRate,penaltyRate,discountRate) VALUES(@p0,@p1,'receivable',0,0,0)",companyMap[branch],name);}planMap[Key(row,"cod_filial","id_pl_conta")]=name;}
        var costCenters=0;foreach(var row in data["cad_centro_custos"].Where(Active)){var branch=Int(row,"cod_filial");var name=Upper(row,"desc_custo");if(companyMap.ContainsKey(branch)&&name.Length>0&&Scalar(db,"SELECT 1 FROM cost_centers WHERE companyId=@p0 AND upper(name)=upper(@p1)",companyMap[branch],name) is null){Exec(db,"INSERT INTO cost_centers(companyId,name) VALUES(@p0,@p1)",companyMap[branch],name);costCenters++;}}
        var methods=new Dictionary<string,long>();foreach(var row in data["cad_forma_pagamento"].Where(Active)){var branch=Int(row,"cod_filial");var name=Upper(row,"desc_forma");if(!companyMap.ContainsKey(branch)||name.Length==0)continue;var id=Scalar(db,"SELECT id FROM payment_methods WHERE companyId=@p0 AND upper(name)=upper(@p1)",companyMap[branch],name);if(id is null){Exec(db,"INSERT INTO payment_methods(companyId,name,kind) VALUES(@p0,@p1,'both')",companyMap[branch],name);id=LastId(db);}methods[branch+":"+name]=Convert.ToInt64(id);}

        long EnsureMethod(int branch,string name){var key=branch+":"+name;if(methods.TryGetValue(key,out var id))return id;Exec(db,"INSERT INTO payment_methods(companyId,name,kind) VALUES(@p0,@p1,'both')",companyMap[branch],name);return methods[key]=LastId(db);}
        var accounts=new Dictionary<string,long>();var fallback=new Dictionary<int,long>();
        foreach(var row in data["ban_conta_corrente"].Where(Active)){var branch=Int(row,"cod_filial");if(!companyMap.ContainsKey(branch))continue;Exec(db,"INSERT INTO accounts(companyId,name,opening,type) VALUES(@p0,@p1,0,'bank')",companyMap[branch],$"BANCO - {Upper(row,"titular")} - {Upper(row,"conta")}");accounts[$"{branch}:BANK:{Text(row,"id_conta_corrente")}"]=LastId(db);}
        foreach(var row in data["cx_caixa"].Where(Active)){var branch=Int(row,"cod_filial");if(!companyMap.ContainsKey(branch))continue;var name="CAIXA - "+Upper(row,"desc_caixa");var existing=Scalar(db,"SELECT id FROM accounts WHERE companyId=@p0 AND upper(name)=upper(@p1)",companyMap[branch],name);if(existing is null){Exec(db,"INSERT INTO accounts(companyId,name,opening,type) VALUES(@p0,@p1,@p2,'cash')",companyMap[branch],name,Money(row,"saldo_inicial"));existing=LastId(db);}var id=Convert.ToInt64(existing);accounts[$"{branch}:CASH:{Text(row,"id_caixa")}"]=id;fallback.TryAdd(branch,id);}
        foreach(var branch in companyMap.Keys)if(!fallback.ContainsKey(branch)){Exec(db,"INSERT INTO accounts(companyId,name,opening,type) VALUES(@p0,'CAIXA',0,'cash')",companyMap[branch]);fallback[branch]=LastId(db);}
        long AccountFor(Dictionary<string,string?> row,int branch){if(Text(row,"id_caixa").Length>0&&accounts.TryGetValue($"{branch}:CASH:{Text(row,"id_caixa")}",out var cash))return cash;if(Text(row,"id_conta_corrente").Length>0&&accounts.TryGetValue($"{branch}:BANK:{Text(row,"id_conta_corrente")}",out var bank))return bank;return fallback[branch];}
        long? MethodFor(Dictionary<string,string?> row,int branch){foreach(var item in new[]{("pix","PIX"),("dinheiro","DINHEIRO"),("cartao_credito","CARTÃO DE CRÉDITO"),("cartao_debito","CARTÃO DE DÉBITO"),("banco","TRANSFERÊNCIA BANCÁRIA"),("cheque_vista","CHEQUE"),("cheque_pre","CHEQUE PRÉ-DATADO"),("carteira_digital","CARTEIRA DIGITAL"),("outros","OUTROS")})if(row.ContainsKey(item.Item1)&&Money(row,item.Item1)>0)return EnsureMethod(branch,item.Item2);return null;}

        var sourceTitles=new List<(string Kind,Dictionary<string,string?> Row)>();sourceTitles.AddRange(data["ctp_documento"].Where(Active).Select(r=>("payable",r)));sourceTitles.AddRange(data["crb_documento"].Where(Active).Select(r=>("receivable",r)));
        var grouped=sourceTitles.GroupBy(item=>$"{item.Kind}|{Text(item.Row,"cod_filial")}|{Text(item.Row,"id_pessoa")}|{(item.Kind=="payable"?Text(item.Row,"nr_documento"):Text(item.Row,"documento"))}|{Text(item.Row,"data_emissao")}");
        var titleMap=new Dictionary<string,long>();var payables=0;var receivables=0;long payablesTotal=0,receivablesTotal=0;
        foreach(var group in grouped){var ordered=group.OrderBy(x=>Int(x.Row,"parcela")).ThenBy(x=>Text(x.Row,"data_vencimento")).ToList();var count=ordered.Count;var groupId=count>1?"GESTORES-"+Guid.NewGuid().ToString("N"):null;foreach(var item in ordered){var row=item.Row;var branch=Int(row,"cod_filial");if(!companyMap.ContainsKey(branch))continue;var personKey=Key(row,"cod_filial","id_pessoa");var party=partyMap.TryGetValue(personKey,out var partyId)?partyId:(long?)null;var category=planMap.GetValueOrDefault(Key(row,"cod_filial","id_pl_conta"),"SEM PLANO DE CONTAS");var partyName=party.HasValue?Convert.ToString(Scalar(db,"SELECT name FROM parties WHERE id=@p0",party.Value))??"SEM PESSOA":"SEM PESSOA";var description=item.Kind=="payable"&&Text(row,"historico").Length>0?Upper(row,"historico"):item.Kind=="receivable"&&Text(row,"obs").Length>0?Upper(row,"obs"):item.Kind=="payable"?"CONTA A PAGAR - "+partyName:"CONTA A RECEBER - "+partyName;var amount=Money(row,"valor_original");if(amount<=0)continue;var document=item.Kind=="payable"?Text(row,"nr_documento"):Text(row,"documento");var notes=item.Kind=="payable"?Upper(row,"obs"):Upper(row,"identificador");var created=Text(row,"data_in").Length>0?$"{Text(row,"data_in")}T{Text(row,"hora_in")}":DateTime.Now.ToString("s");var number=Math.Max(1,Int(row,"parcela"));Exec(db,"INSERT INTO titles(companyId,kind,description,amount,due,partyId,category,notes,cancelled,created,installmentGroup,installmentNumber,installmentCount,deleted,interestRate,penaltyRate,discountRate,documentNumber,issueDate,branch,costCenter,paymentMethodId) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,0,@p8,@p9,@p10,@p11,0,0,0,0,@p12,@p13,@p14,'',NULL)",companyMap[branch],item.Kind,description,amount,Text(row,"data_vencimento"),party,category,notes,created,groupId,number,count,document,Text(row,"data_emissao"),companyNames[branch]);var id=LastId(db);var sourceId=item.Kind=="payable"?Text(row,"id_pagar"):Text(row,"id_receber");titleMap[item.Kind+":"+sourceId]=id;if(item.Kind=="payable"){payables++;payablesTotal+=amount;}else{receivables++;receivablesTotal+=amount;}}}

        var settlements=0;long settlementTotal=0;
        void AddSettlement(string kind,Dictionary<string,string?> row,string sourceField){var key=kind+":"+Text(row,sourceField);if(!titleMap.TryGetValue(key,out var title))return;var branch=Int(row,"cod_filial");var principal=Money(row,"valor_original");if(principal<=0)principal=Money(row,"valor_pago");if(principal<=0)return;var interest=Money(row,"juros");var penalty=Money(row,"multa");var discount=Money(row,"desconto");var account=AccountFor(row,branch);var method=MethodFor(row,branch);Exec(db,"INSERT INTO settlements(companyId,titleId,accountId,principal,interest,penalty,discount,date,reversed,reason,additional,paymentMethodId) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,0,'',0,@p8)",companyMap[branch],title,account,principal,interest,penalty,discount,Text(row,"data"),method);var settlement=LastId(db);var cash=principal+interest+penalty-discount;var signed=kind=="payable"?-cash:cash;var description=Convert.ToString(Scalar(db,"SELECT description FROM titles WHERE id=@p0",title))??"";Exec(db,"INSERT INTO movements(companyId,accountId,amount,date,description,settlementId) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)",companyMap[branch],account,signed,Text(row,"data"),description,settlement);settlements++;settlementTotal+=principal;}
        foreach(var row in data["ctp_documento_baixa"])AddSettlement("payable",row,"id_pagar");foreach(var row in data["crb_documento_baixa"])AddSettlement("receivable",row,"id_receber");
        foreach(var company in companyMap.Values)Exec(db,"INSERT INTO audit(companyId,action,detail) VALUES(@p0,'IMPORTAÇÃO DO GESTORES',@p1)",company,"BACKUP CONFERIDO EM "+DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));
        if(Convert.ToString(Scalar(db,"PRAGMA integrity_check"))!="ok")throw new ArgumentException("O backup convertido não passou na verificação de integridade.");
        var companyLabel=string.Join(" / ",data["cad_filiais"].Where(Active).Select(r=>Upper(r,"razao_social")));
        return new(token,companyLabel,companyMap.Count,partyMap.Count,planNames.Count,costCenters,methods.Count,Convert.ToInt32(Scalar(db,"SELECT count(*) FROM accounts")),payables,payablesTotal,receivables,receivablesTotal,settlements,settlementTotal);
    }

    static List<Dictionary<string,string?>> ReadCopy(string path){var lines=File.ReadAllLines(path,Encoding.UTF8);var header=Array.FindIndex(lines,line=>line.StartsWith("COPY ",StringComparison.Ordinal));if(header<0)return[];var match=Regex.Match(lines[header],@"^COPY\s+[^\(]+\((.+)\)\s+FROM stdin;$");if(!match.Success)throw new ArgumentException("Estrutura inválida no backup do Gestores.");var columns=match.Groups[1].Value.Split(',').Select(x=>x.Trim()).ToArray();var rows=new List<Dictionary<string,string?>>();for(var i=header+1;i<lines.Length&&lines[i]!="\\.";i++){var values=lines[i].Split('\t');if(values.Length!=columns.Length)throw new ArgumentException("Dados inconsistentes no backup do Gestores.");var row=new Dictionary<string,string?>();for(var c=0;c<columns.Length;c++)row[columns[c]]=values[c]=="\\N"?null:Unescape(values[c]);rows.Add(row);}return rows;}
    static string Unescape(string value){var result=new StringBuilder();for(var i=0;i<value.Length;i++){if(value[i]!='\\'||i+1>=value.Length){result.Append(value[i]);continue;}var next=value[++i];result.Append(next switch{'t'=>'\t','n'=>'\n','r'=>'\r','b'=>'\b','f'=>'\f','v'=>'\v','\\'=>'\\',_=>next});}return result.ToString();}
    static bool Active(Dictionary<string,string?> row)=>Text(row,"sit")!="E"&&Text(row,"data_ex").Length==0;
    static string Text(Dictionary<string,string?> row,string key)=>row.TryGetValue(key,out var value)?(value??"").Trim():"";
    static string Upper(Dictionary<string,string?> row,string key)=>Text(row,key).ToUpperInvariant();
    static string Digits(Dictionary<string,string?> row,string key)=>Regex.Replace(Text(row,key),@"\D","");
    static string Key(Dictionary<string,string?> row,string first,string second)=>Text(row,first)+":"+Text(row,second);
    static int Int(Dictionary<string,string?> row,string key)=>int.TryParse(Text(row,key),out var value)?value:0;
    static long Money(Dictionary<string,string?> row,string key)=>decimal.TryParse(Text(row,key),NumberStyles.Number,CultureInfo.InvariantCulture,out var value)?Convert.ToInt64(Math.Round(value*100,0,MidpointRounding.AwayFromZero)):0;
    static void Exec(SqliteConnection db,string sql,params object?[] values){using var command=Command(db,sql,values);command.ExecuteNonQuery();}
    static object? Scalar(SqliteConnection db,string sql,params object?[] values){using var command=Command(db,sql,values);var value=command.ExecuteScalar();return value is DBNull?null:value;}
    static SqliteCommand Command(SqliteConnection db,string sql,object?[] values){var command=db.CreateCommand();command.CommandText=sql;for(var i=0;i<values.Length;i++)command.Parameters.AddWithValue("@p"+i,values[i]??DBNull.Value);return command;}
    static long LastId(SqliteConnection db)=>Convert.ToInt64(Scalar(db,"SELECT last_insert_rowid()"));
    static void TryDelete(string path){try{File.Delete(path);}catch{}}
}

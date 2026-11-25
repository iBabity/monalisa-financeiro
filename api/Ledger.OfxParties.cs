using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

sealed partial class Ledger
{
    static string OfxPartyName(string memo)
    {
        if (IsCofrinhos(memo) || Regex.IsMatch(memo, @"(?i)ENTRE CONTAS|MESMA TITULARIDADE")) return "";
        var match = Regex.Match(memo.ToUpperInvariant(), @"^\s*(?:PIX\s+(?:RECEBIDO|ENVIADO|PAGO|TRANSF|TRANSFER[ÊE]NCIA|QRS)|TED\s+(?:RECEBIDA|ENVIADA))\b\s*(?:(?:POR|DE|PARA)\b\s*:?\s*)?(.+)$");
        if (!match.Success) return "";
        var name = match.Groups[1].Value;
        name = Regex.Replace(name, @"\s*\d{2}/\d{2}(?:/\d{2,4})?\s*$", "");
        name = Regex.Replace(name, @"\s+(?:CPF|CNPJ|ID|E2E|AGENCIA|AG[.:]|CONTA)\b.*$", "");
        name = Regex.Replace(name, @"\s+\d[\d.*/-]{5,}.*$", "");
        name = Regex.Replace(name, @"\s+", " ").Trim(' ', ':', '-', '.');
        if (name.Length < 3 || name.Length > 120 || name.Count(char.IsLetter) < 3 || Regex.IsMatch(name, @"^(?:POR|PARA|DE|RECEBIDO|ENVIADO|TRANSFERENCIA|PAGAMENTO|SEM NOME|NAO INFORMADO|MOVIMENTO BANCARIO|COFRINHOS)$")) return "";
        return name;
    }

    static long? ResolveOfxParty(SqliteConnection c, int company, string kind, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = Required(name, "Nome do cliente ou fornecedor");
        var type = kind == "payable" ? "supplier" : "customer";
        var matches = Query(c, "SELECT id,type FROM parties WHERE companyId=@p0 AND upper(trim(name))=upper(@p1)", company, name);
        if (matches.Count > 1 || matches.Count == 1 && (string)matches[0]["type"]! is var existingType && existingType != type && existingType != "both")
            throw new ArgumentException("Confira o cadastro de " + name + ". Há um nome ambíguo ou cadastrado com outro tipo. Deixe o nome em branco para conciliar sem criar outro cadastro.");
        if (matches.Count == 1) return Number(matches[0], "id");
        Run(c, "INSERT INTO parties(companyId,name,document,type,phone,email,personType) VALUES(@p0,@p1,'',@p2,'','','')", company, name, type);
        var id = Number(Query(c, "SELECT last_insert_rowid() id")[0], "id");
        Audit(c, company, "Cadastro identificado no OFX", new { partyId = id, name, type });
        return id;
    }
}

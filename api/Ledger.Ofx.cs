using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

sealed partial class Ledger
{
    static string OfxTag(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{Regex.Escape(tag)}>\s*([^<\r\n]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }
    static string OfxDate(string value, bool required)
    {
        var digits = new string((value ?? "").TakeWhile(char.IsDigit).ToArray());
        if (digits.Length >= 8 && DateOnly.TryParseExact(digits[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date.ToString("yyyy-MM-dd");
        if (required) throw new ArgumentException("O OFX contém uma movimentação com data inválida."); return "";
    }
    static ParsedOfx ParseOfx(byte[] content)
    {
        if (content.Length == 0 || content.Length > 10 * 1024 * 1024) throw new ArgumentException("O arquivo OFX deve ter até 10 MB.");
        var header = Encoding.ASCII.GetString(content, 0, Math.Min(content.Length, 500));
        var text = header.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8.GetString(content) : Encoding.Latin1.GetString(content);
        if (!text.Contains("<OFX", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("O arquivo selecionado não é um OFX válido.");
        var bankId = OfxTag(text, "BANKID"); var start = OfxDate(OfxTag(text, "DTSTART"), false); var end = OfxDate(OfxTag(text, "DTEND"), false);
        var transactions = new List<ParsedOfxTransaction>(); var index = 0;
        foreach (Match block in Regex.Matches(text, @"(?is)<STMTTRN>(.*?)(?:</STMTTRN>|(?=<STMTTRN>)|(?=</BANKTRANLIST>))"))
        {
            index++; var value = block.Groups[1].Value; var rawAmount = OfxTag(value, "TRNAMT");
            if (!decimal.TryParse(rawAmount, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount)) throw new ArgumentException("O OFX contém um valor inválido.");
            var cents = checked((long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero)); if (cents == 0) continue;
            var date = OfxDate(OfxTag(value, "DTPOSTED"), true); var memo = (OfxTag(value, "MEMO") + " " + OfxTag(value, "NAME")).Trim().ToUpperInvariant(); if (memo.Length == 0) memo = "MOVIMENTO BANCÁRIO";
            var fitId = OfxTag(value, "FITID").Trim().ToUpperInvariant(); if (fitId.Length == 0) fitId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{date}|{cents}|{memo}|{index}")));
            transactions.Add(new(fitId.Length > 200 ? fitId[..200] : fitId, date, cents, memo.Length > 500 ? memo[..500] : memo, OfxTag(value, "TRNTYPE").ToUpperInvariant()));
        }
        if (transactions.Count == 0) throw new ArgumentException("Nenhuma movimentação foi encontrada no OFX.");
        var balanceBlock = Regex.Match(text, @"(?is)<LEDGERBAL>(.*?)(?:</LEDGERBAL>|$)").Groups[1].Value;
        var balanceDate = OfxDate(OfxTag(balanceBlock, "DTASOF"), false);
        long? balance = decimal.TryParse(OfxTag(balanceBlock, "BALAMT"), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var balanceValue)
            ? checked((long)Math.Round(balanceValue * 100m, 0, MidpointRounding.AwayFromZero)) : null;
        return new(bankId, start, end, transactions, balance, balanceDate);
    }
    static HashSet<string> MatchWords(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var clean = new string(normalized.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).Select(ch => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : ' ').ToArray());
        return clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => word.Length >= 3 && !new[] { "PAGAMENTO", "TRANSFERENCIA", "DEBITO", "PIX", "BANCO", "ITAU", "TED", "DOC" }.Contains(word)).ToHashSet();
    }
    public object PreviewOfx(int company, OfxPreviewInput x) => Write(c =>
    {
        Company(c, company); Existing(c, "accounts", x.AccountId, company); var account = Query(c, "SELECT * FROM accounts WHERE id=@p0", x.AccountId)[0];
        byte[] content; try { content = Convert.FromBase64String(x.DataBase64 ?? ""); } catch { throw new ArgumentException("Não foi possível ler o arquivo OFX."); }
        var parsed = ParseOfx(content); var debits = parsed.Transactions;
        var fingerprint = Convert.ToHexString(SHA256.HashData(content)); var existingImport = Query(c, "SELECT id FROM ofx_imports WHERE companyId=@p0 AND accountId=@p1 AND fingerprint=@p2", company, x.AccountId, fingerprint).SingleOrDefault();
        long importId; if (existingImport is null) { Run(c, "INSERT INTO ofx_imports(companyId,accountId,fileName,fingerprint,bankId,periodStart,periodEnd) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6)", company, x.AccountId, Required(x.FileName, "Nome do arquivo"), fingerprint, parsed.BankId, parsed.Start, parsed.End); importId = Number(Query(c, "SELECT last_insert_rowid() id")[0], "id"); } else importId = Number(existingImport, "id");
        Run(c, "UPDATE ofx_imports SET bankBalance=@p0,balanceDate=@p1 WHERE id=@p2", parsed.Balance, parsed.BalanceDate, importId);
        foreach (var item in debits) if (Query(c, "SELECT id FROM ofx_transactions WHERE companyId=@p0 AND accountId=@p1 AND fitId=@p2", company, x.AccountId, item.FitId).Count == 0) Run(c, "INSERT INTO ofx_transactions(importId,companyId,accountId,fitId,date,amount,memo,transactionType) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)", importId, company, x.AccountId, item.FitId, item.Date, item.Amount, item.Memo, item.Type);
        var openTitles = Query(c, """SELECT t.id,t.kind,t.description,t.due,COALESCE(p.name,'SEM FORNECEDOR') partyName,t.amount-COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) remaining FROM titles t LEFT JOIN parties p ON p.id=t.partyId WHERE t.companyId=@p0 AND t.deleted=0 AND t.cancelled=0 AND t.amount>COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) ORDER BY t.due,t.id""", company);
        var rows = new List<object>(); var duplicates = 0; var suggested = 0;
        foreach (var parsedItem in debits)
        {
            var stored = Query(c, "SELECT * FROM ofx_transactions WHERE companyId=@p0 AND accountId=@p1 AND fitId=@p2", company, x.AccountId, parsedItem.FitId)[0]; var reconciled = stored["settlementId"] is not null; var duplicate = reconciled; if (duplicate) duplicates++; var cash = Math.Abs(parsedItem.Amount); var kind = parsedItem.Amount < 0 ? "payable" : "receivable"; var words = MatchWords(parsedItem.Memo);
            var ranked = openTitles.Where(t => (string)t["kind"]! == kind).Select(title => { var remaining = Number(title, "remaining"); var days = Math.Abs(Date((string)title["due"]!).DayNumber - Date(parsedItem.Date).DayNumber); var common = words.Intersect(MatchWords($"{title["description"]} {title["partyName"]}")).Count(); var exact = remaining == cash; var score = (exact ? 100 : Math.Abs(remaining - cash) <= Math.Max(100, cash / 100) ? 35 : 0) + (days <= 3 ? 25 : days <= 7 ? 15 : days <= 30 ? 5 : 0) + Math.Min(30, common * 10); return new { title, score, exact, days }; }).OrderByDescending(v => v.score).ThenBy(v => v.days).ToList();
            var paidMatches = Query(c, """
                SELECT s.id,t.description FROM settlements s JOIN titles t ON t.id=s.titleId
                WHERE s.companyId=@p0 AND s.accountId=@p1 AND s.date=@p2 AND s.reversed=0 AND t.kind=@p3
                  AND s.principal+s.interest+s.penalty+s.additional-s.discount=@p4
                  AND NOT EXISTS(SELECT 1 FROM ofx_transactions o WHERE o.settlementId=s.id)
                """, company, x.AccountId, parsedItem.Date, kind, cash)
                .Where(t => MatchWords((string)t["description"]!).Intersect(words).Any()).ToList();
            var existingSettlementId = paidMatches.Count == 1 ? (long?)Number(paidMatches[0], "id") : null;
            var best = ranked.FirstOrDefault(); var recommend = !duplicate && best is not null && best.exact; if (recommend) suggested++;
            rows.Add(new { id = Number(stored, "id"), date = parsedItem.Date, amount = cash, kind, future = Date(parsedItem.Date) > CreditCardToday(), existingSettlementId, existingSettlementDescription = existingSettlementId.HasValue ? paidMatches[0]["description"] : null, memo = parsedItem.Memo, duplicate, reconciled, suggestedTitleId = recommend ? (long?)Number(best!.title, "id") : null, suggestedDescription = recommend ? (string)best!.title["description"]! : "", suggestedParty = recommend ? (string)best!.title["partyName"]! : "", suggestedDue = recommend ? (string)best!.title["due"]! : "", suggestedRemaining = recommend ? Number(best!.title, "remaining") : 0 });
        }
        Audit(c, company, "Importação OFX", new { importId, file = x.FileName, accountId = x.AccountId, debits = debits.Count, duplicates, suggested }); return new { importId, fileName = x.FileName, accountId = x.AccountId, accountName = account["name"], bankId = parsed.BankId, periodStart = parsed.Start, periodEnd = parsed.End, bankBalance = parsed.Balance, balanceDate = parsed.BalanceDate, currentBalance = Number(account, "opening") + Query(c, "SELECT COALESCE(SUM(amount),0) total FROM movements WHERE accountId=@p0", x.AccountId).Sum(r => Number(r, "total")), total = debits.Count, duplicates, suggested, transactions = rows };
    });
    public object ConfirmOfx(int company, OfxConfirmInput x) => Write(c =>
    {
        Company(c, company);
        if (x.Items is null || x.Items.Length > 500 || (x.Items.Length == 0 && !x.UpdateBalance)) throw new ArgumentException("Selecione movimentações ou a conciliação do saldo.");
        if (x.Items.Select(i => i.TransactionId).Distinct().Count() != x.Items.Length) throw new ArgumentException("Há movimentações repetidas na seleção.");
        var import = Query(c, "SELECT * FROM ofx_imports WHERE id=@p0 AND companyId=@p1", x.ImportId, company).SingleOrDefault() ?? throw new ArgumentException("Importação não encontrada.");
        var accountId = Convert.ToInt32(Number(import, "accountId")); var settlementIds = new List<long>(); long total = 0;
        foreach (var item in x.Items)
        {
            // Um FITID pode reaparecer em arquivos de períodos sobrepostos.
            var transaction = Query(c, "SELECT * FROM ofx_transactions WHERE id=@p0 AND companyId=@p1 AND accountId=@p2", item.TransactionId, company, accountId).SingleOrDefault() ?? throw new ArgumentException("Movimentação não pertence à conta.");
            if (transaction["settlementId"] is not null) throw new ArgumentException("Movimentação já conciliada.");
            var date = (string)transaction["date"]!;
            if (Date(date) > CreditCardToday()) throw new ArgumentException("Movimentações futuras devem aguardar a data de realização.");
            var signedAmount = Number(transaction, "amount"); var cash = Math.Abs(signedAmount); var kind = signedAmount < 0 ? "payable" : "receivable";
            long settlementId;
            if (item.ExistingSettlementId.HasValue)
            {
                var paid = Query(c, """
                    SELECT s.id FROM settlements s JOIN titles t ON t.id=s.titleId
                    WHERE s.id=@p0 AND s.companyId=@p1 AND s.accountId=@p2 AND s.date=@p3 AND s.reversed=0 AND t.kind=@p4
                      AND s.principal+s.interest+s.penalty+s.additional-s.discount=@p5
                      AND NOT EXISTS(SELECT 1 FROM ofx_transactions o WHERE o.settlementId=s.id)
                    """, item.ExistingSettlementId.Value, company, accountId, date, kind, cash).SingleOrDefault()
                    ?? throw new ArgumentException("A baixa existente não corresponde à movimentação.");
                settlementId = Number(paid, "id");
            }
            else
            {
                var titleId = item.TitleId;
                if (item.Create)
                {
                    var description = Required(string.IsNullOrWhiteSpace(item.Description) ? (string)transaction["memo"]! : item.Description, "Descrição");
                    var category = Required(string.IsNullOrWhiteSpace(item.Category) ? (kind == "payable" ? "DESPESAS BANCÁRIAS A CLASSIFICAR" : "RECEITAS BANCÁRIAS A CLASSIFICAR") : item.Category, "Plano de contas");
                    Run(c, "INSERT OR IGNORE INTO plans(companyId,name,kind) VALUES(@p0,@p1,@p2)", company, category, kind);
                    Run(c, "INSERT INTO titles(companyId,kind,description,amount,due,category,notes,documentNumber,issueDate) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p4)", company, kind, description, cash, date, category, "CRIADO NA CONCILIAÇÃO OFX", transaction["fitId"]);
                    titleId = Convert.ToInt32(Number(Query(c, "SELECT last_insert_rowid() id")[0], "id"));
                }
                var title = Query(c, TitlesSql, company).SingleOrDefault(t => Number(t, "id") == titleId) ?? throw new ArgumentException("Lançamento não encontrado.");
                if ((string)title["kind"]! != kind || Number(title, "cancelled") != 0 || Number(title, "remaining") <= 0) throw new ArgumentException("Selecione um lançamento em aberto do mesmo tipo.");
                var principal = Math.Min(cash, Number(title, "remaining"));
                settlementId = SettleCore(c, company, titleId, new(accountId, principal, 0, 0, 0, date, Math.Max(0, cash - principal)));
            }
            Run(c, "UPDATE ofx_transactions SET settlementId=@p0 WHERE id=@p1", settlementId, item.TransactionId);
            settlementIds.Add(settlementId); total = checked(total + signedAmount);
        }
        long balanceAdjustment = 0;
        if (x.UpdateBalance)
        {
            var date = (string)import["balanceDate"]!;
            if (import["bankBalance"] is null || date.Length == 0 || Date(date) > CreditCardToday()) throw new ArgumentException("O extrato não tem saldo realizado com data válida.");
            var current = Number(Query(c, "SELECT a.opening+COALESCE((SELECT SUM(m.amount) FROM movements m WHERE m.accountId=a.id AND m.date<=@p1),0) balance FROM accounts a WHERE a.id=@p0", accountId, date)[0], "balance");
            balanceAdjustment = Number(import, "bankBalance") - current;
            if (balanceAdjustment != 0) Run(c, "INSERT INTO movements(companyId,accountId,amount,date,description) VALUES(@p0,@p1,@p2,@p3,@p4)", company, accountId, balanceAdjustment, date, $"AJUSTE DE SALDO OFX #{x.ImportId} - SALDO BANCÁRIO CONFERIDO");
        }
        Audit(c, company, "Conciliação OFX", new { importId = x.ImportId, count = x.Items.Length, total, settlementIds, balanceAdjustment });
        return new { ok = true, count = x.Items.Length, total, settlementIds, balanceAdjustment };
    });
}

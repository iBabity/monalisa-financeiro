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
        var rows = new List<object>(); var duplicates = 0; var suggested = 0;
        foreach (var parsedItem in debits)
        {
            var stored = Query(c, "SELECT * FROM ofx_transactions WHERE companyId=@p0 AND accountId=@p1 AND fitId=@p2", company, x.AccountId, parsedItem.FitId)[0];
            if (Number(stored,"amount")!=parsedItem.Amount || (string)stored["date"]! != parsedItem.Date) throw new ArgumentException("O mesmo identificador OFX contém data ou valor diferente. Confira o extrato.");
            var fullCash=Math.Abs(parsedItem.Amount);var internalTransfer=IsCofrinhos(parsedItem.Memo);
            var legacyTransfer=internalTransfer && stored["movementId"] is null && stored["settlementId"] is not null;
            var covered=OfxCovered(c,Number(stored,"id"));if(covered>fullCash)throw new ArgumentException("A conciliação desta movimentação excede o valor do banco.");
            var cash=legacyTransfer?fullCash:fullCash-covered;
            var duplicate=stored["movementId"] is not null || (!legacyTransfer && cash==0);if(duplicate)duplicates++;
            var kind=parsedItem.Amount<0?"payable":"receivable";
            var match=duplicate?new OfxMatch([],null,false):FindOfxMatch(c,company,x.AccountId,kind,parsedItem.Date,cash,parsedItem.Memo);
            var paid=match.Review?[]:match.Paid;var recommend=!duplicate && (paid.Count>0 || match.TitleId.HasValue);if(recommend)suggested++;
            rows.Add(new { id=Number(stored,"id"),date=parsedItem.Date,amount=duplicate?fullCash:cash,bankAmount=fullCash,kind,memo=parsedItem.Memo,internalTransfer,reviewRequired=match.Review || (internalTransfer && match.Paid.Count>1),autoCreate=!duplicate&&!match.Review&&paid.Count==0&&!match.TitleId.HasValue,
                future=Date(parsedItem.Date)>CreditCardToday(),duplicate,reconciled=duplicate,
                existingSettlementId=paid.Count>0?(long?)Number(paid[0],"id"):null,existingSettlementIds=paid.Select(p=>Number(p,"id")).ToArray(),
                existingSettlementDescription=paid.Count>1?$"VINCULAR {paid.Count} BAIXAS E CLASSIFICAR SOMENTE A DIFERENÇA":paid.Count==1?(string)paid[0]["description"]!:null,
                existingSettlementChoices=match.Paid.Select(p=>new{id=Number(p,"id"),description=p["description"],date=p["date"],amount=SettlementCash(p)}).ToArray(),
                suggestedTitleId=match.TitleId,suggestedDescription="",suggestedParty=OfxPartyName(parsedItem.Memo),suggestedDue="",suggestedRemaining=cash });
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
            var date=(string)transaction["date"]!;
            if(Date(date)>CreditCardToday())throw new ArgumentException("Movimentações futuras devem aguardar a data de realização.");
            var signedAmount=Number(transaction,"amount");var fullCash=Math.Abs(signedAmount);var tid=Number(transaction,"id");
            var internalTransfer=IsCofrinhos((string)transaction["memo"]!);
            var legacyTransfer=internalTransfer && transaction["movementId"] is null && transaction["settlementId"] is not null;
            var cash=fullCash-OfxCovered(c,tid);
            if(transaction["movementId"] is not null || (!legacyTransfer && cash<=0))throw new ArgumentException("Movimentação já conciliada.");
            if(internalTransfer){var existing=FindOfxMatch(c,company,accountId,kind:signedAmount<0?"payable":"receivable",date:date,cash:fullCash,memo:(string)transaction["memo"]!);if(existing.Review || existing.Paid.Count>1)throw new ArgumentException("Confira as transferências semelhantes de cofrinhos antes de confirmar.");RecordOfxTransfer(c,company,accountId,transaction);total=checked(total+signedAmount);continue;}
            var kind=signedAmount<0?"payable":"receivable";
            var match=FindOfxMatch(c,company,accountId,kind,date,cash,(string)transaction["memo"]!);
            var ids=item.ExistingSettlementIds is { Length:>0 }?item.ExistingSettlementIds:item.ExistingSettlementId.HasValue?(!match.Review && match.Paid.Count>1 && match.Paid.Any(p=>Number(p,"id")==item.ExistingSettlementId.Value)?match.Paid.Select(p=>Number(p,"id")).ToArray():new[]{item.ExistingSettlementId.Value}):item.Create&&!item.ConfirmNew&&!match.Review?match.Paid.Select(p=>Number(p,"id")).ToArray():Array.Empty<long>();
            if(ids.Distinct().Count()!=ids.Length)throw new ArgumentException("Há baixas repetidas na seleção.");
            if(item.Create && !item.ConfirmNew && match.Review && ids.Length==0)throw new ArgumentException("Há baixas ou parcelas semelhantes. Escolha a correspondência antes de criar outro lançamento.");
            if(item.ConfirmNew)Audit(c,company,"Criação OFX confirmada",new{transactionId=tid,reason="USUÁRIO CONFIRMOU QUE É OUTRO PAGAMENTO"});
            var retained=OfxRetained(c,transaction);
            if(ids.Length>0 && retained.Length>0)throw new ArgumentException("Há saldo preservado de um estorno. Escolha a parcela em aberto para reaproveitar esse movimento.");
            long linkedCash=0;
            foreach(var sid in ids)
            {
                var paid=Query(c,"SELECT s.*,t.kind,t.description,COALESCE(p.name,'') partyName FROM settlements s JOIN titles t ON t.id=s.titleId LEFT JOIN parties p ON p.id=t.partyId WHERE s.id=@p0 AND s.companyId=@p1 AND s.accountId=@p2 AND s.reversed=0 AND t.deleted=0 AND t.cancelled=0 AND t.kind=@p3 AND NOT EXISTS(SELECT 1 FROM ofx_allocations a WHERE a.settlementId=s.id) AND NOT EXISTS(SELECT 1 FROM ofx_transactions o WHERE o.settlementId=s.id)",sid,company,accountId,kind).SingleOrDefault()??throw new ArgumentException("A baixa selecionada já foi vinculada ou não pertence à conta.");
                if(Math.Abs(Date((string)paid["date"]!).DayNumber-Date(date).DayNumber)>31 || (!OfxNameMatches((string)transaction["memo"]!,$"{paid["description"]} {paid["partyName"]}") && OfxCardMatch(c,company,(string)transaction["memo"]!,Number(paid,"titleId"))<0))throw new ArgumentException("Confira a data e a descrição da baixa existente.");
                var amount=SettlementCash(paid);linkedCash=checked(linkedCash+amount);if(linkedCash>cash)throw new ArgumentException("As baixas selecionadas excedem o valor do banco.");
                if((string)paid["date"]! != date){Run(c,"UPDATE settlements SET date=@p0 WHERE id=@p1",date,sid);Run(c,"UPDATE movements SET date=@p0 WHERE settlementId=@p1 AND date=@p2",date,sid,paid["date"]);Audit(c,company,"Correção de data da baixa",new{settlementId=sid,previousDate=paid["date"],date,reason="DATA CONFERIDA NO OFX"});}
                LinkOfx(c,tid,sid,amount);settlementIds.Add(sid);
            }
            var remaining=cash-linkedCash;
            if(remaining>0)
            {
                var titleId=item.TitleId;
                if(titleId==0 && item.Create && !item.ConfirmNew && linkedCash==0 && match.TitleId.HasValue)titleId=checked((int)match.TitleId.Value);
                if(titleId==0)
                {
                    if(!item.Create && ids.Length==0)throw new ArgumentException("Selecione a parcela ou a criação do lançamento.");
                    var description=Required(string.IsNullOrWhiteSpace(item.Description)?(string)transaction["memo"]!+(linkedCash>0?" - DIFERENÇA A CLASSIFICAR":""):item.Description,"Descrição");
                    var category=Required(string.IsNullOrWhiteSpace(item.Category)?(kind=="payable"?"DESPESAS BANCÁRIAS A CLASSIFICAR":"RECEITAS BANCÁRIAS A CLASSIFICAR"):item.Category,"Plano de contas");
                    Run(c,"INSERT OR IGNORE INTO plans(companyId,name,kind) VALUES(@p0,@p1,@p2)",company,category,kind);
                    Run(c,"INSERT INTO titles(companyId,kind,description,amount,due,category,notes,documentNumber,issueDate) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p4)",company,kind,description,remaining,date,category,"CRIADO NA CONCILIAÇÃO OFX; DIFERENÇA NÃO IDENTIFICADA AUTOMATICAMENTE",transaction["fitId"]);
                    titleId=checked((int)Number(Query(c,"SELECT last_insert_rowid() id")[0],"id"));
                    Run(c,"UPDATE titles SET plannedAccountId=@p0,partyId=@p2 WHERE id=@p1",accountId,titleId,ResolveOfxParty(c,company,kind,item.PartyName ?? OfxPartyName((string)transaction["memo"]!)));
                }
                var title=Query(c,TitlesSql,company).SingleOrDefault(t=>Number(t,"id")==titleId)??throw new ArgumentException("Lançamento não encontrado.");
                if(title["partyId"] is null)
                {
                    var partyId=ResolveOfxParty(c,company,kind,item.PartyName ?? OfxPartyName((string)transaction["memo"]!));
                    if(partyId.HasValue)Run(c,"UPDATE titles SET partyId=@p0 WHERE id=@p1",partyId.Value,titleId);
                }
                if((string)title["kind"]! != kind || Number(title,"cancelled")!=0 || Number(title,"remaining")<=0)throw new ArgumentException("Selecione um lançamento em aberto do mesmo tipo.");
                var principal=Math.Min(remaining,Number(title,"remaining"));
                var sid=SettleCore(c,company,titleId,new(accountId,principal,0,0,0,date,Math.Max(0,remaining-principal)),retainedSettlementIds:retained);
                Run(c,"UPDATE ofx_allocations SET retained=0 WHERE transactionId=@p0",tid);
                LinkOfx(c,tid,sid,remaining);settlementIds.Add(sid);
            }
            if(OfxCovered(c,tid)!=fullCash)throw new ArgumentException("O rateio não fecha com o valor da movimentação bancária.");
            Audit(c,company,"Rateio do pagamento OFX",new{fitId=transaction["fitId"],bankAmount=fullCash,allocations=Query(c,"SELECT a.settlementId,a.cash AS amount FROM ofx_allocations a JOIN settlements s ON s.id=a.settlementId WHERE a.transactionId=@p0 AND s.reversed=0",tid)});
            total = checked(total + signedAmount);
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

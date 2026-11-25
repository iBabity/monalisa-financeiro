using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed partial class Ledger
{
    static bool IsCofrinhos(string memo) { var words=MatchWords(memo);return (words.Contains("COFRINHOS")||words.Contains("COFRINHO"))&&(words.Contains("APLICACAO")||words.Contains("RESGATE")); }
    static HashSet<string> OfxIdentity(string value)
    {
        var words = MatchWords(value.ToUpperInvariant().Replace("C&A", " CEA ").Replace("C & A", " CEA "));
        foreach (var alias in new[] { ("SALARIOS", "SALARIO"), ("CPFL", "ENERGIA"), ("CEA", "VESTUARIOCEA") })
            if (words.Remove(alias.Item1)) words.Add(alias.Item2);
        words.ExceptWith(new[] { "PAGTO", "TRANSF", "QRS", "PAY", "FUN", "PARCELA", "PARCELAS", "PAGO", "PAGA", "PAG", "BOLETO", "BRASIL", "FATURA", "FINAL" });
        return words;
    }
    static bool OfxNameMatches(string memo, string description) => OfxIdentity(memo).Intersect(OfxIdentity(description)).Any();
    static long SettlementCash(Dictionary<string, object?> s) => Number(s,"principal")+Number(s,"interest")+Number(s,"penalty")+Number(s,"additional")-Number(s,"discount");
    static long OfxCovered(SqliteConnection c, long tid) => Number(Query(c,"SELECT COALESCE(SUM(a.cash),0) total FROM ofx_allocations a JOIN settlements s ON s.id=a.settlementId WHERE a.transactionId=@p0 AND s.reversed=0",tid)[0],"total");
    static long[] OfxRetained(SqliteConnection c, Dictionary<string,object?> row)
    {
        var ids = Query(c,"SELECT a.settlementId FROM ofx_allocations a JOIN settlements s ON s.id=a.settlementId WHERE a.transactionId=@p0 AND a.retained=1 AND s.reversed=1",Number(row,"id")).Select(r=>Number(r,"settlementId")).ToList();
        if(row["retainedMovementId"] is not null) ids.AddRange(Query(c,"SELECT settlementId FROM movements WHERE id=@p0 AND settlementId IS NOT NULL",Number(row,"retainedMovementId")).Select(r=>Number(r,"settlementId")));
        return ids.Distinct().ToArray();
    }
    static void RestoreOfxAllocations(SqliteConnection c)
    {
        // Recover only explicit splits recorded in previous reconciliations.
        foreach(var entry in Query(c,"SELECT companyId,detail FROM audit WHERE action='Rateio do pagamento OFX'"))
        {
            try
            {
                using var doc=JsonDocument.Parse((string)entry["detail"]!);var d=doc.RootElement;
                var transaction=Query(c,"SELECT * FROM ofx_transactions WHERE companyId=@p0 AND fitId=@p1",Number(entry,"companyId"),d.GetProperty("fitId").GetString()).SingleOrDefault();
                if(transaction is null) continue;
                var allocations=d.GetProperty("allocations").EnumerateArray().Select(a=>(Id:a.GetProperty("settlementId").GetInt64(),Cash:a.GetProperty("amount").GetInt64())).ToArray();
                if(allocations.Sum(a=>a.Cash)!=Math.Abs(Number(transaction,"amount")))continue;
                if(allocations.Any(a=>Query(c,"SELECT id FROM settlements WHERE id=@p0 AND companyId=@p1 AND accountId=@p2 AND reversed=0 AND principal+interest+penalty+additional-discount=@p3",a.Id,transaction["companyId"],transaction["accountId"],a.Cash).Count!=1))continue;
                foreach(var a in allocations)Run(c,"INSERT OR IGNORE INTO ofx_allocations(transactionId,settlementId,cash) VALUES(@p0,@p1,@p2)",transaction["id"],a.Id,a.Cash);
            }
            catch(JsonException) {} catch(KeyNotFoundException) {} catch(InvalidOperationException) {}
        }
        foreach(var row in Query(c,"SELECT o.* FROM ofx_transactions o JOIN settlements s ON s.id=o.settlementId WHERE s.reversed=1"))
        {
            if(!IsCofrinhos((string)row["memo"]!))continue;
            var movements=Query(c,"SELECT * FROM movements WHERE settlementId=@p0",row["settlementId"]);
            if(movements.Count==1 && Number(movements[0],"amount")==Number(row,"amount"))Run(c,"UPDATE ofx_transactions SET movementId=@p0,settlementId=NULL WHERE id=@p1",movements[0]["id"],row["id"]);
        }
    }
    sealed record OfxMatch(List<Dictionary<string,object?>> Paid, long? TitleId, bool Review);
    static int OfxCardMatch(SqliteConnection c,int company,string memo,long titleId)
    {
        if(!memo.Contains("FATURA",StringComparison.OrdinalIgnoreCase))return 0;
        var invoice=Query(c,"SELECT cardId FROM credit_card_invoices WHERE titleId=@p0 AND companyId=@p1",titleId,company).SingleOrDefault();
        if(invoice is null)return 0;
        bool known=false;
        foreach(var entry in Query(c,"SELECT detail FROM audit WHERE companyId=@p0 AND action='Conciliação da fatura do cartão'",company))
        {
            try
            {
                using var doc=JsonDocument.Parse((string)entry["detail"]!);var d=doc.RootElement;
                var source=Query(c,"SELECT cardId FROM credit_card_invoices WHERE id=@p0 AND companyId=@p1",d.GetProperty("invoiceId").GetInt64(),company).SingleOrDefault();
                if(source is null || Number(source,"cardId")!=Number(invoice,"cardId"))continue;
                foreach(var fit in d.GetProperty("fitIds").EnumerateArray())
                {
                    var bank=Query(c,"SELECT memo FROM ofx_transactions WHERE companyId=@p0 AND fitId=@p1",company,fit.GetString()).FirstOrDefault();
                    if(bank is null)continue;known=true;
                    if(OfxIdentity(memo).Intersect(OfxIdentity((string)bank["memo"]!)).Any())return 1;
                }
            }
            catch(JsonException){}catch(KeyNotFoundException){}catch(InvalidOperationException){}
        }
        return known?-1:0;
    }
    static OfxMatch FindOfxMatch(SqliteConnection c,int company,int account,string kind,string date,long cash,string memo)
    {
        var allPaid=Query(c,"""
            SELECT s.*,t.description,t.kind,COALESCE(p.name,'') partyName FROM settlements s
            JOIN titles t ON t.id=s.titleId LEFT JOIN parties p ON p.id=t.partyId
            WHERE s.companyId=@p0 AND s.accountId=@p1 AND s.reversed=0 AND t.deleted=0 AND t.cancelled=0 AND t.kind=@p2
              AND NOT EXISTS(SELECT 1 FROM ofx_transactions o WHERE o.settlementId=s.id)
              AND NOT EXISTS(SELECT 1 FROM ofx_allocations a WHERE a.settlementId=s.id)
            ORDER BY s.date,s.id
            """,company,account,kind);
        var candidates=allPaid.Where(s=>Math.Abs(Date((string)s["date"]!).DayNumber-Date(date).DayNumber)<=31 &&
                (OfxNameMatches(memo,$"{s["description"]} {s["partyName"]}") || OfxCardMatch(c,company,memo,Number(s,"titleId"))==1 ||
                 (memo.Contains("FATURA",StringComparison.OrdinalIgnoreCase) && ((string)s["description"]!).Contains("FATURA",StringComparison.OrdinalIgnoreCase) && OfxCardMatch(c,company,memo,Number(s,"titleId"))==0))).ToList();
        var paid=candidates.Where(s=>Math.Abs(Date((string)s["date"]!).DayNumber-Date(date).DayNumber)<=3).ToList();
        if(paid.Count==0 && candidates.Count>0)return new(candidates,null,true);
        if(paid.Any(s=>!OfxNameMatches(memo,$"{s["description"]} {s["partyName"]}") && OfxCardMatch(c,company,memo,Number(s,"titleId"))!=1))return new(paid,null,true);
        var exact=paid.Where(s=>SettlementCash(s)==cash).ToList();
        if(exact.Count>0)return new(exact,null,exact.Count!=1);
        if(paid.Count>=1 && (paid.Count>=2 || OfxCardMatch(c,company,memo,Number(paid[0],"titleId"))==1) && paid.Select(s=>s["titleId"]).Distinct().Count()==paid.Count && paid.All(s=>SettlementCash(s)>0) && paid.Sum(SettlementCash)<=cash)return new(paid,null,false);
        if(paid.Count>0)return new(paid,null,true);
        var possible=Query(c,TitlesSql,company).Where(t=>(string)t["kind"]! == kind && Number(t,"cancelled")==0 && Number(t,"remaining")>0 &&
            (OfxNameMatches(memo,$"{t["description"]} {t["partyName"]}") || OfxCardMatch(c,company,memo,Number(t,"id"))==1)).ToList();
        var titles=possible.Where(t=>(t["plannedAccountId"] is null || Number(t,"plannedAccountId")==account) && Number(t,"remaining")==cash && Math.Abs(Date((string)t["due"]!).DayNumber-Date(date).DayNumber)<=7).ToList();
        if(titles.Count==0)
        {
            var weak=allPaid.Where(s=>SettlementCash(s)==cash && Math.Abs(Date((string)s["date"]!).DayNumber-Date(date).DayNumber)<=3).ToList();
            if(weak.Count>0)return new(weak,null,true);
        }
        return new([],titles.Count==1?Number(titles[0],"id"):null,titles.Count>1 || (titles.Count==0 && possible.Count>0));
    }
    static void LinkOfx(SqliteConnection c,long tid,long sid,long cash)
    {
        Run(c,"INSERT INTO ofx_allocations(transactionId,settlementId,cash) VALUES(@p0,@p1,@p2)",tid,sid,cash);
        Run(c,"UPDATE ofx_transactions SET settlementId=@p0,retainedMovementId=NULL WHERE id=@p1",sid,tid);
    }
    void UnlinkOfx(SqliteConnection c,long sid,bool adjustBalance)
    {
        Run(c,"UPDATE ofx_allocations SET retained=@p0 WHERE settlementId=@p1",adjustBalance?0:1,sid);
        foreach(var row in Query(c,"SELECT o.id FROM ofx_transactions o WHERE o.settlementId=@p0 OR EXISTS(SELECT 1 FROM ofx_allocations a WHERE a.transactionId=o.id AND a.settlementId=@p0)",sid))
            Run(c,"UPDATE ofx_transactions SET settlementId=(SELECT a.settlementId FROM ofx_allocations a JOIN settlements s ON s.id=a.settlementId WHERE a.transactionId=@p0 AND s.reversed=0 LIMIT 1),retainedMovementId=NULL WHERE id=@p0",row["id"]);
    }
    void RecordOfxTransfer(SqliteConnection c,int company,int account,Dictionary<string,object?> row)
    {
        var amount=Number(row,"amount");var description=(amount>0?"TRANSFERÊNCIA COFRINHOS PARA CONTA":"TRANSFERÊNCIA DA CONTA PARA COFRINHOS")+" - OFX "+row["fitId"];
        if(row["settlementId"] is null)
        {
            var candidates=Query(c,"SELECT s.*,t.description FROM settlements s JOIN titles t ON t.id=s.titleId WHERE s.companyId=@p0 AND s.accountId=@p1 AND s.reversed=0 AND t.kind=@p2 AND s.principal+s.interest+s.penalty+s.additional-s.discount=@p3 AND NOT EXISTS(SELECT 1 FROM ofx_allocations a WHERE a.settlementId=s.id) AND NOT EXISTS(SELECT 1 FROM ofx_transactions o WHERE o.settlementId=s.id)",company,account,amount>0?"receivable":"payable",Math.Abs(amount)).Where(s=>IsCofrinhos((string)s["description"]!) && Math.Abs(Date((string)s["date"]!).DayNumber-Date((string)row["date"]!).DayNumber)<=3).ToList();
            if(candidates.Count>1)throw new ArgumentException("Há transferências de cofrinhos semelhantes já registradas. Confira os registros antes de conciliar.");
            if(candidates.Count==1){row["settlementId"]=candidates[0]["id"];LinkOfx(c,Number(row,"id"),Number(candidates[0],"id"),Math.Abs(amount));}
        }
        if(row["settlementId"] is not null)
        {
            var sid=Number(row,"settlementId");var old=Query(c,"SELECT * FROM settlements WHERE id=@p0",sid)[0];
            if(SettlementCash(old)!=Math.Abs(amount))throw new ArgumentException("Confira o rateio antes de reclassificar a transferência.");
            Run(c,"UPDATE settlements SET reversed=1,reason='RECLASSIFICADO COMO TRANSFERÊNCIA OFX; SALDO PRESERVADO' WHERE id=@p0",sid);
            Run(c,"UPDATE ofx_allocations SET retained=1 WHERE transactionId=@p0 AND settlementId=@p1",row["id"],sid);
            if(Query(c,"SELECT id FROM settlements WHERE titleId=@p0 AND reversed=0",old["titleId"]).Count==0)Run(c,"UPDATE titles SET deleted=1 WHERE id=@p0",old["titleId"]);
            Audit(c,company,"Reclassificação de transferência",new { settlementId=sid,adjustBalance=false,reason=description });
        }
        var retained=OfxRetained(c,row);long mid;
        if(retained.Length>0)
        {
            var movements=retained.SelectMany(id=>Query(c,"SELECT * FROM movements WHERE settlementId=@p0",id)).ToList();
            if(movements.Count!=1 || Number(movements[0],"amount")!=amount)throw new ArgumentException("Confira os movimentos preservados antes de classificar esta transferência.");
            mid=Number(movements[0],"id");Run(c,"UPDATE movements SET description=@p0 WHERE id=@p1",description,mid);
        }
        else
        {
            Run(c,"INSERT INTO movements(companyId,accountId,amount,date,description) VALUES(@p0,@p1,@p2,@p3,@p4)",company,account,amount,row["date"],description);
            mid=Number(Query(c,"SELECT last_insert_rowid() id")[0],"id");
        }
        Run(c,"UPDATE ofx_transactions SET movementId=@p0,settlementId=NULL,retainedMovementId=NULL WHERE id=@p1",mid,row["id"]);
        Run(c,"UPDATE ofx_allocations SET retained=0 WHERE transactionId=@p0",row["id"]);
        Audit(c,company,"Transferência OFX",new { transactionId=row["id"],movementId=mid,amount,description,date=row["date"],countsAsRevenueOrExpense=false });
    }
}

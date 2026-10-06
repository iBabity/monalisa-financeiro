using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;

sealed partial class Ledger
{
    public PdfReport FinancialReport(int company, string startValue, string endValue)
    {
        lock (gate)
        {
            using var c = Open(); Company(c, company);
            var start = Date(startValue); var selectedEnd = Date(endValue); if (selectedEnd < start) throw new ArgumentException("A data final não pode ser anterior à data inicial.");
            if (selectedEnd.DayNumber - start.DayNumber > 730) throw new ArgumentException("Escolha um período de até dois anos.");
            var end = selectedEnd.AddDays(1); var startText = start.ToString("yyyy-MM-dd"); var endText = end.ToString("yyyy-MM-dd");
            var companyRow = Query(c, "SELECT name,cnpj FROM companies WHERE id=@p0", company)[0];
            var titles = Query(c, TitlesSql, company).Where(row => string.CompareOrdinal((string)row["due"]!, startText) >= 0 && string.CompareOrdinal((string)row["due"]!, endText) < 0).ToList();
            var settlements = Query(c, """
            SELECT s.*,t.kind,t.description,a.name AS accountName
            FROM settlements s JOIN titles t ON t.id=s.titleId JOIN accounts a ON a.id=s.accountId
            WHERE s.companyId=@p0 AND s.reversed=0 AND t.deleted=0 AND t.cancelled=0
              AND s.date>=@p1 AND s.date<@p2
            ORDER BY s.date,s.id
            """, company, startText, endText);
            long Cash(Dictionary<string, object?> row) => Number(row, "principal") + Number(row, "interest") + Number(row, "penalty") + Number(row, "additional") - Number(row, "discount");
            string TitleStatus(Dictionary<string, object?> row)
            {
                if (Number(row, "cancelled") == 1) return "CANCELADO";
                if (Number(row, "remaining") == 0) return (string)row["kind"]! == "payable" ? "PAGO" : "RECEBIDO";
                if (string.CompareOrdinal((string)row["due"]!, DateTime.Today.ToString("yyyy-MM-dd")) < 0) return "VENCIDO";
                if (Number(row, "settled") > 0) return "PARCIAL";
                return "EM ABERTO";
            }
            var active = titles.Where(row => Number(row, "cancelled") == 0).ToList();
            var reportTitles = active;
            var data = new MonthlyReportData(
                (string)companyRow["name"]!, (string)companyRow["cnpj"]!, start, selectedEnd,
                active.Where(row => (string)row["kind"]! == "payable").Sum(row => Number(row, "amount")),
                active.Where(row => (string)row["kind"]! == "receivable").Sum(row => Number(row, "amount")),
                active.Where(row => (string)row["kind"]! == "payable").Sum(row => Number(row, "remaining")),
                active.Where(row => (string)row["kind"]! == "receivable").Sum(row => Number(row, "remaining")),
                settlements.Where(row => (string)row["kind"]! == "payable").Sum(Cash),
                settlements.Where(row => (string)row["kind"]! == "receivable").Sum(Cash),
                settlements.Select(row => new MonthlyReportLine(
                    Date((string)row["date"]!).ToString("dd/MM/yyyy"),
                    (string)row["kind"]! == "payable" ? "PAGAMENTO" : "RECEBIMENTO",
                    (string)row["description"]!, (string)row["accountName"]!, Cash(row), Principal: Number(row, "principal"), Interest: Number(row, "interest"), Penalty: Number(row, "penalty"), Discount: Number(row, "discount"), Additional: Number(row, "additional"))).ToList(),
                reportTitles.Select(row => new MonthlyReportLine(
                    Date((string)row["due"]!).ToString("dd/MM/yyyy"),
                    (string)row["kind"]! == "payable" ? "A PAGAR" : "A RECEBER",
                    (string)row["description"]!, (string)row["category"]!, Number(row, "amount"), Number(row, "cancelled") == 1 ? 0 : Number(row, "remaining"), TitleStatus(row))).ToList());
            var periodName = start == selectedEnd ? startText : $"{startText}-a-{endValue}";
            return new PdfReport(MonthlyReportPdf.Generate(data), $"relatorio-financeiro-{periodName}.pdf");
        }
    }
    public PdfReport PayableReport(int company, string monthValue, string type)
    {
        lock (gate)
        {
            using var c = Open(); Company(c, company);
            if (!DateOnly.TryParseExact(monthValue + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) throw new ArgumentException("Mês inválido.");
            var end = start.AddMonths(1); var startText = start.ToString("yyyy-MM-dd"); var endText = end.ToString("yyyy-MM-dd");
            var allowed = new[] { "payments", "by-plan", "month", "future" }; if (!allowed.Contains(type)) throw new ArgumentException("Tipo de relatório inválido.");
            var companyRow = Query(c, "SELECT name,cnpj FROM companies WHERE id=@p0", company)[0];
            var rows = new List<PayableReportRow>();
            if (type == "payments")
            {
                foreach (var row in Query(c, """
                SELECT t.id,t.description,t.documentNumber,t.amount,t.due,t.category,COALESCE(p.name,'SEM FORNECEDOR') AS supplier,
                       s.date AS paymentDate,s.principal,s.interest,s.penalty,s.discount,s.additional,
                       t.amount-COALESCE((SELECT SUM(x.principal) FROM settlements x WHERE x.titleId=t.id AND x.reversed=0),0) AS remaining
                FROM settlements s JOIN titles t ON t.id=s.titleId LEFT JOIN parties p ON p.id=t.partyId
                WHERE s.companyId=@p0 AND t.kind='payable' AND t.deleted=0 AND t.cancelled=0 AND s.reversed=0
                  AND s.date>=@p1 AND s.date<@p2 ORDER BY s.date,t.due,t.id,s.id
                """, company, startText, endText)) rows.Add(ReportRow(row, false));
            }
            else
            {
                var range = type == "future" ? "t.due>=@p1" : "t.due>=@p1 AND t.due<@p2";
                foreach (var row in Query(c, $"""
                SELECT t.id,t.description,t.documentNumber,t.amount,t.due,t.category,COALESCE(p.name,'SEM FORNECEDOR') AS supplier,
                       MAX(CASE WHEN s.reversed=0 THEN s.date END) AS paymentDate,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.principal ELSE 0 END),0) AS principal,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.interest ELSE 0 END),0) AS interest,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.penalty ELSE 0 END),0) AS penalty,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.discount ELSE 0 END),0) AS discount,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.additional ELSE 0 END),0) AS additional,
                       t.amount-COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.principal ELSE 0 END),0) AS remaining
                FROM titles t LEFT JOIN parties p ON p.id=t.partyId LEFT JOIN settlements s ON s.titleId=t.id
                WHERE t.companyId=@p0 AND t.kind='payable' AND t.deleted=0 AND t.cancelled=0 AND {range}
                GROUP BY t.id
                HAVING t.amount>COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.principal ELSE 0 END),0)
                ORDER BY t.due,t.id
                """, company, type == "future" ? endText : startText, endText)) rows.Add(ReportRow(row, false));
            }
            var title = type switch { "payments" => "EXTRATO DE PAGAMENTOS", "by-plan" => "CONTAS A PAGAR POR PLANO DE CONTAS", "month" => "EXTRATO DE CONTAS A PAGAR", _ => "CONTAS A PAGAR DOS PRÓXIMOS MESES" };
            var groups = type switch
            {
                "by-plan" => rows.GroupBy(r => r.Plan).OrderBy(g => g.Key).Select(g => new PayableReportGroup(g.Key, g.ToList())).ToList(),
                "future" => rows.GroupBy(r => r.Due[..7]).OrderBy(g => g.Key).Select(g => new PayableReportGroup(DateOnly.ParseExact(g.Key + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("MMMM 'DE' yyyy", CultureInfo.GetCultureInfo("pt-BR")).ToUpper(CultureInfo.GetCultureInfo("pt-BR")), g.ToList())).ToList(),
                _ => new List<PayableReportGroup> { new(start.ToString("MMMM 'DE' yyyy", CultureInfo.GetCultureInfo("pt-BR")).ToUpper(CultureInfo.GetCultureInfo("pt-BR")), rows) }
            };
            var data = new PayableReportData((string)companyRow["name"]!, (string)companyRow["cnpj"]!, title, start, "payable", type, groups);
            var slug = type switch { "payments" => "extrato-pagamentos", "by-plan" => "contas-pagar-por-plano", "month" => "extrato-contas-pagar", _ => "contas-pagar-proximos-meses" };
            return new PdfReport(PayableReportsPdf.Generate(data), $"{slug}-{monthValue}.pdf");
        }
    }
    public PdfReport ReceivableReport(int company, string monthValue, string type)
    {
        lock (gate)
        {
            using var c = Open(); Company(c, company);
            if (!DateOnly.TryParseExact(monthValue + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) throw new ArgumentException("Mês inválido.");
            var end = start.AddMonths(1); var startText = start.ToString("yyyy-MM-dd"); var endText = end.ToString("yyyy-MM-dd");
            var allowed = new[] { "receipts", "by-plan", "month", "future" }; if (!allowed.Contains(type)) throw new ArgumentException("Tipo de relatório inválido.");
            var companyRow = Query(c, "SELECT name,cnpj FROM companies WHERE id=@p0", company)[0];
            var rows = new List<PayableReportRow>();
            if (type == "receipts")
            {
                foreach (var row in Query(c, """
                SELECT t.id,t.description,t.documentNumber,t.amount,t.due,t.category,COALESCE(p.name,'SEM CLIENTE') AS supplier,
                       s.date AS paymentDate,s.principal,s.interest,s.penalty,s.discount,s.additional,
                       t.amount-COALESCE((SELECT SUM(x.principal) FROM settlements x WHERE x.titleId=t.id AND x.reversed=0),0) AS remaining
                FROM settlements s JOIN titles t ON t.id=s.titleId LEFT JOIN parties p ON p.id=t.partyId
                WHERE s.companyId=@p0 AND t.kind='receivable' AND t.deleted=0 AND t.cancelled=0 AND s.reversed=0
                  AND s.date>=@p1 AND s.date<@p2 ORDER BY s.date,t.due,t.id,s.id
                """, company, startText, endText)) rows.Add(ReportRow(row, true));
            }
            else
            {
                var range = type == "future" ? "t.due>=@p1" : "t.due>=@p1 AND t.due<@p2";
                foreach (var row in Query(c, $"""
                SELECT t.id,t.description,t.documentNumber,t.amount,t.due,t.category,COALESCE(p.name,'SEM CLIENTE') AS supplier,
                       MAX(CASE WHEN s.reversed=0 THEN s.date END) AS paymentDate,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.principal ELSE 0 END),0) AS principal,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.interest ELSE 0 END),0) AS interest,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.penalty ELSE 0 END),0) AS penalty,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.discount ELSE 0 END),0) AS discount,
                       COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.additional ELSE 0 END),0) AS additional,
                       t.amount-COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.principal ELSE 0 END),0) AS remaining
                FROM titles t LEFT JOIN parties p ON p.id=t.partyId LEFT JOIN settlements s ON s.titleId=t.id
                WHERE t.companyId=@p0 AND t.kind='receivable' AND t.deleted=0 AND t.cancelled=0 AND {range}
                GROUP BY t.id
                HAVING t.amount>COALESCE(SUM(CASE WHEN s.reversed=0 THEN s.principal ELSE 0 END),0)
                ORDER BY t.due,t.id
                """, company, type == "future" ? endText : startText, endText)) rows.Add(ReportRow(row, true));
            }
            var title = type switch { "receipts" => "EXTRATO DE RECEBIMENTOS", "by-plan" => "CONTAS A RECEBER POR PLANO DE CONTAS", "month" => "EXTRATO DE CONTAS A RECEBER", _ => "CONTAS A RECEBER DOS PRÓXIMOS MESES" };
            var groups = type switch
            {
                "by-plan" => rows.GroupBy(r => r.Plan).OrderBy(g => g.Key).Select(g => new PayableReportGroup(g.Key, g.ToList())).ToList(),
                "future" => rows.GroupBy(r => r.Due[..7]).OrderBy(g => g.Key).Select(g => new PayableReportGroup(DateOnly.ParseExact(g.Key + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("MMMM 'DE' yyyy", CultureInfo.GetCultureInfo("pt-BR")).ToUpper(CultureInfo.GetCultureInfo("pt-BR")), g.ToList())).ToList(),
                _ => new List<PayableReportGroup> { new(start.ToString("MMMM 'DE' yyyy", CultureInfo.GetCultureInfo("pt-BR")).ToUpper(CultureInfo.GetCultureInfo("pt-BR")), rows) }
            };
            var pdfType = type == "receipts" ? "payments" : type;
            var data = new PayableReportData((string)companyRow["name"]!, (string)companyRow["cnpj"]!, title, start, "receivable", pdfType, groups);
            var slug = type switch { "receipts" => "extrato-recebimentos", "by-plan" => "contas-receber-por-plano", "month" => "extrato-contas-receber", _ => "contas-receber-proximos-meses" };
            return new PdfReport(PayableReportsPdf.Generate(data), $"{slug}-{monthValue}.pdf");
        }
    }
    GenericReportData AdvancedData(SqliteConnection c, int company, string type, string startValue, string endValue, int? partyId, int? accountId)
    {
        var start = Date(startValue); var end = Date(endValue); if (end < start) throw new ArgumentException("A data final não pode ser anterior à inicial."); if (end.DayNumber - start.DayNumber > 1095) throw new ArgumentException("Escolha um período de até três anos."); var endExclusive = end.AddDays(1).ToString("yyyy-MM-dd"); var startText = start.ToString("yyyy-MM-dd"); var companyRow = Query(c, "SELECT name,cnpj FROM companies WHERE id=@p0", company)[0]; var rows = new List<IReadOnlyList<string>>(); var headers = new List<string>(); var title = ""; long total = 0; List<GenericReportGroup>? groups = null;
        string M(long value) => (value / 100m).ToString("C", CultureInfo.GetCultureInfo("pt-BR")); string D(string value) => Date(value).ToString("dd/MM/yyyy");
        if (type == "overdue")
        {
            title = "TÍTULOS VENCIDOS E TEMPO DE ATRASO"; headers.AddRange(new[] { "VENCIMENTO", "ATRASO", "TIPO", "PESSOA", "DESCRIÇÃO", "PLANO", "VALOR", "EM ABERTO" });
            foreach (var row in Query(c, """
                SELECT t.*,COALESCE(p.name,'SEM PESSOA') AS partyName,t.amount-COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) AS remaining
                FROM titles t LEFT JOIN parties p ON p.id=t.partyId WHERE t.companyId=@p0 AND t.deleted=0 AND t.cancelled=0 AND t.due<@p1
                AND t.amount>COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) ORDER BY t.due
                """, company, DateTime.Today.ToString("yyyy-MM-dd"))) { var remaining = Number(row, "remaining"); var days = DateOnly.FromDateTime(DateTime.Today).DayNumber - Date((string)row["due"]!).DayNumber; rows.Add(new[] { D((string)row["due"]!), $"{days} DIA(S)", (string)row["kind"]! != "payable" ? "A RECEBER" : "A PAGAR", (string)row["partyName"]!, (string)row["description"]!, (string)row["category"]!, M(Number(row, "amount")), M(remaining) }); total += remaining; }
        }
        else if (type == "party")
        {
            title = "HISTÓRICO FINANCEIRO POR CLIENTE E FORNECEDOR"; headers.AddRange(new[] { "VENCIMENTO", "PESSOA", "TIPO", "DESCRIÇÃO", "PLANO", "VALOR", "EM ABERTO" }); var filter = partyId.HasValue ? " AND t.partyId=@p3" : ""; var values = partyId.HasValue ? new object?[] { company, startText, endExclusive, partyId } : new object?[] { company, startText, endExclusive };
            var partyGroups = new Dictionary<string, List<IReadOnlyList<string>>>(); var partyTotals = new Dictionary<string, long>();
            foreach (var row in Query(c, $"""SELECT t.*,COALESCE(p.name,'SEM PESSOA') AS partyName,t.amount-COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) AS remaining FROM titles t LEFT JOIN parties p ON p.id=t.partyId WHERE t.companyId=@p0 AND t.deleted=0 AND t.due>=@p1 AND t.due<@p2 {filter} ORDER BY partyName,t.due""", values)) { var key = (string)row["partyName"]!; var remaining = Number(row, "remaining"); var line = new[] { D((string)row["due"]!), key, (string)row["kind"]! != "payable" ? "A RECEBER" : "A PAGAR", (string)row["description"]!, (string)row["category"]!, M(Number(row, "amount")), M(remaining) }; rows.Add(line); if (!partyGroups.TryGetValue(key, out var list)) { list = []; partyGroups[key] = list; partyTotals[key] = 0; } list.Add(line); partyTotals[key] += remaining; total += remaining; }
            groups = partyGroups.OrderBy(item => item.Key).Select(item => new GenericReportGroup($"CLIENTE / FORNECEDOR: {item.Key}", item.Value, $"SALDO DO CADASTRO: {M(partyTotals[item.Key])}")).ToList();
        }
        else if (type == "cost-center")
        {
            title = "RECEITAS E DESPESAS POR CENTRO DE CUSTO"; headers.AddRange(new[] { "VENCIMENTO", "CENTRO DE CUSTO", "TIPO", "DESCRIÇÃO", "PLANO", "VALOR", "EM ABERTO" });
            foreach (var row in Query(c, """SELECT t.*,t.amount-COALESCE((SELECT SUM(s.principal) FROM settlements s WHERE s.titleId=t.id AND s.reversed=0),0) AS remaining FROM titles t WHERE t.companyId=@p0 AND t.deleted=0 AND t.due>=@p1 AND t.due<@p2 ORDER BY costCenter,due""", company, startText, endExclusive)) { rows.Add(new[] { D((string)row["due"]!), string.IsNullOrWhiteSpace(row["costCenter"] as string) ? "SEM CENTRO" : (string)row["costCenter"]!, (string)row["kind"]! != "payable" ? "RECEITA" : "DESPESA", (string)row["description"]!, (string)row["category"]!, M(Number(row, "amount")), M(Number(row, "remaining")) }); total += Number(row, "amount"); }
        }
        else if (type == "account")
        {
            title = "EXTRATO POR CONTA FINANCEIRA"; headers.AddRange(new[] { "DATA", "CONTA", "MOVIMENTO", "ENTRADA", "SAÍDA", "SALDO DO MOVIMENTO" }); var filter = accountId.HasValue ? " AND m.accountId=@p3" : ""; var values = accountId.HasValue ? new object?[] { company, startText, endExclusive, accountId } : new object?[] { company, startText, endExclusive };
            var accountGroups = new Dictionary<string, List<IReadOnlyList<string>>>(); var accountTotals = new Dictionary<string, long>();
            foreach (var row in Query(c, $"""SELECT m.*,a.name AS accountName FROM movements m JOIN accounts a ON a.id=m.accountId WHERE m.companyId=@p0 AND m.date>=@p1 AND m.date<@p2 {filter} ORDER BY a.name,m.date,m.id""", values)) { var amount = Number(row, "amount"); var key = (string)row["accountName"]!; var line = new[] { D((string)row["date"]!), key, (string)row["description"]!, amount > 0 ? M(amount) : "-", amount < 0 ? M(-amount) : "-", M(amount) }; rows.Add(line); if (!accountGroups.TryGetValue(key, out var list)) { list = []; accountGroups[key] = list; accountTotals[key] = 0; } list.Add(line); accountTotals[key] += amount; total += amount; }
            groups = accountGroups.OrderBy(item => item.Key).Select(item => new GenericReportGroup($"CONTA FINANCEIRA: {item.Key}", item.Value, $"MOVIMENTO LÍQUIDO: {M(accountTotals[item.Key])}")).ToList();
        }
        else if (type == "cash-flow")
        {
            title = "FLUXO DE CAIXA PREVISTO E REALIZADO"; headers.AddRange(new[] { "DATA", "ORIGEM", "DESCRIÇÃO", "ENTRADA", "SAÍDA", "SITUAÇÃO" });
            foreach (var row in Query(c, """SELECT due AS date,'PREVISTO' AS origin,description,kind,amount FROM titles WHERE companyId=@p0 AND deleted=0 AND cancelled=0 AND due>=@p1 AND due<@p2 UNION ALL SELECT date,'REALIZADO',description,CASE WHEN amount>=0 THEN 'receivable' ELSE 'payable' END,abs(amount) FROM movements WHERE companyId=@p0 AND date>=@p1 AND date<@p2 ORDER BY date""", company, startText, endExclusive)) { var amount = Number(row, "amount"); var incoming = (string)row["kind"]! != "payable"; rows.Add(new[] { D((string)row["date"]!), (string)row["origin"]!, (string)row["description"]!, incoming ? M(amount) : "-", incoming ? "-" : M(amount), (string)row["origin"]! }); total += incoming ? amount : -amount; }
        }
        else throw new ArgumentException("Tipo de relatório avançado inválido.");
        return new((string)companyRow["name"]!, (string)companyRow["cnpj"]!, title, $"{start:dd/MM/yyyy} A {end:dd/MM/yyyy}", headers, rows, $"TOTAL DO RELATÓRIO: {M(total)}", groups);
    }
    public PdfReport AdvancedReport(int company, string type, string start, string end, int? partyId, int? accountId) { lock (gate) { using var c = Open(); Company(c, company); var data = AdvancedData(c, company, type, start, end, partyId, accountId); return new(GenericReportPdf.Generate(data), $"{type}-{start}-a-{end}.pdf"); } }
    public DownloadFile ExportCsv(int company, string type, string start, string end, int? partyId, int? accountId) { lock (gate) { using var c = Open(); Company(c, company); var data = AdvancedData(c, company, type, start, end, partyId, accountId); static string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\""; var lines = new List<string>(); if (data.Groups is { Count: > 0 }) { foreach (var group in data.Groups) { if (lines.Count > 0) lines.Add(""); lines.Add(Escape(group.Name)); lines.Add(string.Join(';', data.Headers.Select(Escape))); lines.AddRange(group.Rows.Select(row => string.Join(';', row.Select(Escape)))); lines.Add(Escape(group.Summary)); } } else { lines.Add(string.Join(';', data.Headers.Select(Escape))); lines.AddRange(data.Rows.Select(row => string.Join(';', row.Select(Escape)))); } lines.Add(""); lines.Add(Escape(data.Summary)); return new(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", lines))).ToArray(), $"{type}-{start}-a-{end}.csv"); } }
    static PayableReportRow ReportRow(Dictionary<string, object?> row, bool receivable)
    {
        var principal = Number(row, "principal"); var interest = Number(row, "interest"); var penalty = Number(row, "penalty"); var discount = Number(row, "discount"); var additional = Number(row, "additional"); var remaining = Number(row, "remaining");
        var status = remaining <= 0 ? (receivable ? "RECEBIDO" : "PAGO") : principal > 0 ? "PARCIAL" : string.CompareOrdinal((string)row["due"]!, DateTime.Today.ToString("yyyy-MM-dd")) < 0 ? "VENCIDO" : "EM ABERTO";
        return new PayableReportRow(Number(row, "id"), (string)row["due"]!, row["paymentDate"] as string, (string)row["supplier"]!, (string)row["documentNumber"]!, (string)row["description"]!, (string)row["category"]!, Number(row, "amount"), principal, remaining, interest, penalty, discount, additional, principal + interest + penalty + additional - discount, status);
    }
}

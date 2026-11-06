using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

sealed partial class Ledger
{
    static void EnsureManualTitle(SqliteConnection c, int titleId)
    {
        if (Query(c, "SELECT id FROM credit_card_invoices WHERE titleId=@p0 LIMIT 1", titleId).Count > 0)
            throw new ArgumentException("Esta fatura é controlada pela tela Cartões de crédito. Altere ou exclua as compras que formam a fatura.");
    }

    static DateOnly CardDay(DateOnly month, int day) => new(month.Year, month.Month, Math.Min(day, DateTime.DaysInMonth(month.Year, month.Month)));

    static (string Reference, DateOnly Closing, DateOnly Due) CardInvoiceDates(DateOnly purchase, int closingDay, int dueDay, int installmentOffset)
    {
        var reference = new DateOnly(purchase.Year, purchase.Month, 1);
        if (purchase.Day > Math.Min(closingDay, DateTime.DaysInMonth(purchase.Year, purchase.Month))) reference = reference.AddMonths(1);
        reference = reference.AddMonths(installmentOffset);
        var closing = CardDay(reference, closingDay);
        var dueMonth = dueDay > closingDay ? reference : reference.AddMonths(1);
        var due = CardDay(dueMonth, dueDay);
        return (reference.ToString("yyyy-MM", CultureInfo.InvariantCulture), closing, due);
    }

    static string CardLastFour(string? value)
    {
        var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length is not (0 or 4)) throw new ArgumentException("Informe exatamente os quatro últimos dígitos do cartão.");
        return digits;
    }

    static DateOnly CreditCardToday() => DateOnly.FromDateTime(DateTime.Today);

    static string CreditCardLabel(Dictionary<string, object?> card)
    {
        var name = (string)card["name"]!;
        var lastFour = (card.GetValueOrDefault("lastFour") as string ?? "").Trim();
        return lastFour.Length == 4 ? $"{name} • FINAL {lastFour}" : name;
    }

    static long EnsureCreditCardInvoice(SqliteConnection c, int company, int cardId, DateOnly cycleDate, int closingDay, int dueDay)
    {
        var dates = CardInvoiceDates(cycleDate, closingDay, dueDay, 0);
        var existing = Query(c, "SELECT id FROM credit_card_invoices WHERE cardId=@p0 AND referenceMonth=@p1", cardId, dates.Reference).SingleOrDefault();
        if (existing is not null) return Number(existing, "id");
        Run(c, "INSERT INTO credit_card_invoices(companyId,cardId,referenceMonth,closingDate,dueDate,titleId,closedAt) VALUES(@p0,@p1,@p2,@p3,@p4,NULL,'')", company, cardId, dates.Reference, dates.Closing.ToString("yyyy-MM-dd"), dates.Due.ToString("yyyy-MM-dd"));
        return Number(Query(c, "SELECT last_insert_rowid() AS id")[0], "id");
    }

    static (string Current, string Next) OpenCreditCardReferences(DateOnly today, int closingDay, int dueDay)
    {
        var current = CardInvoiceDates(today, closingDay, dueDay, 0).Reference;
        var currentDate = DateOnly.ParseExact(current + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return (current, currentDate.AddMonths(1).ToString("yyyy-MM", CultureInfo.InvariantCulture));
    }

    static void EnsureCreditCardInvoiceCalendar(SqliteConnection c, int company, DateOnly today)
    {
        foreach (var card in Query(c, "SELECT * FROM credit_cards WHERE companyId=@p0 AND active=1", company))
        {
            var closingDay = Convert.ToInt32(card["closingDay"]);
            var dueDay = Convert.ToInt32(card["dueDay"]);
            var references = OpenCreditCardReferences(today, closingDay, dueDay);
            var currentCycle = DateOnly.ParseExact(references.Current + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
            EnsureCreditCardInvoice(c, company, Convert.ToInt32(card["id"]), currentCycle, closingDay, dueDay);
            EnsureCreditCardInvoice(c, company, Convert.ToInt32(card["id"]), currentCycle.AddMonths(1), closingDay, dueDay);

            // Recorrências são projeções sem quantidade fixa. Mantemos somente a fatura
            // atual e a próxima; faturas mais distantes pertencem apenas a compras parceladas.
            Run(c, """
                DELETE FROM credit_card_purchases
                WHERE cardId=@p0 AND recurringChargeId IS NOT NULL
                  AND invoiceId IN (
                    SELECT id FROM credit_card_invoices
                    WHERE cardId=@p0 AND companyId=@p1 AND closedAt='' AND referenceMonth>@p2
                  )
                """, card["id"], company, references.Next);
            Run(c, """
                UPDATE titles SET deleted=1
                WHERE id IN (
                    SELECT i.titleId FROM credit_card_invoices i
                    WHERE i.cardId=@p0 AND i.companyId=@p1 AND i.closedAt=''
                      AND i.referenceMonth NOT IN (@p2,@p3) AND i.titleId IS NOT NULL
                      AND NOT EXISTS(SELECT 1 FROM credit_card_purchases p WHERE p.invoiceId=i.id)
                )
                  AND NOT EXISTS(SELECT 1 FROM settlements s WHERE s.titleId=titles.id AND s.reversed=0)
                """, card["id"], company, references.Current, references.Next);
            Run(c, """
                UPDATE credit_card_invoices SET titleId=NULL
                WHERE cardId=@p0 AND companyId=@p1 AND closedAt=''
                  AND referenceMonth NOT IN (@p2,@p3)
                  AND NOT EXISTS(SELECT 1 FROM credit_card_purchases p WHERE p.invoiceId=credit_card_invoices.id)
                  AND (titleId IS NULL OR NOT EXISTS(
                    SELECT 1 FROM settlements s WHERE s.titleId=credit_card_invoices.titleId AND s.reversed=0
                  ))
                """, card["id"], company, references.Current, references.Next);
            Run(c, """
                DELETE FROM credit_card_invoices
                WHERE cardId=@p0 AND companyId=@p1 AND closedAt='' AND titleId IS NULL
                  AND referenceMonth NOT IN (@p2,@p3)
                  AND NOT EXISTS(SELECT 1 FROM credit_card_purchases p WHERE p.invoiceId=credit_card_invoices.id)
                """, card["id"], company, references.Current, references.Next);
        }
    }

    static long CreateCreditCardPayable(SqliteConnection c, int company, Dictionary<string, object?> invoice, long amount)
    {
        var plans = Query(c, "SELECT * FROM plans WHERE companyId=@p0 AND kind='payable' AND name='CARTÃO DE CRÉDITO'", company);
        if (plans.Count == 0)
        {
            Run(c, "INSERT INTO plans(companyId,name,kind) VALUES(@p0,'CARTÃO DE CRÉDITO','payable')", company);
            plans = Query(c, "SELECT * FROM plans WHERE id=last_insert_rowid()");
        }
        var plan = plans.Single();
        var reference = (string)invoice["referenceMonth"]!;
        var invoiceLabel = DateOnly.ParseExact(reference + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("MM/yyyy", CultureInfo.InvariantCulture);
        var cardLabel = CreditCardLabel(invoice);
        Run(c, """
            INSERT INTO titles(companyId,kind,description,amount,due,category,notes,installmentNumber,installmentCount,
            interestRate,interestMode,interestValue,penaltyRate,penaltyMode,penaltyValue,discountRate,discountMode,discountValue,documentNumber,issueDate)
            VALUES(@p0,'payable',@p1,@p2,@p3,'CARTÃO DE CRÉDITO',@p4,1,1,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15)
            """, company, $"FATURA {cardLabel} - {invoiceLabel}", amount, invoice["dueDate"], $"FATURA FECHADA AUTOMATICAMENTE DO CARTÃO {cardLabel}", plan["interestRate"], plan["interestMode"], plan["interestValue"], plan["penaltyRate"], plan["penaltyMode"], plan["penaltyValue"], plan["discountRate"], plan["discountMode"], plan["discountValue"], $"CARTAO-{invoice["cardId"]}-{reference}", invoice["closingDate"]);
        return Number(Query(c, "SELECT last_insert_rowid() AS id")[0], "id");
    }

    static int CloseCreditCardInvoices(SqliteConnection c, int company, DateOnly today)
    {
        EnsureCreditCardInvoiceCalendar(c, company, today);
        var invoices = Query(c, """
            SELECT i.*,cc.name,cc.lastFour,
                COALESCE((SELECT SUM(p.amount) FROM credit_card_purchases p WHERE p.invoiceId=i.id),0) AS total
            FROM credit_card_invoices i JOIN credit_cards cc ON cc.id=i.cardId
            WHERE i.companyId=@p0 AND i.closedAt='' AND i.closingDate<@p1
            ORDER BY i.closingDate,i.id
            """, company, today.ToString("yyyy-MM-dd"));
        var closed = 0;
        foreach (var invoice in invoices)
        {
            var amount = Number(invoice, "total");
            long? titleId = invoice["titleId"] is null ? null : Number(invoice, "titleId");
            if (amount > 0)
            {
                if (titleId.HasValue)
                    Run(c, "UPDATE titles SET amount=@p0,due=@p1,description=@p2,notes=@p3,deleted=0 WHERE id=@p4", amount, invoice["dueDate"], $"FATURA {CreditCardLabel(invoice)} - {DateOnly.ParseExact((string)invoice["referenceMonth"]! + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture):MM/yyyy}", $"FATURA FECHADA AUTOMATICAMENTE DO CARTÃO {CreditCardLabel(invoice)}", titleId.Value);
                else
                {
                    titleId = CreateCreditCardPayable(c, company, invoice, amount);
                    Run(c, "UPDATE credit_card_invoices SET titleId=@p0 WHERE id=@p1", titleId.Value, invoice["id"]);
                }
            }
            else if (titleId.HasValue)
            {
                Run(c, "UPDATE titles SET deleted=1 WHERE id=@p0", titleId.Value);
                Run(c, "UPDATE credit_card_invoices SET titleId=NULL WHERE id=@p0", invoice["id"]);
            }
            Run(c, "UPDATE credit_card_invoices SET closedAt=@p0 WHERE id=@p1 AND closedAt=''", today.ToString("yyyy-MM-dd"), invoice["id"]);
            Audit(c, company, "Fechamento automático de fatura de cartão", new { invoiceId = invoice["id"], cardId = invoice["cardId"], referenceMonth = invoice["referenceMonth"], amount, titleId });
            closed++;
        }
        return closed;
    }

    public object CreditCard(int company, CreditCardInput x) => Write(c =>
    {
        Company(c, company);
        if (x.ClosingDay is < 1 or > 31) throw new ArgumentException("Informe um dia de fechamento entre 1 e 31.");
        if (x.DueDay is < 1 or > 31) throw new ArgumentException("Informe um dia de vencimento entre 1 e 31.");
        if (x.AccountId.HasValue) Existing(c, "accounts", x.AccountId.Value, company);
        var name = Required(x.Name, "Nome do cartão");
        var lastFour = CardLastFour(x.LastFour);
        var startDate = CreditCardToday();
        Run(c, "INSERT INTO credit_cards(companyId,name,closingDay,dueDay,accountId,notes,lastFour,startDate) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)", company, name, x.ClosingDay, x.DueDay, x.AccountId, RequiredOrEmpty(x.Notes), lastFour, startDate.ToString("yyyy-MM-dd"));
        var id = Number(Query(c, "SELECT last_insert_rowid() AS id")[0], "id");
        EnsureCreditCardInvoice(c, company, Convert.ToInt32(id), startDate, x.ClosingDay, x.DueDay);
        if (Query(c, "SELECT id FROM plans WHERE companyId=@p0 AND kind='payable' AND name='CARTÃO DE CRÉDITO'", company).Count == 0)
            Run(c, "INSERT INTO plans(companyId,name,kind) VALUES(@p0,'CARTÃO DE CRÉDITO','payable')", company);
        Audit(c, company, "Cadastro de cartão de crédito", new { id, name, lastFour, x.ClosingDay, x.DueDay, x.AccountId });
        return new { ok = true, id };
    });

    public object EditCreditCard(int company, int cardId, CreditCardInput x) => Write(c =>
    {
        Company(c, company);
        var current = Query(c, "SELECT * FROM credit_cards WHERE id=@p0 AND companyId=@p1 AND active=1", cardId, company).SingleOrDefault()
            ?? throw new ArgumentException("Cartão não encontrado.");
        if (x.ClosingDay is < 1 or > 31) throw new ArgumentException("Informe um dia de fechamento entre 1 e 31.");
        if (x.DueDay is < 1 or > 31) throw new ArgumentException("Informe um dia de vencimento entre 1 e 31.");
        if (x.AccountId.HasValue) Existing(c, "accounts", x.AccountId.Value, company);
        var name = Required(x.Name, "Nome do cartão");
        var lastFour = CardLastFour(x.LastFour);
        if (Query(c, "SELECT id FROM credit_cards WHERE companyId=@p0 AND upper(name)=upper(@p1) AND id<>@p2", company, name, cardId).Count > 0)
            throw new ArgumentException("Já existe outro cartão com este nome.");
        Run(c, "UPDATE credit_cards SET name=@p0,lastFour=@p1,closingDay=@p2,dueDay=@p3,accountId=@p4,notes=@p5 WHERE id=@p6 AND companyId=@p7", name, lastFour, x.ClosingDay, x.DueDay, x.AccountId, RequiredOrEmpty(x.Notes), cardId, company);
        Run(c, "DELETE FROM credit_card_invoices WHERE cardId=@p0 AND closedAt='' AND titleId IS NULL AND NOT EXISTS(SELECT 1 FROM credit_card_purchases p WHERE p.invoiceId=credit_card_invoices.id)", cardId);
        EnsureCreditCardInvoiceCalendar(c, company, CreditCardToday());
        Run(c, """
            UPDATE titles SET
                description='FATURA ' || @p0 || CASE WHEN @p3='' THEN '' ELSE ' • FINAL ' || @p3 END || ' - ' || (SELECT substr(i.referenceMonth,6,2) || '/' || substr(i.referenceMonth,1,4) FROM credit_card_invoices i WHERE i.titleId=titles.id),
                notes='FATURA FECHADA AUTOMATICAMENTE DO CARTÃO ' || @p0 || CASE WHEN @p3='' THEN '' ELSE ' • FINAL ' || @p3 END
            WHERE id IN (SELECT titleId FROM credit_card_invoices WHERE cardId=@p1 AND companyId=@p2)
            """, name, cardId, company, lastFour);
        Audit(c, company, "Edição de cartão de crédito", new { cardId, before = current, after = new { name, lastFour, x.ClosingDay, x.DueDay, x.AccountId, x.Notes } });
        return new { ok = true, id = cardId };
    });

    static (long InvoiceId, long? TitleId, string Reference, string Due, long Amount) AddCreditCardItem(
        SqliteConnection c, int company, int cardId, Dictionary<string, object?> card, DateOnly purchaseDate,
        int monthOffset, string group, string description, string category, string notes, long amount,
        int installmentNumber, int installmentCount, long? recurringChargeId = null)
    {
        var dates = CardInvoiceDates(purchaseDate, Convert.ToInt32(card["closingDay"]), Convert.ToInt32(card["dueDay"]), monthOffset);
        var invoice = Query(c, "SELECT * FROM credit_card_invoices WHERE cardId=@p0 AND referenceMonth=@p1", cardId, dates.Reference).SingleOrDefault();
        long invoiceId;
        long? titleId;
        if (invoice is null)
        {
            Run(c, "INSERT INTO credit_card_invoices(companyId,cardId,referenceMonth,closingDate,dueDate,titleId,closedAt) VALUES(@p0,@p1,@p2,@p3,@p4,NULL,'')", company, cardId, dates.Reference, dates.Closing.ToString("yyyy-MM-dd"), dates.Due.ToString("yyyy-MM-dd"));
            invoiceId = Number(Query(c, "SELECT last_insert_rowid() AS id")[0], "id");
            titleId = null;
        }
        else
        {
            if (!string.IsNullOrEmpty(invoice["closedAt"] as string))
                throw new ArgumentException($"A fatura {dates.Reference} já foi fechada. Esta compra deve entrar em um ciclo posterior.");
            invoiceId = Number(invoice, "id");
            titleId = invoice["titleId"] is null ? null : Number(invoice, "titleId");
        }
        Run(c, "INSERT INTO credit_card_purchases(companyId,cardId,invoiceId,purchaseGroup,description,category,notes,amount,purchaseDate,installmentNumber,installmentCount,recurringChargeId) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11)", company, cardId, invoiceId, group, description, category, notes, amount, purchaseDate.ToString("yyyy-MM-dd"), installmentNumber, installmentCount, recurringChargeId);
        return (invoiceId, titleId, dates.Reference, dates.Due.ToString("yyyy-MM-dd"), amount);
    }

    static int EnsureRecurringCharges(SqliteConnection c, int company, long? onlyId = null)
    {
        var rows = Query(c, "SELECT r.*,cc.name,cc.lastFour,cc.startDate,cc.closingDay,cc.dueDay FROM credit_card_recurring_charges r JOIN credit_cards cc ON cc.id=r.cardId WHERE r.companyId=@p0 AND r.active=1 AND cc.active=1" + (onlyId.HasValue ? " AND r.id=@p1" : ""), onlyId.HasValue ? new object?[] { company, onlyId.Value } : new object?[] { company });
        var today = CreditCardToday();
        var created = 0;
        foreach (var recurrence in rows)
        {
            var recurrenceId = Number(recurrence, "id"); var start = Date((string)recurrence["startDate"]!);
            var references = OpenCreditCardReferences(today, Convert.ToInt32(recurrence["closingDay"]), Convert.ToInt32(recurrence["dueDay"]));
            var currentReference = DateOnly.ParseExact(references.Current + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var lastReference = DateOnly.ParseExact(references.Next + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
            for (var offset = 0; offset < 240; offset++)
            {
                var occurrenceDate = start.AddMonths(offset);
                var dates = CardInvoiceDates(occurrenceDate, Convert.ToInt32(recurrence["closingDay"]), Convert.ToInt32(recurrence["dueDay"]), 0);
                var referenceDate = DateOnly.ParseExact(dates.Reference + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (referenceDate > lastReference) break;
                if (referenceDate < currentReference) continue;
                if (Query(c, "SELECT id FROM credit_card_recurring_skips WHERE recurringChargeId=@p0 AND referenceMonth=@p1 UNION SELECT p.id FROM credit_card_purchases p JOIN credit_card_invoices i ON i.id=p.invoiceId WHERE p.recurringChargeId=@p0 AND i.referenceMonth=@p1 LIMIT 1", recurrenceId, dates.Reference).Count > 0) continue;
                var group = $"REC-{recurrenceId}-{dates.Reference}";
                AddCreditCardItem(c, company, Convert.ToInt32(recurrence["cardId"]), recurrence, occurrenceDate, 0, group, (string)recurrence["description"]!, (string)recurrence["category"]!, (string)recurrence["notes"]!, Number(recurrence, "amount"), 1, 1, recurrenceId);
                created++;
            }
        }
        return created;
    }

    public object CreditCardPurchase(int company, int cardId, CreditCardPurchaseInput x) => Write(c =>
    {
        Company(c, company); Existing(c, "credit_cards", cardId, company); Money(x.Amount, true);
        CloseCreditCardInvoices(c, company, CreditCardToday());
        var mode = (x.Mode ?? "single").Trim().ToLowerInvariant(); if (mode is not ("single" or "installments" or "recurring")) throw new ArgumentException("Escolha compra única, parcelada ou recorrente.");
        var purchaseDate = Date(x.PurchaseDate); var card = Query(c, "SELECT * FROM credit_cards WHERE id=@p0 AND companyId=@p1 AND active=1", cardId, company).SingleOrDefault() ?? throw new ArgumentException("Cartão não encontrado.");
        if (purchaseDate < Date((string)card["startDate"]!)) throw new ArgumentException("A compra não pode ser anterior à data de criação do cartão.");
        var description = Required(x.Description, "Descrição da compra"); var category = string.IsNullOrWhiteSpace(x.Category) ? "CARTÃO DE CRÉDITO" : Required(x.Category, "Plano de contas"); var notes = RequiredOrEmpty(x.Notes);
        if (Query(c, "SELECT id FROM plans WHERE companyId=@p0 AND kind='payable' AND upper(name)=upper(@p1)", company, category).Count == 0) Run(c, "INSERT INTO plans(companyId,name,kind) VALUES(@p0,@p1,'payable')", company, category);
        if (mode == "recurring")
        {
            var today = CreditCardToday(); var firstCurrentMonth = new DateOnly(today.Year, today.Month, 1); if (purchaseDate < firstCurrentMonth) throw new ArgumentException("A recorrência deve começar no mês atual ou em um mês futuro.");
            Run(c, "INSERT INTO credit_card_recurring_charges(companyId,cardId,description,amount,startDate,category,notes) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6)", company, cardId, description, x.Amount, purchaseDate.ToString("yyyy-MM-dd"), category, notes);
            var recurrenceId = Number(Query(c, "SELECT last_insert_rowid() AS id")[0], "id"); var generated = EnsureRecurringCharges(c, company, recurrenceId);
            Audit(c, company, "Recorrência de cartão criada", new { cardId, recurrenceId, input = x, generated }); return new { ok = true, recurrenceId, generated };
        }
        var count = mode == "single" ? 1 : x.Installments; if (count is < 1 or > 120 || x.Amount < count) throw new ArgumentException("Informe entre 1 e 120 parcelas, com ao menos um centavo por parcela.");
        var group = Guid.NewGuid().ToString("N"); var generatedItems = new List<object>();
        for (var index = 0; index < count; index++)
        {
            var amount = x.Amount / count + (index < x.Amount % count ? 1 : 0); var item = AddCreditCardItem(c, company, cardId, card, purchaseDate, index, group, description, category, notes, amount, index + 1, count);
            generatedItems.Add(new { invoiceId = item.InvoiceId, titleId = item.TitleId, referenceMonth = item.Reference, due = item.Due, amount = item.Amount });
        }
        Audit(c, company, "Compra no cartão de crédito", new { cardId, purchaseGroup = group, input = x, generated = generatedItems }); return new { ok = true, purchaseGroup = group, installments = generatedItems };
    });

    static int DeletePurchaseGroupCore(SqliteConnection c, int company, string purchaseGroup, string reason, bool addRecurringSkip)
    {
        var purchases = Query(c, "SELECT p.*,i.titleId,i.referenceMonth,i.closedAt FROM credit_card_purchases p JOIN credit_card_invoices i ON i.id=p.invoiceId WHERE p.companyId=@p0 AND p.purchaseGroup=@p1 ORDER BY p.id", company, purchaseGroup);
        if (purchases.Count == 0) throw new ArgumentException("Compra não encontrada.");
        if (purchases.Any(purchase => !string.IsNullOrEmpty(purchase["closedAt"] as string))) throw new ArgumentException("Esta compra pertence a uma fatura já fechada e não pode mais ser alterada.");
        if (addRecurringSkip && purchases[0]["recurringChargeId"] is not null) Run(c, "INSERT OR IGNORE INTO credit_card_recurring_skips(recurringChargeId,referenceMonth,reason) VALUES(@p0,@p1,@p2)", purchases[0]["recurringChargeId"], purchases[0]["referenceMonth"], reason);
        foreach (var invoiceGroup in purchases.GroupBy(row => Number(row, "invoiceId")))
        {
            var titleId = invoiceGroup.First()["titleId"] is null ? (long?)null : Number(invoiceGroup.First(), "titleId");
            Run(c, "DELETE FROM credit_card_purchases WHERE companyId=@p0 AND purchaseGroup=@p1 AND invoiceId=@p2", company, purchaseGroup, invoiceGroup.Key);
            if (Query(c, "SELECT id FROM credit_card_purchases WHERE invoiceId=@p0 LIMIT 1", invoiceGroup.Key).Count == 0 && titleId.HasValue)
            {
                Run(c, "UPDATE titles SET deleted=1 WHERE id=@p0", titleId.Value);
                Run(c, "UPDATE credit_card_invoices SET titleId=NULL WHERE id=@p0", invoiceGroup.Key);
            }
        }
        return purchases.Count;
    }

    public object DeleteCreditCardPurchase(int company, string purchaseGroup, CreditCardPurchaseDeleteInput x) => Write(c =>
    {
        var reason = Required(x.Reason, "Motivo"); var count = DeletePurchaseGroupCore(c, company, purchaseGroup, reason, true);
        Audit(c, company, "Exclusão de compra do cartão", new { purchaseGroup, reason, installments = count }); return new { ok = true, deletedCount = count };
    });

    public object RefreshCreditCardRecurrences(int company) => Write(c =>
    {
        Company(c, company);
        var generated = EnsureRecurringCharges(c, company);
        var closed = CloseCreditCardInvoices(c, company, CreditCardToday());
        return new { ok = true, generated, closed };
    });

    public object StopCreditCardRecurrence(int company, int recurrenceId, ReasonInput x) => Write(c =>
    {
        Existing(c, "credit_card_recurring_charges", recurrenceId, company); var reason = Required(x.Reason, "Motivo"); var today = CreditCardToday();
        var futureGroups = Query(c, "SELECT DISTINCT p.purchaseGroup FROM credit_card_purchases p JOIN credit_card_invoices i ON i.id=p.invoiceId WHERE p.companyId=@p0 AND p.recurringChargeId=@p1 AND i.closedAt=''", company, recurrenceId).Select(row => (string)row["purchaseGroup"]!).ToList();
        var removed = 0; foreach (var group in futureGroups) { try { removed += DeletePurchaseGroupCore(c, company, group, reason, false); } catch (ArgumentException) { } }
        Run(c, "UPDATE credit_card_recurring_charges SET active=0,endedDate=@p0 WHERE id=@p1", today.ToString("yyyy-MM-dd"), recurrenceId);
        Audit(c, company, "Recorrência de cartão encerrada", new { recurrenceId, reason, removed }); return new { ok = true, removed };
    });
}

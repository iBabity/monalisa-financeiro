using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

record PayableReportRow(
    long TitleId,
    string Due,
    string? PaymentDate,
    string Supplier,
    string DocumentNumber,
    string Description,
    string Plan,
    long Amount,
    long PaidPrincipal,
    long Remaining,
    long Interest,
    long Penalty,
    long Discount,
    long Additional,
    long TotalPaid,
    string Status);

record PayableReportGroup(string Name, IReadOnlyList<PayableReportRow> Rows);

record PayableReportData(
    string Company,
    string Document,
    string Title,
    DateOnly Month,
    string Direction,
    string Type,
    IReadOnlyList<PayableReportGroup> Groups);

static class PayableReportsPdf
{
    const string Teal = "#073F43";
    const string Orange = "#ED791B";
    const string Cream = "#F7F2EA";
    const string Muted = "#58706A";
    static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static byte[] Generate(PayableReportData data)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var rows = data.Groups.SelectMany(group => group.Rows).ToList();
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(24);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(style => style.FontSize(7).FontColor("#243532"));
            page.Header().Element(container => Header(container, data));
            page.Content().PaddingVertical(14).Column(column =>
            {
                column.Spacing(12);
                column.Item().Element(container => Totals(container, rows, data));
                if (data.Groups.Count == 0 || rows.Count == 0)
                {
                    column.Item().Background(Cream).Padding(18).AlignCenter().Text("NENHUM REGISTRO ENCONTRADO PARA ESTE RELATÓRIO.").FontColor(Muted);
                    return;
                }
                foreach (var group in data.Groups)
                {
                    column.Item().PaddingTop(4).Background(Cream).PaddingVertical(6).PaddingHorizontal(8)
                        .Text(group.Name).FontSize(9).Bold().FontColor(Teal);
                    column.Item().Element(container => Table(container, group.Rows, data.Direction));
                    var subtotal = data.Type == "payments"
                        ? $"SUBTOTAL {(data.Direction == "receivable" ? "RECEBIDO" : "PAGO")}: {Money(group.Rows.Sum(row => row.TotalPaid))}"
                        : $"PARCELAS: {Money(group.Rows.Sum(row => row.Amount))}  •  EM ABERTO: {Money(group.Rows.Sum(row => row.Remaining))}";
                    column.Item().AlignRight().Text(subtotal).FontSize(7.5f).Bold().FontColor(Teal);
                }
            });
            page.Footer().BorderTop(1).BorderColor("#D9E1DE").PaddingTop(7).Row(row =>
            {
                row.RelativeItem().Text($"Emitido em {DateTime.Now:dd/MM/yyyy 'às' HH:mm}").FontSize(7).FontColor(Muted);
                row.RelativeItem().AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(7).FontColor(Muted));
                    text.Span("Página "); text.CurrentPageNumber(); text.Span(" de "); text.TotalPages();
                });
            });
        })).GeneratePdf();
    }

    static void Header(IContainer container, PayableReportData data) => container.Column(column =>
    {
        column.Item().Row(row =>
        {
            row.RelativeItem().Column(left =>
            {
                left.Item().Text("monalisa").FontSize(24).ExtraBold().FontColor(Teal);
                left.Item().Text("F I N A N C E I R O").FontSize(8).SemiBold().FontColor(Orange);
            });
            row.RelativeItem(2).AlignRight().Column(right =>
            {
                right.Item().AlignRight().Text(data.Title).FontSize(14).Bold().FontColor(Teal);
                right.Item().AlignRight().Text(data.Type == "future" ? $"A PARTIR DE {data.Month.AddMonths(1).ToString("MMMM 'DE' yyyy", PtBr).ToUpper(PtBr)}" : data.Month.ToString("MMMM 'DE' yyyy", PtBr).ToUpper(PtBr)).FontSize(8.5f).FontColor(Muted);
            });
        });
        column.Item().PaddingTop(10).BorderTop(3).BorderColor(Orange).PaddingTop(7).Row(row =>
        {
            row.RelativeItem().Text(data.Company).Bold().FontColor(Teal);
            row.RelativeItem().AlignRight().Text(CompanyDocument(data.Document)).FontColor(Muted);
        });
    });

    static void Totals(IContainer container, IReadOnlyList<PayableReportRow> rows, PayableReportData data) => container.Row(row =>
    {
        var installmentTotal = data.Type == "payments"
            ? rows.GroupBy(item => item.TitleId).Sum(group => group.First().Amount)
            : rows.Sum(item => item.Amount);
        TotalCard(row.RelativeItem(), "TOTAL DAS PARCELAS", installmentTotal, Teal);
        row.ConstantItem(7);
        TotalCard(row.RelativeItem(), data.Direction == "receivable" ? "PRINCIPAL RECEBIDO" : "PRINCIPAL PAGO", rows.Sum(item => item.PaidPrincipal), Teal);
        row.ConstantItem(7);
        TotalCard(row.RelativeItem(), "JUROS + MULTA + ACRÉSC.", rows.Sum(item => item.Interest + item.Penalty + item.Additional), Orange);
        row.ConstantItem(7);
        TotalCard(row.RelativeItem(), "DESCONTOS", rows.Sum(item => item.Discount), "#267458");
        row.ConstantItem(7);
        TotalCard(row.RelativeItem(), "SALDO RESTANTE", rows.Sum(item => item.Remaining), Orange);
        row.ConstantItem(7);
        TotalCard(row.RelativeItem(), data.Direction == "receivable" ? "TOTAL RECEBIDO" : "TOTAL PAGO", rows.Sum(item => item.TotalPaid), Teal);
    });

    static void TotalCard(IContainer container, string label, long value, string color) => container
        .Border(1).BorderColor("#DCE4E1").Background("#FBFCFB").Padding(8).Column(column =>
        {
            column.Item().Text(label).FontSize(6).SemiBold().FontColor(Muted);
            column.Item().PaddingTop(3).Text(Money(value)).FontSize(10).Bold().FontColor(color);
        });

    static void Table(IContainer container, IReadOnlyList<PayableReportRow> rows, string direction) => container.Table(table =>
    {
        table.ColumnsDefinition(columns =>
        {
            columns.ConstantColumn(45); columns.ConstantColumn(45);
            columns.RelativeColumn(1.25f); columns.ConstantColumn(47); columns.RelativeColumn(1.35f); columns.RelativeColumn(1.05f);
            columns.ConstantColumn(55); columns.ConstantColumn(55); columns.ConstantColumn(44);
            columns.ConstantColumn(44); columns.ConstantColumn(44); columns.ConstantColumn(44); columns.ConstantColumn(55);
            columns.ConstantColumn(55); columns.ConstantColumn(45);
        });
        table.Header(header =>
        {
            Header(header, "VENC."); Header(header, direction == "receivable" ? "REC." : "PAG."); Header(header, direction == "receivable" ? "CLIENTE / ORIGEM" : "FORNECEDOR"); Header(header, "DOC."); Header(header, "DESCRIÇÃO"); Header(header, "PLANO");
            Header(header, "PARCELA", true); Header(header, "PRINCIPAL", true); Header(header, "JUROS", true); Header(header, "MULTA", true);
            Header(header, "DESC.", true); Header(header, "ACRÉSC.", true); Header(header, "RESTANTE", true); Header(header, direction == "receivable" ? "TOTAL REC." : "TOTAL PAGO", true); Header(header, "SITUAÇÃO");
        });
        foreach (var item in rows)
        {
            Cell(table, Date(item.Due)); Cell(table, item.PaymentDate is null ? "—" : Date(item.PaymentDate));
            Cell(table, item.Supplier); Cell(table, string.IsNullOrWhiteSpace(item.DocumentNumber) ? "—" : item.DocumentNumber); Cell(table, item.Description); Cell(table, item.Plan);
            Cell(table, Money(item.Amount), true); Cell(table, Money(item.PaidPrincipal), true);
            Cell(table, Money(item.Interest), true, item.Interest > 0 ? Orange : null);
            Cell(table, Money(item.Penalty), true, item.Penalty > 0 ? Orange : null);
            Cell(table, Money(item.Discount), true, item.Discount > 0 ? "#267458" : null);
            Cell(table, Money(item.Additional), true, item.Additional > 0 ? Orange : null);
            Cell(table, Money(item.Remaining), true, item.Remaining > 0 ? Orange : Teal);
            Cell(table, Money(item.TotalPaid), true, Teal); Cell(table, item.Status);
        }
    });

    static void Header(TableCellDescriptor table, string text, bool right = false)
    {
        var cell = table.Cell().Background(Teal).PaddingVertical(5).PaddingHorizontal(3);
        (right ? cell.AlignRight() : cell).Text(text).FontSize(5.5f).SemiBold().FontColor(Colors.White);
    }

    static void Cell(TableDescriptor table, string text, bool right = false, string? color = null)
    {
        var cell = table.Cell().BorderBottom(.5f).BorderColor("#E4E9E7").PaddingVertical(5).PaddingHorizontal(3);
        (right ? cell.AlignRight() : cell).Text(text).FontSize(5.8f).FontColor(color ?? "#243532");
    }

    static string Money(long cents) => (cents / 100m).ToString("C", PtBr);
    static string CompanyDocument(string value) => value.Length == 14
        ? $"{value[..2]}.{value[2..5]}.{value[5..8]}/{value[8..12]}-{value[12..]}"
        : string.IsNullOrWhiteSpace(value) ? "SEM CNPJ INFORMADO" : value;
    static string Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd/MM/yyyy");
}

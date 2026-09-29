using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

record MonthlyReportLine(
    string Date,
    string Type,
    string Description,
    string Detail,
    long Amount,
    long Remaining = 0,
    string Status = "",
    long Principal = 0,
    long Interest = 0,
    long Penalty = 0,
    long Discount = 0,
    long Additional = 0);

record MonthlyReportData(
    string Company,
    string Document,
    DateOnly Start,
    DateOnly End,
    long PayableScheduled,
    long ReceivableScheduled,
    long PayableOpen,
    long ReceivableOpen,
    long Paid,
    long Received,
    IReadOnlyList<MonthlyReportLine> Settlements,
    IReadOnlyList<MonthlyReportLine> Titles);

record PdfReport(byte[] Content, string FileName);

static class MonthlyReportPdf
{
    const string Teal = "#073F43";
    const string Orange = "#ED791B";
    const string Cream = "#F7F2EA";
    const string Muted = "#58706A";
    static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static byte[] Generate(MonthlyReportData data)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(style => style.FontSize(8.5f).FontColor("#243532"));

                page.Header().Element(container => Header(container, data));
                page.Content().PaddingVertical(18).Column(column =>
                {
                    column.Spacing(16);
                    column.Item().Element(container => Summary(container, data));
                    column.Item().Element(container => SectionTitle(container, "BAIXAS REALIZADAS NO PERÍODO", "Valores que realmente movimentaram as contas financeiras"));
                    column.Item().Element(container => SettlementTable(container, data.Settlements));
                    column.Item().Element(container => SectionTitle(container, "LANÇAMENTOS COM VENCIMENTO NO PERÍODO", "Contas previstas, pagas, recebidas, vencidas ou ainda em aberto"));
                    column.Item().Element(container => TitleTable(container, data.Titles));
                    column.Item().PaddingTop(4).Background(Cream).Padding(10).Text(
                        "O resultado do período considera somente as baixas não estornadas. Contas em aberto aparecem na relação de lançamentos e não alteram o resultado realizado.")
                        .FontSize(8).FontColor(Muted);
                });
                page.Footer().BorderTop(1).BorderColor("#D9E1DE").PaddingTop(8).Row(row =>
                {
                    row.RelativeItem().Text($"Emitido em {DateTime.Now:dd/MM/yyyy 'às' HH:mm}").FontSize(7.5f).FontColor(Muted);
                    row.RelativeItem().AlignRight()
                        .DefaultTextStyle(style => style.FontSize(7.5f).FontColor(Muted))
                        .Text(text =>
                        {
                            text.Span("Página ");
                            text.CurrentPageNumber();
                            text.Span(" de ");
                            text.TotalPages();
                        });
                });
            });
        }).GeneratePdf();
    }

    static void Header(IContainer container, MonthlyReportData data) => container.Column(column =>
    {
        column.Item().Row(row =>
        {
            row.RelativeItem().Column(left =>
            {
                left.Item().Text("monalisa").FontSize(25).ExtraBold().FontColor(Teal);
                left.Item().Text("F I N A N C E I R O").FontSize(8).SemiBold().FontColor(Orange);
            });
            row.RelativeItem(1.5f).AlignRight().Column(right =>
            {
                right.Item().AlignRight().Text("RELATÓRIO FINANCEIRO").FontSize(14).Bold().FontColor(Teal);
                right.Item().AlignRight().Text(Period(data)).FontSize(9).FontColor(Muted);
            });
        });
        column.Item().PaddingTop(12).BorderTop(3).BorderColor(Orange).PaddingTop(9).Row(row =>
        {
            row.RelativeItem().Text(data.Company).Bold().FontColor(Teal);
            row.RelativeItem().AlignRight().Text(CompanyDocument(data.Document)).FontColor(Muted);
        });
    });

    static void Summary(IContainer container, MonthlyReportData data)
    {
        var result = data.Received - data.Paid;
        container.Column(column =>
        {
            column.Item().Text("RESUMO DO PERÍODO").FontSize(11).Bold().FontColor(Teal);
            column.Item().PaddingTop(7).Row(row =>
            {
                SummaryCard(row.RelativeItem(), "RECEBIDO", data.Received, Teal, "Baixas de contas a receber");
                row.ConstantItem(8);
                SummaryCard(row.RelativeItem(), "PAGO", data.Paid, Orange, "Baixas de contas a pagar");
                row.ConstantItem(8);
                SummaryCard(row.RelativeItem(), "RESULTADO", result, result < 0 ? "#B23A3A" : Teal, "Recebido menos pago");
            });
            column.Item().PaddingTop(8).Row(row =>
            {
                SummaryCard(row.RelativeItem(), "A PAGAR EM ABERTO", data.PayableOpen, Orange, $"Previsto no período: {Money(data.PayableScheduled)}");
                row.ConstantItem(8);
                SummaryCard(row.RelativeItem(), "A RECEBER EM ABERTO", data.ReceivableOpen, Teal, $"Previsto no período: {Money(data.ReceivableScheduled)}");
            });
        });
    }

    static void SummaryCard(IContainer container, string label, long value, string color, string note) => container
        .Border(1).BorderColor("#DCE4E1").Background("#FBFCFB").Padding(11).Column(column =>
        {
            column.Item().Text(label).FontSize(7).SemiBold().FontColor(Muted);
            column.Item().PaddingTop(5).Text(Money(value)).FontSize(15).Bold().FontColor(color);
            column.Item().PaddingTop(3).Text(note).FontSize(6.8f).FontColor(Muted);
        });

    static void SectionTitle(IContainer container, string title, string subtitle) => container.Column(column =>
    {
        column.Item().Text(title).FontSize(10.5f).Bold().FontColor(Teal);
        column.Item().PaddingTop(2).Text(subtitle).FontSize(7.5f).FontColor(Muted);
    });

    static void SettlementTable(IContainer container, IReadOnlyList<MonthlyReportLine> lines)
    {
        if (lines.Count == 0)
        {
            container.Background(Cream).Padding(14).AlignCenter().Text("NENHUMA BAIXA REALIZADA NESTE PERÍODO.").FontColor(Muted);
            return;
        }
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(52);
                columns.ConstantColumn(65);
                columns.RelativeColumn(2.3f);
                columns.RelativeColumn(1f);
                columns.ConstantColumn(60);
                columns.ConstantColumn(50);
                columns.ConstantColumn(48);
                columns.ConstantColumn(60);
            });
            table.Header(header =>
            {
                HeaderCell(header, "DATA"); HeaderCell(header, "TIPO"); HeaderCell(header, "DESCRIÇÃO"); HeaderCell(header, "CONTA");
                HeaderCell(header, "PRINCIPAL", true); HeaderCell(header, "ACRÉSCIMOS", true); HeaderCell(header, "DESCONTO", true); HeaderCell(header, "TOTAL", true);
            });
            foreach (var line in lines)
            {
                BodyCell(table, line.Date);
                BodyCell(table, line.Type);
                BodyCell(table, line.Description);
                BodyCell(table, line.Detail);
                BodyCell(table, Money(line.Principal), true);
                BodyCell(table, Money(line.Interest + line.Penalty + line.Additional), true, line.Interest + line.Penalty + line.Additional > 0 ? Orange : null);
                BodyCell(table, Money(line.Discount), true, line.Discount > 0 ? "#267458" : null);
                BodyCell(table, Money(line.Amount), true, line.Type == "RECEBIMENTO" ? Teal : Orange);
            }
        });
    }

    static void TitleTable(IContainer container, IReadOnlyList<MonthlyReportLine> lines)
    {
        if (lines.Count == 0)
        {
            container.Background(Cream).Padding(14).AlignCenter().Text("NENHUM LANÇAMENTO COM VENCIMENTO NESTE PERÍODO.").FontColor(Muted);
            return;
        }
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(52);
                columns.ConstantColumn(52);
                columns.RelativeColumn(2.5f);
                columns.RelativeColumn(1.3f);
                columns.ConstantColumn(58);
                columns.ConstantColumn(70);
                columns.ConstantColumn(70);
            });
            table.Header(header =>
            {
                HeaderCell(header, "VENC."); HeaderCell(header, "TIPO"); HeaderCell(header, "DESCRIÇÃO"); HeaderCell(header, "PLANO"); HeaderCell(header, "SITUAÇÃO"); HeaderCell(header, "VALOR", true); HeaderCell(header, "EM ABERTO", true);
            });
            foreach (var line in lines)
            {
                BodyCell(table, line.Date);
                BodyCell(table, line.Type);
                BodyCell(table, line.Description);
                BodyCell(table, line.Detail);
                BodyCell(table, line.Status);
                BodyCell(table, Money(line.Amount), true);
                BodyCell(table, Money(line.Remaining), true, line.Remaining > 0 ? Orange : Teal);
            }
        });
    }

    static void HeaderCell(TableCellDescriptor table, string text, bool right = false)
    {
        var cell = table.Cell().Background(Teal).PaddingVertical(6).PaddingHorizontal(5);
        (right ? cell.AlignRight() : cell).Text(text).FontSize(6.8f).SemiBold().FontColor(Colors.White);
    }

    static void BodyCell(TableDescriptor table, string text, bool right = false, string? color = null)
    {
        var cell = table.Cell().BorderBottom(0.6f).BorderColor("#E4E9E7").PaddingVertical(6).PaddingHorizontal(5);
        (right ? cell.AlignRight() : cell).Text(text).FontSize(7.2f).FontColor(color ?? "#243532");
    }

    static string Money(long cents) => (cents / 100m).ToString("C", PtBr);
    static string CompanyDocument(string value) => value.Length == 14
        ? $"{value[..2]}.{value[2..5]}.{value[5..8]}/{value[8..12]}-{value[12..]}"
        : string.IsNullOrWhiteSpace(value) ? "SEM CNPJ INFORMADO" : value;
    static string Period(MonthlyReportData data) => data.Start == data.End
        ? data.Start.ToString("dd 'DE' MMMM 'DE' yyyy", PtBr).ToUpper(PtBr)
        : $"{data.Start:dd/MM/yyyy} A {data.End:dd/MM/yyyy}";
}

using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

record GenericReportGroup(string Name,IReadOnlyList<IReadOnlyList<string>> Rows,string Summary);
record GenericReportData(string Company,string Document,string Title,string Period,IReadOnlyList<string> Headers,IReadOnlyList<IReadOnlyList<string>> Rows,string Summary,IReadOnlyList<GenericReportGroup>? Groups=null);

static class GenericReportPdf
{
    const string Teal="#073F43";
    const string Orange="#ED791B";
    const string Cream="#F7F2EA";
    const string Muted="#58706A";

    public static byte[] Generate(GenericReportData data)
    {
        QuestPDF.Settings.License=LicenseType.Community;
        return Document.Create(document=>document.Page(page=>{
            page.Size(PageSizes.A4.Landscape());page.Margin(26);page.PageColor(Colors.White);page.DefaultTextStyle(s=>s.FontSize(8).FontColor("#243532"));
            page.Header().Column(column=>{
                column.Item().Row(row=>{row.RelativeItem().Column(left=>{left.Item().Text("monalisa").FontSize(24).ExtraBold().FontColor(Teal);left.Item().Text("F I N A N C E I R O").FontSize(8).SemiBold().FontColor(Orange);});row.RelativeItem(2).AlignRight().Column(right=>{right.Item().AlignRight().Text(data.Title).FontSize(14).Bold().FontColor(Teal);right.Item().AlignRight().Text(data.Period).FontSize(8).FontColor(Muted);});});
                column.Item().PaddingTop(10).BorderTop(3).BorderColor(Orange).PaddingTop(7).Row(row=>{row.RelativeItem().Text(data.Company).Bold().FontColor(Teal);row.RelativeItem().AlignRight().Text(CompanyDocument(data.Document)).FontColor(Muted);});
            });
            page.Content().PaddingVertical(16).Column(column=>{
                if(data.Rows.Count==0){column.Item().Background(Cream).Padding(18).AlignCenter().Text("NENHUM REGISTRO ENCONTRADO PARA ESTE RELATÓRIO.").FontColor(Muted);return;}
                if(data.Groups is {Count:>0}) {
                    foreach(var group in data.Groups) {
                        column.Item().PaddingTop(8).PaddingBottom(5).Row(row=>{
                            row.RelativeItem().Text(group.Name).FontSize(10).Bold().FontColor(Teal);
                            row.RelativeItem().AlignRight().Text(group.Summary).FontSize(8).SemiBold().FontColor(Muted);
                        });
                        ReportTable(column,data.Headers,group.Rows);
                    }
                } else ReportTable(column,data.Headers,data.Rows);
                column.Item().PaddingTop(12).AlignRight().Text(data.Summary).Bold().FontColor(Teal);
            });
            page.Footer().BorderTop(1).BorderColor("#D9E1DE").PaddingTop(7).Row(row=>{row.RelativeItem().Text($"Emitido em {DateTime.Now:dd/MM/yyyy 'às' HH:mm}").FontSize(7).FontColor(Muted);row.RelativeItem().AlignRight().Text(text=>{text.DefaultTextStyle(style=>style.FontSize(7).FontColor(Muted));text.Span("Página ");text.CurrentPageNumber();text.Span(" de ");text.TotalPages();});});
        })).GeneratePdf();
    }

    static void ReportTable(ColumnDescriptor column,IReadOnlyList<string> headers,IReadOnlyList<IReadOnlyList<string>> rows)
    {
        column.Item().Table(table=>{
            table.ColumnsDefinition(columns=>{for(var i=0;i<headers.Count;i++)columns.RelativeColumn(i==0?0.8f:1.2f);});
            table.Header(header=>{foreach(var value in headers)header.Cell().Background(Teal).Padding(6).Text(value).FontSize(7).SemiBold().FontColor(Colors.White);});
            foreach(var row in rows)foreach(var value in row)table.Cell().BorderBottom(.5f).BorderColor("#E4E9E7").Padding(6).Text(value).FontSize(7);
        });
    }

    static string CompanyDocument(string value)=>value.Length==14?$"{value[..2]}.{value[2..5]}.{value[5..8]}/{value[8..12]}-{value[12..]}":value.Length==11?$"{value[..3]}.{value[3..6]}.{value[6..9]}-{value[9..]}":value;
}

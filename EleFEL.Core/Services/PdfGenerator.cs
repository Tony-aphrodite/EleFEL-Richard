using System.Xml.Linq;
using EleFEL.Core.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EleFEL.Core.Services;

/// <summary>
/// Generates PDF documents for certified invoices using QuestPDF
/// </summary>
public static class PdfGenerator
{
    static PdfGenerator()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] GenerateInvoicePdf(Invoice invoice, string xmlContent)
    {
        var items = ParseItemsFromXml(xmlContent);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Element(c => ComposeHeader(c, invoice));
                page.Content().Element(c => ComposeContent(c, invoice, items));
                page.Footer().Element(c => ComposeFooter(c, invoice));
            });
        });

        return document.GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, Invoice invoice)
    {
        container.Column(col =>
        {
            col.Item().Text("DOCUMENTO TRIBUTARIO ELECTRONICO").Bold().FontSize(12).AlignCenter();
            col.Item().Text("FACTURA ELECTRONICA - FEL").FontSize(10).AlignCenter();
            col.Item().PaddingTop(5).LineHorizontal(1);
            col.Item().PaddingTop(5).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text($"No. Autorización (UUID):").Bold();
                    c.Item().Text(invoice.Uuid ?? "Pendiente");
                    c.Item().Text($"Serie: {invoice.SerialNumber ?? "-"}  Número: {invoice.DteNumber ?? "-"}");
                });
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text($"Fecha de Certificación:").Bold().AlignRight();
                    c.Item().Text(invoice.CertificationDate?.ToString("dd/MM/yyyy HH:mm:ss") ?? "-").AlignRight();
                });
            });
            col.Item().PaddingTop(5).LineHorizontal(1);

            col.Item().PaddingTop(5).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("RECEPTOR:").Bold();
                    c.Item().Text($"NIT: {invoice.CustomerNit}");
                    c.Item().Text($"Nombre: {invoice.CustomerName}");
                });
            });
            col.Item().PaddingTop(5).LineHorizontal(1);
        });
    }

    private static void ComposeContent(IContainer container, Invoice invoice, List<PdfLineItem> items)
    {
        container.PaddingTop(10).Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.ConstantColumn(30);   // #
                cols.RelativeColumn(3);     // Description
                cols.ConstantColumn(50);    // Qty
                cols.ConstantColumn(70);    // Unit Price
                cols.ConstantColumn(70);    // Total
            });

            table.Header(header =>
            {
                header.Cell().Text("#").Bold();
                header.Cell().Text("Descripción").Bold();
                header.Cell().Text("Cant.").Bold().AlignRight();
                header.Cell().Text("P. Unit.").Bold().AlignRight();
                header.Cell().Text("Total").Bold().AlignRight();

                header.Cell().ColumnSpan(5).PaddingTop(3).LineHorizontal(0.5f);
            });

            int lineNum = 1;
            foreach (var item in items)
            {
                table.Cell().Text(lineNum.ToString());
                table.Cell().Text(item.Description);
                table.Cell().Text(item.Quantity).AlignRight();
                table.Cell().Text(item.UnitPrice).AlignRight();
                table.Cell().Text(item.Total).AlignRight();
                lineNum++;
            }

            table.Cell().ColumnSpan(5).PaddingTop(3).LineHorizontal(0.5f);

            // Total row
            table.Cell().ColumnSpan(4).Text("TOTAL:").Bold().AlignRight();
            table.Cell().Text($"Q {invoice.Total:N2}").Bold().AlignRight();
        });
    }

    private static void ComposeFooter(IContainer container, Invoice invoice)
    {
        container.Column(col =>
        {
            col.Item().LineHorizontal(1);
            col.Item().PaddingTop(5).Text("Certificador: Infile, S.A. - NIT: 12521337").FontSize(7).AlignCenter();
            col.Item().Text("Documento Tributario Electrónico generado por EleFEL").FontSize(7).AlignCenter();
            col.Item().Text($"Consulte este documento en: https://report.feel.com.gt/ingfacerelofficontroller/ingaboraboringreso.jsp?uuid={invoice.Uuid}")
                .FontSize(6).AlignCenter();
        });
    }

    private static List<PdfLineItem> ParseItemsFromXml(string xmlContent)
    {
        var items = new List<PdfLineItem>();
        try
        {
            var doc = XDocument.Parse(xmlContent);
            var itemElements = doc.Descendants().Where(e => e.Name.LocalName == "Item");

            foreach (var item in itemElements)
            {
                items.Add(new PdfLineItem
                {
                    Description = item.Descendants().FirstOrDefault(e => e.Name.LocalName == "Descripcion")?.Value ?? "",
                    Quantity = item.Descendants().FirstOrDefault(e => e.Name.LocalName == "Cantidad")?.Value ?? "0",
                    UnitPrice = item.Descendants().FirstOrDefault(e => e.Name.LocalName == "PrecioUnitario")?.Value ?? "0",
                    Total = item.Descendants().FirstOrDefault(e => e.Name.LocalName == "Total")?.Value ?? "0"
                });
            }
        }
        catch
        {
            // Fallback: empty items list
        }
        return items;
    }

    private class PdfLineItem
    {
        public string Description { get; set; } = "";
        public string Quantity { get; set; } = "";
        public string UnitPrice { get; set; } = "";
        public string Total { get; set; } = "";
    }
}

using EleFEL.Core.Models;

namespace EleFEL.Core.Services;

/// <summary>
/// Manages saving XML and PDF invoice files organized by date
/// </summary>
public class InvoiceFileService
{
    private readonly string _baseDirectory;
    private readonly LogService _log;
    private readonly EmitterConfig? _emitter;

    public InvoiceFileService(string invoiceDirectory, LogService log, EmitterConfig? emitter = null)
    {
        _baseDirectory = invoiceDirectory;
        _log = log;
        _emitter = emitter;
        Directory.CreateDirectory(_baseDirectory);
    }

    /// <summary>
    /// Saves the certified XML to a date-organized folder
    /// </summary>
    public async Task<string> SaveXmlAsync(Invoice invoice, string xmlContent)
    {
        var folder = GetDateFolder(invoice.CertificationDate ?? DateTime.Now);
        var fileName = $"DTE_{invoice.EleventaSaleId}_{invoice.Uuid ?? "pending"}.xml";
        var filePath = Path.Combine(folder, fileName);

        await File.WriteAllTextAsync(filePath, xmlContent);
        _log.LogInfo($"XML saved: {filePath}");
        return filePath;
    }

    /// <summary>
    /// Generates a PDF from the certified invoice and saves it
    /// </summary>
    public async Task<string> GenerateAndSavePdfAsync(Invoice invoice, string xmlContent)
    {
        var folder = GetDateFolder(invoice.CertificationDate ?? DateTime.Now);
        var fileName = $"DTE_{invoice.EleventaSaleId}_{invoice.Uuid ?? "pending"}.pdf";
        var filePath = Path.Combine(folder, fileName);

        var pdfBytes = PdfGenerator.GenerateInvoicePdf(invoice, xmlContent, _emitter);
        await File.WriteAllBytesAsync(filePath, pdfBytes);
        _log.LogInfo($"PDF saved: {filePath}");
        return filePath;
    }

    private string GetDateFolder(DateTime date)
    {
        var folder = Path.Combine(_baseDirectory, date.ToString("yyyy"), date.ToString("MM"), date.ToString("dd"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}

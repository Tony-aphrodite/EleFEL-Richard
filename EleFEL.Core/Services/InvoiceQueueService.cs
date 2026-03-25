using EleFEL.Core.Interfaces;
using EleFEL.Core.Models;

namespace EleFEL.Core.Services;

/// <summary>
/// Manages the offline queue for pending invoices.
/// Automatically retries certification when internet is restored.
/// </summary>
public class InvoiceQueueService
{
    private readonly LocalDatabaseService _db;
    private readonly IFelCertifier _certifier;
    private readonly InvoiceFileService _fileService;
    private readonly LogService _log;
    private readonly int _maxRetries;

    public event Action<int>? OnPendingCountChanged;
    public event Action<Invoice>? OnInvoiceCertified;
    public event Action<Invoice, string>? OnInvoiceFailed;

    public InvoiceQueueService(
        LocalDatabaseService db,
        IFelCertifier certifier,
        InvoiceFileService fileService,
        LogService log,
        int maxRetries = 10)
    {
        _db = db;
        _certifier = certifier;
        _fileService = fileService;
        _log = log;
        _maxRetries = maxRetries;
    }

    /// <summary>
    /// Processes all pending invoices in the queue
    /// </summary>
    public async Task ProcessPendingAsync()
    {
        var pending = await _db.GetPendingInvoicesAsync();
        if (pending.Count == 0) return;

        _log.LogInfo($"Processing {pending.Count} pending invoice(s)");

        foreach (var invoice in pending)
        {
            if (invoice.RetryCount >= _maxRetries)
            {
                _log.LogError($"Invoice SaleID={invoice.EleventaSaleId} exceeded max retries ({_maxRetries})");
                continue;
            }

            await ProcessSingleInvoiceAsync(invoice);
        }

        var remainingCount = await _db.GetPendingCountAsync();
        OnPendingCountChanged?.Invoke(remainingCount);
    }

    /// <summary>
    /// Certifies a single invoice through the FEL certifier
    /// </summary>
    public async Task ProcessSingleInvoiceAsync(Invoice invoice)
    {
        if (string.IsNullOrEmpty(invoice.XmlContent))
        {
            _log.LogError($"Invoice SaleID={invoice.EleventaSaleId} has no XML content");
            return;
        }

        invoice.Status = InvoiceStatus.Sending;
        await _db.UpdateInvoiceAsync(invoice);

        // Increment retry count AFTER status update succeeds
        invoice.RetryCount++;
        await _db.UpdateInvoiceAsync(invoice);

        _log.LogRetry(invoice.EleventaSaleId, invoice.RetryCount, _maxRetries);

        try
        {
            var result = await _certifier.CertifyAsync(invoice.XmlContent);

            if (result.Success)
            {
                invoice.Status = InvoiceStatus.Certified;
                invoice.Uuid = result.Uuid;
                invoice.AuthorizationNumber = result.AuthorizationNumber;
                invoice.SerialNumber = result.SerialNumber;
                invoice.DteNumber = result.DteNumber;
                invoice.CertificationDate = result.CertificationDate;

                // Save certified XML and generate PDF
                if (result.CertifiedXml != null)
                {
                    invoice.XmlFilePath = await _fileService.SaveXmlAsync(invoice, result.CertifiedXml);
                    invoice.PdfFilePath = await _fileService.GenerateAndSavePdfAsync(invoice, result.CertifiedXml);
                }

                invoice.ErrorMessage = null;
                await _db.UpdateInvoiceAsync(invoice);

                _log.LogInvoiceCertified(invoice.EleventaSaleId, result.Uuid ?? "N/A");
                OnInvoiceCertified?.Invoke(invoice);
            }
            else
            {
                invoice.Status = InvoiceStatus.Error;
                invoice.ErrorMessage = result.ErrorMessage;
                await _db.UpdateInvoiceAsync(invoice);

                _log.LogError($"Certification failed for SaleID={invoice.EleventaSaleId}: {result.ErrorMessage}");
                OnInvoiceFailed?.Invoke(invoice, result.ErrorMessage ?? "Unknown error");
            }
        }
        catch (Exception ex)
        {
            // Recover from Sending state to Error so it can be retried
            invoice.Status = InvoiceStatus.Error;
            invoice.ErrorMessage = $"Exception: {ex.Message}";
            await _db.UpdateInvoiceAsync(invoice);
            _log.LogError($"Exception during certification for SaleID={invoice.EleventaSaleId}", ex);
        }
    }
}

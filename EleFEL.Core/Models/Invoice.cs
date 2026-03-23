namespace EleFEL.Core.Models;

/// <summary>
/// Represents an electronic invoice (DTE) and its certification status
/// </summary>
public class Invoice
{
    public int Id { get; set; }
    public long EleventaSaleId { get; set; }
    public string CustomerNit { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Pending;
    public string? Uuid { get; set; }
    public string? AuthorizationNumber { get; set; }
    public string? SerialNumber { get; set; }
    public string? DteNumber { get; set; }
    public DateTime? CertificationDate { get; set; }
    public string? XmlContent { get; set; }
    public string? XmlFilePath { get; set; }
    public string? PdfFilePath { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public enum InvoiceStatus
{
    Pending = 0,
    Sending = 1,
    Certified = 2,
    Error = 3,
    Cancelled = 4,
    Postponed = 5
}

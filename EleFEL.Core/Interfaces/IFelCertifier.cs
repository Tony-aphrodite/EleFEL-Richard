using EleFEL.Core.Models;

namespace EleFEL.Core.Interfaces;

/// <summary>
/// Abstraction layer for FEL certifiers.
/// MVP implements Infile; future certifiers (Digifact, Megaprint)
/// can be added by implementing this interface.
/// </summary>
public interface IFelCertifier
{
    string CertifierName { get; }

    /// <summary>
    /// Sends the signed XML DTE to the certifier and returns the certification result
    /// </summary>
    Task<CertificationResult> CertifyAsync(string xmlContent);

    /// <summary>
    /// Cancels a previously certified DTE
    /// </summary>
    Task<CertificationResult> CancelAsync(string uuid, string reason);
}

public class CertificationResult
{
    public bool Success { get; set; }
    public string? Uuid { get; set; }
    public string? AuthorizationNumber { get; set; }
    public string? SerialNumber { get; set; }
    public string? DteNumber { get; set; }
    public DateTime? CertificationDate { get; set; }
    public string? CertifiedXml { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }
}

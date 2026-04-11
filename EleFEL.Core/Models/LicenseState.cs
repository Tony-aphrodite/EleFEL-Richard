namespace EleFEL.Core.Models;

/// <summary>
/// Persisted license state. Stored in license.json next to config.json.
/// </summary>
public class LicenseState
{
    public string Key { get; set; } = string.Empty;
    public string? LastStatus { get; set; }          // active | expired | inactive
    public string? ClientName { get; set; }
    public string? ExpiresDisplay { get; set; }      // human-readable date from server
    public DateTime? LastSuccessfulCheckUtc { get; set; }
}

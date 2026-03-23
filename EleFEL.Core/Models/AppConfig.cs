namespace EleFEL.Core.Models;

/// <summary>
/// Main application configuration loaded from config.json
/// </summary>
public class AppConfig
{
    public EleventaConfig Eleventa { get; set; } = new();
    public InfileConfig Infile { get; set; } = new();
    public EmitterConfig Emitter { get; set; } = new();
    public PrinterConfig Printer { get; set; } = new();
    public SystemConfig System { get; set; } = new();
}

public class EleventaConfig
{
    public string DatabasePath { get; set; } = string.Empty;
    public int PollingIntervalSeconds { get; set; } = 10;
}

public class InfileConfig
{
    // Signing endpoint (public, no auth required)
    public string SigningUrl { get; set; } = "https://signer-emisores.feel.com.gt/sign_solicitud_firmas/firma_xml";

    // Certification endpoint
    public string CertificationUrl { get; set; } = "https://certificador.feel.com.gt/fel/certificacion/dte/";

    // Cancellation endpoint
    public string CancellationUrl { get; set; } = "https://certificador.feel.com.gt/fel/anulacion/dte/";

    // NIT lookup endpoint
    public string NitConsultUrl { get; set; } = "https://consultareceptores.feel.com.gt/rest/action";

    // Credentials for signing (llave firma)
    public string SigningKey { get; set; } = string.Empty;

    // Credentials for certification (headers: usuario, llave)
    public string CertUser { get; set; } = string.Empty;
    public string CertKey { get; set; } = string.Empty;

    // Internal code used for signing (codigo) and certification (identificador)
    public string EmitterCode { get; set; } = string.Empty;

    // Alias for signing
    public string SigningAlias { get; set; } = string.Empty;

    // Email to receive a copy of certified invoices
    public string CopyEmail { get; set; } = string.Empty;

    // NIT of the certifier (Infile)
    public string CertifierNit { get; set; } = "12521337";

    public bool UseSandbox { get; set; } = true;
}

public class EmitterConfig
{
    public string Nit { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CommercialName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string Municipality { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Country { get; set; } = "GT";
    public string TaxpayerType { get; set; } = "GEN"; // GEN = General, PEQ = Pequeño Contribuyente
    public string CurrencyCode { get; set; } = "GTQ";
    public string EstablishmentCode { get; set; } = "1";
    public List<FraseConfig> Frases { get; set; } = new();
}

public class FraseConfig
{
    public int TipoFrase { get; set; }
    public int CodigoEscenario { get; set; }
}

public class PrinterConfig
{
    public string PrinterName { get; set; } = string.Empty;
    public int PaperWidthMm { get; set; } = 80;
    public bool Enabled { get; set; } = true;
}

public class SystemConfig
{
    public string DataDirectory { get; set; } = "Data";
    public string LogDirectory { get; set; } = "Logs";
    public string InvoiceDirectory { get; set; } = "Invoices";
    public int QueueRetryIntervalSeconds { get; set; } = 60;
    public int MaxRetryAttempts { get; set; } = 10;
    public int PostponedExpirationDays { get; set; } = 3;
}

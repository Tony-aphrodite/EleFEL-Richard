using System.Text.Json;
using System.Text.Json.Serialization;
using EleFEL.Core.Models;

namespace EleFEL.Core.Services;

/// <summary>
/// Manages loading and saving of application configuration from config.json
/// </summary>
public class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _configPath;

    public AppConfig Config { get; private set; } = new();

    public ConfigService(string? configPath = null)
    {
        _configPath = configPath
            ?? Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                "config.json");
    }

    public AppConfig Load()
    {
        if (!File.Exists(_configPath))
        {
            Config = CreateDefault();
            Save();
            return Config;
        }

        var json = File.ReadAllText(_configPath);
        Config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        return Config;
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(Config, JsonOptions);
        File.WriteAllText(_configPath, json);
    }

    private static AppConfig CreateDefault()
    {
        return new AppConfig
        {
            Eleventa = new EleventaConfig
            {
                DatabasePath = @"C:\Eleventa\Data\PDVDATA.FDB",
                PollingIntervalSeconds = 10
            },
            Infile = new InfileConfig
            {
                UseSandbox = true,
                SigningKey = "",
                CertUser = "",
                CertKey = "",
                EmitterCode = "",
                SigningAlias = "",
                CopyEmail = ""
            },
            Emitter = new EmitterConfig
            {
                Nit = "",
                Name = "",
                CommercialName = "",
                Address = "",
                Municipality = "Guatemala",
                Department = "Guatemala",
                TaxpayerType = "GEN"
            },
            Printer = new PrinterConfig
            {
                PaperWidthMm = 80,
                Enabled = true
            },
            System = new SystemConfig
            {
                DataDirectory = "Data",
                LogDirectory = "Logs",
                InvoiceDirectory = "Invoices",
                QueueRetryIntervalSeconds = 60
            }
        };
    }
}

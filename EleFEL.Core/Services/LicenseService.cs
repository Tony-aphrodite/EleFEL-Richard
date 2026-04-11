using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EleFEL.Core.Models;

namespace EleFEL.Core.Services;

public enum LicenseStatus
{
    Unknown,
    Active,
    Expired,
    Inactive,
    NotFound,
    Error,
    OfflineGrace,
    OfflineGraceExpired,
    NotActivated
}

public class LicenseCheckResult
{
    public LicenseStatus Status { get; set; }
    public string? Message { get; set; }
    public string? ClientName { get; set; }
    public string? ExpiresDisplay { get; set; }

    public bool IsAllowed =>
        Status == LicenseStatus.Active || Status == LicenseStatus.OfflineGrace;
}

/// <summary>
/// Verifies the EleFEL monthly license against sistema-elefel.vercel.app.
/// Caches last successful result to allow a short offline grace period.
/// </summary>
public class LicenseService
{
    public const string DefaultVerifyUrl = "https://sistema-elefel.vercel.app/api/verify";
    public const int GraceDays = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _statePath;
    private readonly LogService _logService;
    private readonly HttpClient _http;
    private readonly string _verifyUrl;

    public LicenseState State { get; private set; } = new();

    public LicenseService(string statePath, LogService logService, HttpClient? http = null, string? verifyUrl = null)
    {
        _statePath = statePath;
        _logService = logService;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _verifyUrl = verifyUrl ?? DefaultVerifyUrl;
        Load();
    }

    public bool HasKey => !string.IsNullOrWhiteSpace(State.Key);

    public void Load()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var json = File.ReadAllText(_statePath);
                State = JsonSerializer.Deserialize<LicenseState>(json, JsonOptions) ?? new LicenseState();
            }
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to load license state", ex);
            State = new LicenseState();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(State, JsonOptions);
            File.WriteAllText(_statePath, json);
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to save license state", ex);
        }
    }

    public void Clear()
    {
        State = new LicenseState();
        Save();
    }

    /// <summary>
    /// Contacts the verification server. On active/expired/inactive, updates cached state.
    /// Network failures return Status=Error so the caller can fall back to cache.
    /// </summary>
    public async Task<LicenseCheckResult> VerifyOnlineAsync(string key, CancellationToken ct = default)
    {
        var normalized = (key ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized))
            return new LicenseCheckResult { Status = LicenseStatus.Error, Message = "Ingrese una llave de licencia." };

        try
        {
            var payload = JsonSerializer.Serialize(new { key = normalized });
            using var req = new HttpRequestMessage(HttpMethod.Post, _verifyUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.BadRequest)
            {
                _logService.LogWarning($"License verify HTTP {(int)resp.StatusCode}: {body}");
                return new LicenseCheckResult
                {
                    Status = LicenseStatus.Error,
                    Message = $"Servidor respondió {(int)resp.StatusCode}."
                };
            }

            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;

            switch (status)
            {
                case "active":
                    var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var expires = doc.RootElement.TryGetProperty("expires", out var e) ? e.GetString() : null;
                    State.Key = normalized;
                    State.LastStatus = "active";
                    State.ClientName = name;
                    State.ExpiresDisplay = expires;
                    State.LastSuccessfulCheckUtc = DateTime.UtcNow;
                    Save();
                    _logService.LogInfo($"License active: {name} (expira {expires})");
                    return new LicenseCheckResult
                    {
                        Status = LicenseStatus.Active,
                        ClientName = name,
                        ExpiresDisplay = expires
                    };

                case "expired":
                    State.Key = normalized;
                    State.LastStatus = "expired";
                    Save();
                    return new LicenseCheckResult
                    {
                        Status = LicenseStatus.Expired,
                        Message = "La licencia ha expirado. Renueve su suscripción mensual."
                    };

                case "inactive":
                    State.Key = normalized;
                    State.LastStatus = "inactive";
                    Save();
                    return new LicenseCheckResult
                    {
                        Status = LicenseStatus.Inactive,
                        Message = "La licencia está inactiva. Contacte al proveedor."
                    };

                case "not_found":
                    return new LicenseCheckResult
                    {
                        Status = LicenseStatus.NotFound,
                        Message = "Llave de licencia no encontrada. Verifique e intente de nuevo."
                    };

                default:
                    return new LicenseCheckResult
                    {
                        Status = LicenseStatus.Error,
                        Message = "Respuesta inválida del servidor."
                    };
            }
        }
        catch (TaskCanceledException)
        {
            return new LicenseCheckResult
            {
                Status = LicenseStatus.Error,
                Message = "Tiempo de espera agotado al contactar el servidor."
            };
        }
        catch (HttpRequestException ex)
        {
            _logService.LogWarning($"License network error: {ex.Message}");
            return new LicenseCheckResult
            {
                Status = LicenseStatus.Error,
                Message = "Sin conexión a internet."
            };
        }
        catch (Exception ex)
        {
            _logService.LogError("License verify failed", ex);
            return new LicenseCheckResult
            {
                Status = LicenseStatus.Error,
                Message = $"Error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Evaluates locally cached state — used when the network is unreachable.
    /// </summary>
    public LicenseCheckResult EvaluateCached()
    {
        if (string.IsNullOrWhiteSpace(State.Key))
            return new LicenseCheckResult
            {
                Status = LicenseStatus.NotActivated,
                Message = "No hay licencia activada."
            };

        if (State.LastStatus == "expired")
            return new LicenseCheckResult
            {
                Status = LicenseStatus.Expired,
                ClientName = State.ClientName,
                ExpiresDisplay = State.ExpiresDisplay,
                Message = "La licencia ha expirado."
            };

        if (State.LastStatus == "inactive")
            return new LicenseCheckResult
            {
                Status = LicenseStatus.Inactive,
                Message = "La licencia está inactiva."
            };

        if (State.LastStatus != "active" || State.LastSuccessfulCheckUtc == null)
            return new LicenseCheckResult
            {
                Status = LicenseStatus.Unknown,
                Message = "Estado de licencia desconocido. Conéctese a internet para verificar."
            };

        var age = DateTime.UtcNow - State.LastSuccessfulCheckUtc.Value;
        if (age.TotalDays > GraceDays)
            return new LicenseCheckResult
            {
                Status = LicenseStatus.OfflineGraceExpired,
                ClientName = State.ClientName,
                ExpiresDisplay = State.ExpiresDisplay,
                Message = $"No se ha podido verificar la licencia en {(int)age.TotalDays} días. Conéctese a internet."
            };

        return new LicenseCheckResult
        {
            Status = LicenseStatus.OfflineGrace,
            ClientName = State.ClientName,
            ExpiresDisplay = State.ExpiresDisplay
        };
    }

    /// <summary>
    /// Startup check: tries online verification; falls back to cached grace period on network failure.
    /// </summary>
    public async Task<LicenseCheckResult> CheckOnStartupAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(State.Key))
            return new LicenseCheckResult
            {
                Status = LicenseStatus.NotActivated,
                Message = "Ingrese su llave de licencia."
            };

        var online = await VerifyOnlineAsync(State.Key, ct).ConfigureAwait(false);
        if (online.Status == LicenseStatus.Error)
        {
            _logService.LogWarning("License: network check failed, evaluating cached state.");
            return EvaluateCached();
        }
        return online;
    }
}

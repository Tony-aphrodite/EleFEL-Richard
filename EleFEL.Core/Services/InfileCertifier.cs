using System.Text;
using System.Text.Json;
using EleFEL.Core.Interfaces;
using EleFEL.Core.Models;

namespace EleFEL.Core.Services;

/// <summary>
/// Infile FEL certifier implementation.
/// Based on official Infile documentation:
/// - Signing: POST to signer-emisores.feel.com.gt (public, no auth)
/// - Certification: POST to certificador.feel.com.gt (headers: usuario, llave, identificador)
/// - Cancellation: POST to certificador.feel.com.gt/fel/anulacion/dte/
/// </summary>
public class InfileCertifier : IFelCertifier, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly InfileConfig _config;
    private readonly EmitterConfig _emitter;
    private readonly LogService _log;
    private int _identifierCounter;

    public string CertifierName => "Infile";

    public InfileCertifier(InfileConfig config, EmitterConfig emitter, LogService log)
    {
        _config = config;
        _emitter = emitter;
        _log = log;
        _identifierCounter = (int)(DateTime.Now.Ticks % 1000000);
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// Signs and certifies an XML DTE document through Infile.
    /// Step 1: Sign XML via public signing endpoint
    /// Step 2: Send signed XML for certification with auth headers
    /// </summary>
    public async Task<CertificationResult> CertifyAsync(string xmlContent)
    {
        // Demo mode: return mock certification result without calling Infile API
        if (_config.UseSandbox)
        {
            _log.LogInfo("DEMO MODE: Generating mock certification (no API call)");
            var demoUuid = $"DEMO-{Guid.NewGuid():N}"[..36].ToUpperInvariant();
            return new CertificationResult
            {
                Success = true,
                Uuid = demoUuid,
                AuthorizationNumber = $"DEMO-{DateTime.Now:yyyyMMddHHmmss}",
                SerialNumber = "DEMO-SERIE",
                DteNumber = $"{++_identifierCounter}",
                CertificationDate = DateTime.Now,
                CertifiedXml = xmlContent
            };
        }

        try
        {
            // Step 1: Sign the XML (public endpoint, no auth)
            _log.LogInfo("Step 1: Signing XML with Infile...");
            // Save generated XML for debugging
            try
            {
                var debugDir = Path.Combine(
                    Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                    "Debug");
                Directory.CreateDirectory(debugDir);
                File.WriteAllText(Path.Combine(debugDir, $"last_xml_{DateTime.Now:HHmmss}.xml"), xmlContent);
                _log.LogInfo($"Debug XML saved to Debug folder");
            }
            catch { }
            var signedXmlBase64 = await SignXmlAsync(xmlContent, isAnulacion: false);
            if (signedXmlBase64 == null)
            {
                return new CertificationResult
                {
                    Success = false,
                    ErrorMessage = "Failed to sign XML with Infile"
                };
            }

            // Step 2: Send signed XML for certification
            _log.LogInfo("Step 2: Sending signed XML for certification...");
            return await SendForCertificationAsync(signedXmlBase64);
        }
        catch (HttpRequestException ex)
        {
            _log.LogError("Network error connecting to Infile", ex);
            return new CertificationResult
            {
                Success = false,
                ErrorMessage = $"Network error: {ex.Message}",
                ErrorCode = "NETWORK_ERROR"
            };
        }
        catch (TaskCanceledException)
        {
            _log.LogError("Timeout connecting to Infile");
            return new CertificationResult
            {
                Success = false,
                ErrorMessage = "Connection timeout to Infile",
                ErrorCode = "TIMEOUT"
            };
        }
        catch (Exception ex)
        {
            _log.LogError("Unexpected error during certification", ex);
            return new CertificationResult
            {
                Success = false,
                ErrorMessage = $"Unexpected error: {ex.Message}",
                ErrorCode = "UNKNOWN"
            };
        }
    }

    /// <summary>
    /// Cancels a previously certified DTE via Infile's anulacion endpoint
    /// </summary>
    public async Task<CertificationResult> CancelAsync(string uuid, string reason)
    {
        try
        {
            // Build cancellation XML per official Infile example
            var cancelXml = BuildCancellationXml(uuid, reason, _emitter.Nit, "CF");

            // Sign the cancellation XML
            var signedXmlBase64 = await SignXmlAsync(cancelXml, isAnulacion: true);
            if (signedXmlBase64 == null)
            {
                return new CertificationResult
                {
                    Success = false,
                    ErrorMessage = "Failed to sign cancellation XML"
                };
            }

            // Send to cancellation endpoint
            var identifier = $"ANUL_{Interlocked.Increment(ref _identifierCounter)}";
            var request = CreateCertificationRequest(_config.CancellationUrl, signedXmlBase64, identifier);
            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _log.LogInfo($"DTE cancelled: UUID={uuid}");
                return new CertificationResult { Success = true, Uuid = uuid };
            }

            _log.LogError($"Cancellation failed: {responseBody}");
            return new CertificationResult
            {
                Success = false,
                ErrorMessage = ParseErrorMessage(responseBody)
            };
        }
        catch (Exception ex)
        {
            _log.LogError("Error cancelling DTE", ex);
            return new CertificationResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Looks up a NIT in Infile's database to get the registered business name.
    /// URL: https://consultareceptores.feel.com.gt/rest/action
    /// </summary>
    public async Task<NitLookupResult> LookupNitAsync(string nit)
    {
        try
        {
            var cleanNit = nit.Replace("-", "").Replace(" ", "");

            var requestBody = new
            {
                emisor_codigo = _config.EmitterCode,
                emisor_clave = _config.CertKey,
                nit_consulta = cleanNit
            };

            var json = JsonSerializer.Serialize(requestBody);
            var request = new HttpRequestMessage(HttpMethod.Post, _config.NitConsultUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var doc = JsonDocument.Parse(responseBody);
                var nombre = doc.RootElement.TryGetProperty("nombre", out var n) ? n.GetString() : null;
                var mensaje = doc.RootElement.TryGetProperty("mensaje", out var m) ? m.GetString() : null;

                if (!string.IsNullOrEmpty(nombre))
                {
                    _log.LogInfo($"NIT lookup: {cleanNit} = {nombre}");
                    return new NitLookupResult { Found = true, Nit = cleanNit, Name = nombre };
                }

                return new NitLookupResult { Found = false, Nit = cleanNit, ErrorMessage = mensaje ?? "NIT not found" };
            }

            return new NitLookupResult { Found = false, Nit = cleanNit, ErrorMessage = "NIT lookup service unavailable" };
        }
        catch (Exception ex)
        {
            _log.LogError($"NIT lookup error for {nit}", ex);
            return new NitLookupResult { Found = false, Nit = nit, ErrorMessage = ex.Message };
        }
    }

    // ─── Step 1: Signing (Public endpoint, no auth) ──────────────────

    private async Task<string?> SignXmlAsync(string xmlContent, bool isAnulacion)
    {
        try
        {
            var xmlBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(xmlContent));

            // Per Infile manual: POST JSON, no authorization needed
            var signRequest = new
            {
                llave = _config.SigningKey,
                archivo = xmlBase64,
                codigo = _config.EmitterCode,
                alias = _config.SigningAlias,
                es_anulacion = isAnulacion ? "S" : "N"
            };

            var jsonContent = JsonSerializer.Serialize(signRequest);
            var request = new HttpRequestMessage(HttpMethod.Post, _config.SigningUrl)
            {
                Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
            };
            // No authorization headers needed - signing endpoint is public

            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var signResponse = JsonDocument.Parse(responseBody);

                if (signResponse.RootElement.TryGetProperty("resultado", out var resultado)
                    && resultado.GetBoolean())
                {
                    if (signResponse.RootElement.TryGetProperty("archivo", out var signedFile))
                    {
                        var signedBase64 = signedFile.GetString();
                        _log.LogInfo("XML signed successfully");
                        return signedBase64;
                    }
                }

                var desc = signResponse.RootElement.TryGetProperty("descripcion", out var d) ? d.GetString() : "Unknown";
                _log.LogError($"Signing failed: {desc}");
                return null;
            }

            _log.LogError($"Signing HTTP error {response.StatusCode}: {responseBody}");
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError("Error signing XML", ex);
            return null;
        }
    }

    // ─── Step 2: Certification (Custom headers: usuario, llave, identificador) ───

    private async Task<CertificationResult> SendForCertificationAsync(string signedXmlBase64)
    {
        var identifier = $"EleFEL_{Interlocked.Increment(ref _identifierCounter)}";
        var request = CreateCertificationRequest(_config.CertificationUrl, signedXmlBase64, identifier);

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            return ParseCertificationResponse(responseBody, signedXmlBase64);
        }

        _log.LogError($"Certification rejected ({response.StatusCode}): {responseBody}");
        return new CertificationResult
        {
            Success = false,
            ErrorMessage = ParseErrorMessage(responseBody),
            ErrorCode = response.StatusCode.ToString()
        };
    }

    /// <summary>
    /// Creates a certification/cancellation request per Infile manual:
    /// Headers: usuario, llave, identificador, Content-Type: application/json
    /// Body: { "nit_emisor": "...", "correo_copia": "...", "xml_dte": "base64_signed_xml" }
    /// </summary>
    private HttpRequestMessage CreateCertificationRequest(string url, string signedXmlBase64, string identifier)
    {
        var body = new
        {
            nit_emisor = _emitter.Nit,
            correo_copia = _config.CopyEmail,
            xml_dte = signedXmlBase64
        };

        var jsonContent = JsonSerializer.Serialize(body);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
        };

        // Custom headers per Infile manual
        request.Headers.Add("usuario", _config.CertUser);
        request.Headers.Add("llave", _config.CertKey);
        request.Headers.Add("identificador", identifier);

        return request;
    }

    private CertificationResult ParseCertificationResponse(string responseBody, string signedXmlBase64)
    {
        try
        {
            var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var resultado = root.TryGetProperty("resultado", out var res) && res.GetBoolean();
            var uuid = root.TryGetProperty("uuid", out var u) ? u.GetString() : null;
            var fecha = root.TryGetProperty("fecha", out var f) ? f.GetString() : null;
            var descripcion = root.TryGetProperty("descripcion", out var d) ? d.GetString() : null;

            // Get the certified XML from response
            var certifiedXml = root.TryGetProperty("xml_certificado", out var xmlCert) ? xmlCert.GetString() : null;

            // Decode the signed XML to get the actual XML content for storage
            string? decodedXml = null;
            try
            {
                if (certifiedXml != null)
                    decodedXml = Encoding.UTF8.GetString(Convert.FromBase64String(certifiedXml));
                else if (signedXmlBase64 != null)
                    decodedXml = Encoding.UTF8.GetString(Convert.FromBase64String(signedXmlBase64));
            }
            catch
            {
                decodedXml = certifiedXml ?? signedXmlBase64;
            }

            if (resultado && !string.IsNullOrEmpty(uuid))
            {
                _log.LogInfo($"Certification successful: UUID={uuid}, Fecha={fecha}");

                return new CertificationResult
                {
                    Success = true,
                    Uuid = uuid,
                    AuthorizationNumber = uuid,
                    CertificationDate = DateTime.TryParse(fecha, out var dt) ? dt : DateTime.Now,
                    CertifiedXml = decodedXml
                };
            }

            var alertas = root.TryGetProperty("descripcion_alertas_sat", out var al) ? al.ToString() : "";
            var errores = root.TryGetProperty("descripcion_errores", out var er) ? er.ToString() : "";

            _log.LogError($"Certification not successful: {descripcion} | Alertas: {alertas} | Errores: {errores}");
            return new CertificationResult
            {
                Success = false,
                ErrorMessage = descripcion ?? "Certification failed",
                ErrorCode = errores
            };
        }
        catch (Exception ex)
        {
            _log.LogError("Error parsing certification response", ex);
            return new CertificationResult
            {
                Success = false,
                ErrorMessage = "Failed to parse certification response",
                CertifiedXml = responseBody
            };
        }
    }

    private static string ParseErrorMessage(string responseBody)
    {
        try
        {
            var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("descripcion", out var desc))
                return desc.GetString() ?? responseBody;
            if (doc.RootElement.TryGetProperty("mensaje", out var msg))
                return msg.GetString() ?? responseBody;
        }
        catch
        {
            // Response might not be JSON
        }
        return responseBody;
    }

    /// <summary>
    /// Builds cancellation XML per official Infile ANULACIÓN.xml example.
    /// Required fields: FechaEmisionDocumentoAnular, FechaHoraAnulacion,
    /// IDReceptor, MotivoAnulacion, NITEmisor, NumeroDocumentoAAnular
    /// </summary>
    private static string BuildCancellationXml(string uuid, string reason,
        string nitEmisor, string idReceptor, DateTime? fechaEmisionOriginal = null)
    {
        var now = DateTime.Now;
        var fechaEmision = fechaEmisionOriginal ?? now;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <dte:GTAnulacionDocumento xmlns:ds="http://www.w3.org/2000/09/xmldsig#" xmlns:dte="http://www.sat.gob.gt/dte/fel/0.1.0" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" Version="0.1">
                <dte:SAT>
                    <dte:AnulacionDTE ID="DatosCertificados">
                        <dte:DatosGenerales
                            FechaEmisionDocumentoAnular="{fechaEmision:yyyy-MM-ddTHH:mm:sszzz}"
                            FechaHoraAnulacion="{now:yyyy-MM-ddTHH:mm:sszzz}"
                            ID="DatosAnulacion"
                            IDReceptor="{idReceptor}"
                            MotivoAnulacion="{reason}"
                            NITEmisor="{nitEmisor}"
                            NumeroDocumentoAAnular="{uuid}" />
                    </dte:AnulacionDTE>
                </dte:SAT>
            </dte:GTAnulacionDocumento>
            """;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Result of a NIT lookup via Infile's consultation service
/// </summary>
public class NitLookupResult
{
    public bool Found { get; set; }
    public string Nit { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? ErrorMessage { get; set; }
}

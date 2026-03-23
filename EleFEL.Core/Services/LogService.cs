using Microsoft.Extensions.Logging;

namespace EleFEL.Core.Services;

/// <summary>
/// File-based daily logging service for tracking system events,
/// certified invoices, and errors
/// </summary>
public class LogService : ILogger, IDisposable
{
    private readonly string _logDirectory;
    private readonly object _lock = new();
    private StreamWriter? _currentWriter;
    private string _currentDate = string.Empty;

    public LogService(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public void LogInfo(string message) => WriteLog("INFO", message);
    public void LogWarning(string message) => WriteLog("WARN", message);
    public void LogError(string message, Exception? ex = null)
    {
        var msg = ex != null ? $"{message} | {ex.GetType().Name}: {ex.Message}" : message;
        WriteLog("ERROR", msg);
    }

    public void LogSaleDetected(long saleId, decimal total)
        => LogInfo($"Sale detected: ID={saleId}, Total={total:F2} GTQ");

    public void LogInvoiceCertified(long saleId, string uuid)
        => LogInfo($"Invoice certified: SaleID={saleId}, UUID={uuid}");

    public void LogInvoiceQueued(long saleId, string reason)
        => LogWarning($"Invoice queued: SaleID={saleId}, Reason={reason}");

    public void LogRetry(long saleId, int attempt, int maxAttempts)
        => LogWarning($"Retry certification: SaleID={saleId}, Attempt={attempt}/{maxAttempts}");

    private void WriteLog(string level, string message)
    {
        lock (_lock)
        {
            try
            {
                EnsureWriter();
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                _currentWriter?.WriteLine($"[{timestamp}] [{level,-5}] {message}");
                _currentWriter?.Flush();
            }
            catch
            {
                // Logging should never crash the application
            }
        }
    }

    private void EnsureWriter()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (today == _currentDate && _currentWriter != null) return;

        _currentWriter?.Dispose();
        _currentDate = today;

        var filePath = Path.Combine(_logDirectory, $"EleFEL_{today}.log");
        _currentWriter = new StreamWriter(filePath, append: true) { AutoFlush = false };
    }

    // ILogger implementation for Microsoft.Extensions.Logging compatibility
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        switch (logLevel)
        {
            case LogLevel.Error:
            case LogLevel.Critical:
                LogError(message, exception);
                break;
            case LogLevel.Warning:
                LogWarning(message);
                break;
            default:
                LogInfo(message);
                break;
        }
    }

    public void Dispose()
    {
        _currentWriter?.Dispose();
        GC.SuppressFinalize(this);
    }
}

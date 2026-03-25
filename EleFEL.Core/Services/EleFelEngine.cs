using EleFEL.Core.Interfaces;
using EleFEL.Core.Models;

namespace EleFEL.Core.Services;

/// <summary>
/// Main orchestrator that connects all services together.
/// Runs the polling loop, handles new sale detection, and coordinates
/// the entire invoice lifecycle from detection to printing.
/// </summary>
public class EleFelEngine : IDisposable
{
    private readonly AppConfig _config;
    private readonly EleventaPollingService _polling;
    private readonly LocalDatabaseService _db;
    private readonly XmlDteGenerator _xmlGenerator;
    private readonly InvoiceQueueService _queue;
    private readonly ThermalPrinterService _printer;
    private readonly InfileCertifier? _infileCertifier;
    private readonly LogService _log;

    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private Task? _queueTask;
    private DateTime _engineStartTime;

    public EngineStatus Status { get; private set; } = EngineStatus.Stopped;

    // Events for UI updates
    public event Action<EngineStatus>? OnStatusChanged;
    public event Action<EleventaSale>? OnNewSaleRequiresNit;
    public event Action<Invoice>? OnInvoiceCertified;
    public event Action<int>? OnPendingCountChanged;
    public event Action<string>? OnError;

    public EleFelEngine(
        AppConfig config,
        EleventaPollingService polling,
        LocalDatabaseService db,
        XmlDteGenerator xmlGenerator,
        InvoiceQueueService queue,
        ThermalPrinterService printer,
        LogService log,
        InfileCertifier? infileCertifier = null)
    {
        _config = config;
        _polling = polling;
        _db = db;
        _xmlGenerator = xmlGenerator;
        _queue = queue;
        _printer = printer;
        _infileCertifier = infileCertifier;
        _log = log;

        _queue.OnInvoiceCertified += inv => OnInvoiceCertified?.Invoke(inv);
        _queue.OnPendingCountChanged += count => OnPendingCountChanged?.Invoke(count);
    }

    /// <summary>
    /// Starts the engine: initializes DB, cleans expired postponed, begins polling and queue processing
    /// </summary>
    public async Task StartAsync()
    {
        if (Status == EngineStatus.Running) return;

        _log.LogInfo("EleFEL Engine starting...");

        _engineStartTime = DateTime.Now;

        await _db.InitializeAsync();

        // On first run, set baseline to current max sale ID in Eleventa
        // so we only detect NEW sales from this point forward
        if (!await _db.HasBaselineSaleIdAsync())
        {
            var currentMaxId = await _polling.GetCurrentMaxSaleIdAsync();
            if (currentMaxId > 0)
            {
                await _db.SetBaselineSaleIdAsync(currentMaxId);
                _log.LogInfo($"First run: baseline set to SaleID={currentMaxId}. Only new sales will be detected.");
            }
            else
            {
                _log.LogWarning("Could not get max sale ID from Eleventa. Will use timestamp filter as fallback.");
            }
        }

        // Clean up expired postponed invoices on startup
        var deleted = await _db.DeleteExpiredPostponedAsync(_config.System.PostponedExpirationDays);
        if (deleted > 0)
        {
            _log.LogInfo($"Cleaned up {deleted} expired postponed invoice(s) (older than {_config.System.PostponedExpirationDays} days)");
        }

        _cts = new CancellationTokenSource();
        Status = EngineStatus.Running;
        OnStatusChanged?.Invoke(Status);

        _pollingTask = RunPollingLoopAsync(_cts.Token);
        _queueTask = RunQueueLoopAsync(_cts.Token);

        _log.LogInfo("EleFEL Engine started successfully");
    }

    /// <summary>
    /// Stops the engine gracefully
    /// </summary>
    public async Task StopAsync()
    {
        if (Status == EngineStatus.Stopped) return;

        _log.LogInfo("EleFEL Engine stopping...");
        _cts?.Cancel();

        if (_pollingTask != null) await _pollingTask;
        if (_queueTask != null) await _queueTask;

        Status = EngineStatus.Stopped;
        OnStatusChanged?.Invoke(Status);
        _log.LogInfo("EleFEL Engine stopped");
    }

    /// <summary>
    /// Searches for a customer by NIT: first in local DB, then via Infile API
    /// </summary>
    public async Task<Customer?> GetCustomerByNitAsync(string nit)
    {
        // First try local database
        var local = await _db.GetCustomerByNitAsync(nit);
        if (local != null) return local;

        // If not found locally, try Infile NIT lookup API
        if (_infileCertifier != null)
        {
            var lookup = await _infileCertifier.LookupNitAsync(nit);
            if (lookup.Found && !string.IsNullOrEmpty(lookup.Name))
            {
                return new Customer { Nit = nit, Name = lookup.Name };
            }
        }

        return null;
    }

    /// <summary>
    /// Searches customers by partial NIT or name match
    /// </summary>
    public async Task<List<Customer>> SearchCustomersAsync(string searchTerm)
    {
        return await _db.SearchCustomersAsync(searchTerm);
    }

    /// <summary>
    /// Called by UI when the cashier provides NIT for a sale
    /// </summary>
    public async Task ProcessSaleWithNitAsync(EleventaSale sale, Customer customer)
    {
        try
        {
            // Save/update customer in local DB
            await _db.SaveCustomerAsync(customer);

            // Check for duplicates
            if (await _db.IsSaleAlreadyProcessedAsync(sale.SaleId))
            {
                _log.LogWarning($"Sale {sale.SaleId} already processed, skipping");
                return;
            }

            // Generate XML
            var xml = _xmlGenerator.GenerateXml(sale, customer);

            // Create invoice record
            var invoice = new Invoice
            {
                EleventaSaleId = sale.SaleId,
                CustomerNit = customer.Nit,
                CustomerName = customer.Name,
                Total = sale.Total,
                Status = InvoiceStatus.Pending,
                XmlContent = xml
            };

            await _db.SaveInvoiceAsync(invoice);
            _log.LogInfo($"Invoice created for SaleID={sale.SaleId}, NIT={customer.Nit}");

            // Try to certify immediately
            await _queue.ProcessSingleInvoiceAsync(invoice);

            // Print if certified
            if (invoice.Status == InvoiceStatus.Certified)
            {
                await _printer.PrintInvoiceAsync(invoice, sale, _config.Emitter);
            }
            else
            {
                _log.LogInvoiceQueued(sale.SaleId, invoice.ErrorMessage ?? "Pending certification");
            }

            var pendingCount = await _db.GetPendingCountAsync();
            OnPendingCountChanged?.Invoke(pendingCount);
        }
        catch (Exception ex)
        {
            _log.LogError($"Error processing sale {sale.SaleId}", ex);
            OnError?.Invoke($"Error processing sale {sale.SaleId}: {ex.Message}");
        }
    }

    // ─── Postponed Operations ────────────────────────────────────

    /// <summary>
    /// Saves a sale as postponed (Facturar Después)
    /// </summary>
    public async Task PostponeSaleAsync(EleventaSale sale)
    {
        if (await _db.IsSaleAlreadyProcessedAsync(sale.SaleId))
        {
            _log.LogWarning($"Sale {sale.SaleId} already processed, skipping postpone");
            return;
        }

        var invoice = new Invoice
        {
            EleventaSaleId = sale.SaleId,
            CustomerNit = string.Empty,
            CustomerName = string.Empty,
            Total = sale.Total,
            Status = InvoiceStatus.Postponed
        };

        await _db.SaveInvoiceAsync(invoice);
        _log.LogInfo($"Sale {sale.SaleId} postponed for later invoicing");
    }

    /// <summary>
    /// Gets all postponed invoices
    /// </summary>
    public async Task<List<Invoice>> GetPostponedInvoicesAsync()
    {
        return await _db.GetPostponedInvoicesAsync();
    }

    /// <summary>
    /// Returns the configured expiration days for postponed invoices
    /// </summary>
    public int GetPostponedExpirationDays()
    {
        return _config.System.PostponedExpirationDays;
    }

    /// <summary>
    /// Reactivates a postponed invoice for invoicing (triggers NIT window)
    /// </summary>
    public async Task ReactivatePostponedAsync(int invoiceId, long eleventaSaleId)
    {
        // Delete the postponed record so it can be re-processed
        await _db.DeletePostponedInvoiceAsync(invoiceId);
        _log.LogInfo($"Postponed invoice {invoiceId} (Sale {eleventaSaleId}) reactivated for invoicing");

        // Re-fetch the sale from Eleventa and trigger NIT window
        var sale = await _polling.GetSaleByIdAsync(eleventaSaleId);
        if (sale != null)
        {
            OnNewSaleRequiresNit?.Invoke(sale);
        }
    }

    /// <summary>
    /// Deletes a postponed invoice permanently
    /// </summary>
    public async Task DeletePostponedAsync(int invoiceId)
    {
        await _db.DeletePostponedInvoiceAsync(invoiceId);
        _log.LogInfo($"Postponed invoice {invoiceId} deleted by user");
    }

    /// <summary>
    /// Polling loop: checks Eleventa DB for new sales
    /// </summary>
    private async Task RunPollingLoopAsync(CancellationToken ct)
    {
        // Ensure polling runs on thread pool, not UI thread
        await Task.Yield();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var lastSaleId = await _db.GetLastProcessedSaleIdAsync().ConfigureAwait(false);
                var newSales = await _polling.GetNewSalesAsync(lastSaleId, _engineStartTime).ConfigureAwait(false);

                // Recover from Error state if polling succeeds
                if (Status == EngineStatus.Error)
                {
                    Status = EngineStatus.Running;
                    OnStatusChanged?.Invoke(Status);
                    _log.LogInfo("Engine recovered from error state");
                }

                foreach (var sale in newSales)
                {
                    // Check if sale was already processed (avoid duplicates)
                    if (await _db.IsSaleAlreadyProcessedAsync(sale.SaleId).ConfigureAwait(false))
                        continue;

                    _log.LogInfo($"New sale detected: SaleID={sale.SaleId}, Total={sale.Total}");
                    OnNewSaleRequiresNit?.Invoke(sale);
                }
            }
            catch (Exception ex)
            {
                _log.LogError("Error in polling loop", ex);
                if (Status != EngineStatus.Error)
                {
                    Status = EngineStatus.Error;
                    OnStatusChanged?.Invoke(Status);
                }
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_config.Eleventa.PollingIntervalSeconds),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Queue loop: retries pending invoices periodically
    /// </summary>
    private async Task RunQueueLoopAsync(CancellationToken ct)
    {
        await Task.Yield();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _queue.ProcessPendingAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError("Error in queue processing", ex);
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_config.System.QueueRetryIntervalSeconds),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _db.Dispose();
        _log.Dispose();
        GC.SuppressFinalize(this);
    }
}

public enum EngineStatus
{
    Stopped,
    Running,
    Error
}

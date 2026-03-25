using Dapper;
using EleFEL.Core.Models;
using Microsoft.Data.Sqlite;

namespace EleFEL.Core.Services;

/// <summary>
/// SQLite local database for tracking processed sales, customer records,
/// and pending invoice queue
/// </summary>
public class LocalDatabaseService : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private readonly SemaphoreSlim _dbLock = new(1, 1);

    public LocalDatabaseService(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "EleFEL.db");
        _connectionString = $"Data Source={dbPath}";
    }

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private async Task<T> WithLockAsync<T>(Func<Task<T>> action)
    {
        if (!await _dbLock.WaitAsync(LockTimeout))
            throw new TimeoutException("Database lock timeout");
        try { return await action(); }
        finally { _dbLock.Release(); }
    }

    private async Task WithLockAsync(Func<Task> action)
    {
        if (!await _dbLock.WaitAsync(LockTimeout))
            throw new TimeoutException("Database lock timeout");
        try { await action(); }
        finally { _dbLock.Release(); }
    }

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync();
        await CreateTablesAsync();
    }

    private async Task CreateTablesAsync()
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS Customers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Nit TEXT NOT NULL UNIQUE,
                Name TEXT NOT NULL,
                Address TEXT DEFAULT 'Ciudad',
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')),
                LastUsedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            CREATE TABLE IF NOT EXISTS Invoices (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EleventaSaleId INTEGER NOT NULL UNIQUE,
                CustomerNit TEXT NOT NULL,
                CustomerName TEXT NOT NULL,
                Total REAL NOT NULL,
                Status INTEGER NOT NULL DEFAULT 0,
                Uuid TEXT,
                AuthorizationNumber TEXT,
                SerialNumber TEXT,
                DteNumber TEXT,
                CertificationDate TEXT,
                XmlContent TEXT,
                XmlFilePath TEXT,
                PdfFilePath TEXT,
                ErrorMessage TEXT,
                RetryCount INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')),
                UpdatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            CREATE INDEX IF NOT EXISTS IX_Invoices_Status ON Invoices(Status);
            CREATE INDEX IF NOT EXISTS IX_Invoices_EleventaSaleId ON Invoices(EleventaSaleId);
            CREATE INDEX IF NOT EXISTS IX_Customers_Nit ON Customers(Nit);

            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """;

        await _connection!.ExecuteAsync(sql);
    }

    // ─── Customer Operations ──────────────────────────────────────

    public Task<Customer?> GetCustomerByNitAsync(string nit) => WithLockAsync(async () =>
    {
        const string sql = "SELECT * FROM Customers WHERE Nit = @Nit";
        return await _connection!.QueryFirstOrDefaultAsync<Customer>(sql, new { Nit = nit });
    });

    public Task<List<Customer>> SearchCustomersAsync(string searchTerm) => WithLockAsync(async () =>
    {
        const string sql = """
            SELECT * FROM Customers
            WHERE Nit LIKE @Term OR Name LIKE @Term
            ORDER BY LastUsedAt DESC
            LIMIT 10
            """;
        var results = await _connection!.QueryAsync<Customer>(sql, new { Term = $"%{searchTerm}%" });
        return results.ToList();
    });

    public Task SaveCustomerAsync(Customer customer) => WithLockAsync(async () =>
    {
        const string sql = """
            INSERT INTO Customers (Nit, Name, Address, CreatedAt, LastUsedAt)
            VALUES (@Nit, @Name, @Address, datetime('now','localtime'), datetime('now','localtime'))
            ON CONFLICT(Nit) DO UPDATE SET
                Name = excluded.Name,
                LastUsedAt = datetime('now','localtime')
            """;
        await _connection!.ExecuteAsync(sql, customer);
    });

    // ─── Invoice Operations ───────────────────────────────────────

    public Task<bool> IsSaleAlreadyProcessedAsync(long eleventaSaleId) => WithLockAsync(async () =>
    {
        const string sql = "SELECT COUNT(1) FROM Invoices WHERE EleventaSaleId = @SaleId";
        var count = await _connection!.ExecuteScalarAsync<int>(sql, new { SaleId = eleventaSaleId });
        return count > 0;
    });

    public Task<int> SaveInvoiceAsync(Invoice invoice) => WithLockAsync(async () =>
    {
        const string sql = """
            INSERT INTO Invoices (EleventaSaleId, CustomerNit, CustomerName, Total, Status,
                Uuid, AuthorizationNumber, SerialNumber, DteNumber, CertificationDate,
                XmlContent, XmlFilePath, PdfFilePath, ErrorMessage, RetryCount)
            VALUES (@EleventaSaleId, @CustomerNit, @CustomerName, @Total, @Status,
                @Uuid, @AuthorizationNumber, @SerialNumber, @DteNumber, @CertificationDate,
                @XmlContent, @XmlFilePath, @PdfFilePath, @ErrorMessage, @RetryCount);
            SELECT last_insert_rowid();
            """;
        var id = await _connection!.ExecuteScalarAsync<int>(sql, invoice);
        invoice.Id = id;
        return id;
    });

    public Task UpdateInvoiceAsync(Invoice invoice) => WithLockAsync(async () =>
    {
        const string sql = """
            UPDATE Invoices SET
                Status = @Status,
                Uuid = @Uuid,
                AuthorizationNumber = @AuthorizationNumber,
                SerialNumber = @SerialNumber,
                DteNumber = @DteNumber,
                CertificationDate = @CertificationDate,
                XmlContent = @XmlContent,
                XmlFilePath = @XmlFilePath,
                PdfFilePath = @PdfFilePath,
                ErrorMessage = @ErrorMessage,
                RetryCount = @RetryCount,
                UpdatedAt = datetime('now','localtime')
            WHERE Id = @Id
            """;
        await _connection!.ExecuteAsync(sql, invoice);
    });

    public Task<List<Invoice>> GetPendingInvoicesAsync() => WithLockAsync(async () =>
    {
        const string sql = """
            SELECT * FROM Invoices
            WHERE Status IN (@Pending, @Error)
            AND RetryCount < @MaxRetry
            ORDER BY CreatedAt ASC
            """;
        var results = await _connection!.QueryAsync<Invoice>(sql, new
        {
            Pending = (int)InvoiceStatus.Pending,
            Error = (int)InvoiceStatus.Error,
            MaxRetry = 10
        });
        return results.ToList();
    });

    public Task<int> GetPendingCountAsync() => WithLockAsync(async () =>
    {
        const string sql = "SELECT COUNT(1) FROM Invoices WHERE Status IN (0, 3) AND RetryCount < 10";
        return await _connection!.ExecuteScalarAsync<int>(sql);
    });

    public Task<long> GetLastProcessedSaleIdAsync() => WithLockAsync(async () =>
    {
        const string invoiceSql = "SELECT COALESCE(MAX(EleventaSaleId), 0) FROM Invoices WHERE Status != 5";
        var fromInvoices = await _connection!.ExecuteScalarAsync<long>(invoiceSql);

        const string baselineSql = "SELECT COALESCE(Value, '0') FROM Settings WHERE Key = 'BaselineSaleId'";
        var baselineStr = await _connection!.ExecuteScalarAsync<string>(baselineSql) ?? "0";
        long.TryParse(baselineStr, out var baseline);

        return Math.Max(fromInvoices, baseline);
    });

    public Task SetBaselineSaleIdAsync(long saleId) => WithLockAsync(async () =>
    {
        const string sql = """
            INSERT INTO Settings (Key, Value) VALUES ('BaselineSaleId', @Value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value
            """;
        await _connection!.ExecuteAsync(sql, new { Value = saleId.ToString() });
    });

    public Task<bool> HasBaselineSaleIdAsync() => WithLockAsync(async () =>
    {
        const string sql = "SELECT COUNT(1) FROM Settings WHERE Key = 'BaselineSaleId'";
        var count = await _connection!.ExecuteScalarAsync<int>(sql);
        return count > 0;
    });

    // ─── Postponed Operations ────────────────────────────────────

    public Task<List<Invoice>> GetPostponedInvoicesAsync() => WithLockAsync(async () =>
    {
        const string sql = """
            SELECT * FROM Invoices
            WHERE Status = @Postponed
            ORDER BY CreatedAt DESC
            """;
        var results = await _connection!.QueryAsync<Invoice>(sql, new
        {
            Postponed = (int)InvoiceStatus.Postponed
        });
        return results.ToList();
    });

    public Task<int> GetPostponedCountAsync() => WithLockAsync(async () =>
    {
        const string sql = "SELECT COUNT(1) FROM Invoices WHERE Status = 5";
        return await _connection!.ExecuteScalarAsync<int>(sql);
    });

    public Task DeletePostponedInvoiceAsync(int invoiceId) => WithLockAsync(async () =>
    {
        const string sql = "DELETE FROM Invoices WHERE Id = @Id AND Status = 5";
        await _connection!.ExecuteAsync(sql, new { Id = invoiceId });
    });

    public Task<int> DeleteExpiredPostponedAsync(int expirationDays) => WithLockAsync(async () =>
    {
        const string sql = """
            DELETE FROM Invoices
            WHERE Status = 5
            AND CreatedAt < datetime('now', 'localtime', @Days)
            """;
        return await _connection!.ExecuteAsync(sql, new { Days = $"-{expirationDays} days" });
    });

    public Task UpdateInvoiceStatusAsync(int invoiceId, InvoiceStatus status) => WithLockAsync(async () =>
    {
        const string sql = """
            UPDATE Invoices SET
                Status = @Status,
                UpdatedAt = datetime('now','localtime')
            WHERE Id = @Id
            """;
        await _connection!.ExecuteAsync(sql, new { Id = invoiceId, Status = (int)status });
    });

    public void Dispose()
    {
        _connection?.Dispose();
        _dbLock.Dispose();
        GC.SuppressFinalize(this);
    }
}

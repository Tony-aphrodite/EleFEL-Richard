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

    public LocalDatabaseService(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "EleFEL.db");
        _connectionString = $"Data Source={dbPath}";
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
            """;

        await _connection!.ExecuteAsync(sql);
    }

    // ─── Customer Operations ──────────────────────────────────────

    public async Task<Customer?> GetCustomerByNitAsync(string nit)
    {
        const string sql = "SELECT * FROM Customers WHERE Nit = @Nit";
        return await _connection!.QueryFirstOrDefaultAsync<Customer>(sql, new { Nit = nit });
    }

    public async Task<List<Customer>> SearchCustomersAsync(string searchTerm)
    {
        const string sql = """
            SELECT * FROM Customers
            WHERE Nit LIKE @Term OR Name LIKE @Term
            ORDER BY LastUsedAt DESC
            LIMIT 10
            """;
        var results = await _connection!.QueryAsync<Customer>(sql, new { Term = $"%{searchTerm}%" });
        return results.ToList();
    }

    public async Task SaveCustomerAsync(Customer customer)
    {
        const string sql = """
            INSERT INTO Customers (Nit, Name, Address, CreatedAt, LastUsedAt)
            VALUES (@Nit, @Name, @Address, datetime('now','localtime'), datetime('now','localtime'))
            ON CONFLICT(Nit) DO UPDATE SET
                Name = excluded.Name,
                LastUsedAt = datetime('now','localtime')
            """;
        await _connection!.ExecuteAsync(sql, customer);
    }

    // ─── Invoice Operations ───────────────────────────────────────

    public async Task<bool> IsSaleAlreadyProcessedAsync(long eleventaSaleId)
    {
        const string sql = "SELECT COUNT(1) FROM Invoices WHERE EleventaSaleId = @SaleId";
        var count = await _connection!.ExecuteScalarAsync<int>(sql, new { SaleId = eleventaSaleId });
        return count > 0;
    }

    public async Task<int> SaveInvoiceAsync(Invoice invoice)
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
    }

    public async Task UpdateInvoiceAsync(Invoice invoice)
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
    }

    public async Task<List<Invoice>> GetPendingInvoicesAsync()
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
    }

    public async Task<int> GetPendingCountAsync()
    {
        const string sql = "SELECT COUNT(1) FROM Invoices WHERE Status IN (0, 3) AND RetryCount < 10";
        return await _connection!.ExecuteScalarAsync<int>(sql);
    }

    public async Task<long> GetLastProcessedSaleIdAsync()
    {
        // Exclude Postponed (5) sales - they should be re-detectable after reactivation
        // Include: Pending (0), Sending (1), Certified (2), Error (3), Cancelled (4)
        const string sql = "SELECT COALESCE(MAX(EleventaSaleId), 0) FROM Invoices WHERE Status != 5";
        return await _connection!.ExecuteScalarAsync<long>(sql);
    }

    // ─── Postponed Operations ────────────────────────────────────

    public async Task<List<Invoice>> GetPostponedInvoicesAsync()
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
    }

    public async Task<int> GetPostponedCountAsync()
    {
        const string sql = "SELECT COUNT(1) FROM Invoices WHERE Status = 5";
        return await _connection!.ExecuteScalarAsync<int>(sql);
    }

    public async Task DeletePostponedInvoiceAsync(int invoiceId)
    {
        const string sql = "DELETE FROM Invoices WHERE Id = @Id AND Status = 5";
        await _connection!.ExecuteAsync(sql, new { Id = invoiceId });
    }

    public async Task<int> DeleteExpiredPostponedAsync(int expirationDays)
    {
        const string sql = """
            DELETE FROM Invoices
            WHERE Status = 5
            AND CreatedAt < datetime('now', 'localtime', @Days)
            """;
        return await _connection!.ExecuteAsync(sql, new { Days = $"-{expirationDays} days" });
    }

    public async Task UpdateInvoiceStatusAsync(int invoiceId, InvoiceStatus status)
    {
        const string sql = """
            UPDATE Invoices SET
                Status = @Status,
                UpdatedAt = datetime('now','localtime')
            WHERE Id = @Id
            """;
        await _connection!.ExecuteAsync(sql, new { Id = invoiceId, Status = (int)status });
    }

    public void Dispose()
    {
        _connection?.Dispose();
        GC.SuppressFinalize(this);
    }
}

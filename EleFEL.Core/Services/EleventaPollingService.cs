using EleFEL.Core.Models;
using FirebirdSql.Data.FirebirdClient;

namespace EleFEL.Core.Services;

/// <summary>
/// Monitors Eleventa's Firebird database for new sales using a polling strategy.
/// READ-ONLY access - never modifies Eleventa's database.
/// </summary>
public class EleventaPollingService : IDisposable
{
    private readonly string _connectionString;
    private readonly LogService _log;

    public EleventaPollingService(EleventaConfig config, LogService log)
    {
        _log = log;

        // Find Firebird embedded client library in app directory
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var fbClientPath = Path.Combine(appDir, "fbembed.dll");
        if (!File.Exists(fbClientPath))
            fbClientPath = Path.Combine(appDir, "fbclient.dll");

        // Eleventa uses embedded Firebird - connect in read-only mode
        var csb = new FbConnectionStringBuilder
        {
            Database = config.DatabasePath,
            ServerType = FbServerType.Embedded,
            UserID = "SYSDBA",
            Password = "masterkey",
            Charset = "UTF8",
            Pooling = false
        };

        if (File.Exists(fbClientPath))
            csb.ClientLibrary = fbClientPath;

        _connectionString = csb.ToString();
        _log.LogInfo($"Firebird connection configured: DB={config.DatabasePath}");
    }

    /// <summary>
    /// Reads new sales from Eleventa's database that haven't been processed yet
    /// </summary>
    public async Task<List<EleventaSale>> GetNewSalesAsync(long lastProcessedSaleId)
    {
        var sales = new List<EleventaSale>();

        try
        {
            await using var connection = new FbConnection(_connectionString);
            await connection.OpenAsync();

            // Eleventa uses VENTATICKETS as the main sales table
            // Database file: PDVDATA.FDB - verified column names from actual DB
            const string salesQuery = """
                SELECT
                    v.ID as SaleId,
                    v.FOLIO as Folio,
                    v.VENDIDO_EN as SaleDate,
                    v.TOTAL as Total,
                    v.SUBTOTAL as Subtotal,
                    v.IMPUESTOS as TaxAmount,
                    v.FORMA_PAGO as PaymentMethod,
                    v.CAJA_ID as TerminalId,
                    v.NOMBRE as CashierName,
                    v.NUMERO_ARTICULOS as NumberOfItems
                FROM VENTATICKETS v
                WHERE v.ID > @LastId
                AND v.VENDIDO_EN IS NOT NULL
                ORDER BY v.ID ASC
                """;

            await using var salesCmd = new FbCommand(salesQuery, connection);
            salesCmd.Parameters.AddWithValue("@LastId", lastProcessedSaleId);

            await using var reader = await salesCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var sale = new EleventaSale
                {
                    SaleId = reader.GetInt64(reader.GetOrdinal("SaleId")),
                    Folio = reader.GetInt32(reader.GetOrdinal("Folio")),
                    SaleDate = reader.GetDateTime(reader.GetOrdinal("SaleDate")),
                    Total = reader.GetDecimal(reader.GetOrdinal("Total")),
                    Subtotal = reader.GetDecimal(reader.GetOrdinal("Subtotal")),
                    TaxAmount = reader.GetDecimal(reader.GetOrdinal("TaxAmount")),
                    PaymentMethod = reader.IsDBNull(reader.GetOrdinal("PaymentMethod")) ? "" : reader.GetString(reader.GetOrdinal("PaymentMethod")),
                    TerminalId = reader.GetInt32(reader.GetOrdinal("TerminalId")),
                    CashierName = reader.IsDBNull(reader.GetOrdinal("CashierName")) ? "" : reader.GetString(reader.GetOrdinal("CashierName")),
                    NumberOfItems = reader.GetInt32(reader.GetOrdinal("NumberOfItems"))
                };

                sale.Items = await GetSaleItemsAsync(connection, sale.SaleId);
                sales.Add(sale);
                _log.LogSaleDetected(sale.SaleId, sale.Total);
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"Error reading Eleventa database", ex);
        }

        return sales;
    }

    /// <summary>
    /// Gets a specific sale by its ID from Eleventa (used for reactivating postponed sales)
    /// </summary>
    public async Task<EleventaSale?> GetSaleByIdAsync(long saleId)
    {
        try
        {
            await using var connection = new FbConnection(_connectionString);
            await connection.OpenAsync();

            const string query = """
                SELECT
                    v.ID as SaleId,
                    v.FOLIO as Folio,
                    v.VENDIDO_EN as SaleDate,
                    v.TOTAL as Total,
                    v.SUBTOTAL as Subtotal,
                    v.IMPUESTOS as TaxAmount,
                    v.FORMA_PAGO as PaymentMethod,
                    v.CAJA_ID as TerminalId,
                    v.NOMBRE as CashierName,
                    v.NUMERO_ARTICULOS as NumberOfItems
                FROM VENTATICKETS v
                WHERE v.ID = @SaleId
                """;

            await using var cmd = new FbCommand(query, connection);
            cmd.Parameters.AddWithValue("@SaleId", saleId);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var sale = new EleventaSale
                {
                    SaleId = reader.GetInt64(reader.GetOrdinal("SaleId")),
                    Folio = reader.GetInt32(reader.GetOrdinal("Folio")),
                    SaleDate = reader.GetDateTime(reader.GetOrdinal("SaleDate")),
                    Total = reader.GetDecimal(reader.GetOrdinal("Total")),
                    Subtotal = reader.GetDecimal(reader.GetOrdinal("Subtotal")),
                    TaxAmount = reader.GetDecimal(reader.GetOrdinal("TaxAmount")),
                    PaymentMethod = reader.IsDBNull(reader.GetOrdinal("PaymentMethod")) ? "" : reader.GetString(reader.GetOrdinal("PaymentMethod")),
                    TerminalId = reader.GetInt32(reader.GetOrdinal("TerminalId")),
                    CashierName = reader.IsDBNull(reader.GetOrdinal("CashierName")) ? "" : reader.GetString(reader.GetOrdinal("CashierName")),
                    NumberOfItems = reader.GetInt32(reader.GetOrdinal("NumberOfItems"))
                };

                sale.Items = await GetSaleItemsAsync(connection, sale.SaleId);
                return sale;
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"Error reading sale {saleId} from Eleventa", ex);
        }

        return null;
    }

    private async Task<List<EleventaSaleItem>> GetSaleItemsAsync(FbConnection connection, long saleId)
    {
        var items = new List<EleventaSaleItem>();

        // VENTATICKETS_ARTICULOS contains the line items for each sale
        // Column names verified from actual PDVDATA.FDB
        const string itemsQuery = """
            SELECT
                d.ID as ItemId,
                d.TICKET_ID as SaleId,
                d.PRODUCTO_CODIGO as ProductCode,
                d.PRODUCTO_NOMBRE as Description,
                d.CANTIDAD as Quantity,
                'UND' as UnitOfMeasure,
                d.PRECIO_USADO as UnitPrice,
                d.PORCENTAJE_DESCUENTO as Discount,
                d.TOTAL_ARTICULO as LineTotal,
                d.IMPUESTO_UNITARIO as TaxAmount
            FROM VENTATICKETS_ARTICULOS d
            WHERE d.TICKET_ID = @SaleId
            ORDER BY d.ID ASC
            """;

        await using var cmd = new FbCommand(itemsQuery, connection);
        cmd.Parameters.AddWithValue("@SaleId", saleId);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new EleventaSaleItem
            {
                ItemId = reader.GetInt64(reader.GetOrdinal("ItemId")),
                SaleId = reader.GetInt64(reader.GetOrdinal("SaleId")),
                ProductCode = reader.GetString(reader.GetOrdinal("ProductCode")),
                Description = reader.GetString(reader.GetOrdinal("Description")),
                Quantity = reader.GetDecimal(reader.GetOrdinal("Quantity")),
                UnitOfMeasure = reader.GetString(reader.GetOrdinal("UnitOfMeasure")),
                UnitPrice = reader.GetDecimal(reader.GetOrdinal("UnitPrice")),
                Discount = reader.GetDecimal(reader.GetOrdinal("Discount")),
                LineTotal = reader.GetDecimal(reader.GetOrdinal("LineTotal")),
                TaxAmount = reader.GetDecimal(reader.GetOrdinal("TaxAmount"))
            });
        }

        return items;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}

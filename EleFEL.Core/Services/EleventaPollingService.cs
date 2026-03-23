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
            // Database file: PDVDATA.FDB
            const string salesQuery = """
                SELECT
                    v.TICKET_ID as SaleId,
                    v.FECHA as SaleDate,
                    v.TOTAL as Total,
                    v.SUBTOTAL as Subtotal,
                    v.IMPUESTO1TOTAL as TaxAmount,
                    v.FORMA_PAGO as PaymentMethod,
                    v.CAJA_ID as TerminalId,
                    v.CAJERO_ID as CashierName
                FROM VENTATICKETS v
                WHERE v.TICKET_ID > @LastId
                AND v.ESTADO != 'CANCELADA'
                ORDER BY v.TICKET_ID ASC
                """;

            await using var salesCmd = new FbCommand(salesQuery, connection);
            salesCmd.Parameters.AddWithValue("@LastId", lastProcessedSaleId);

            await using var reader = await salesCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var sale = new EleventaSale
                {
                    SaleId = reader.GetInt64(reader.GetOrdinal("SaleId")),
                    SaleDate = reader.GetDateTime(reader.GetOrdinal("SaleDate")),
                    Total = reader.GetDecimal(reader.GetOrdinal("Total")),
                    Subtotal = reader.GetDecimal(reader.GetOrdinal("Subtotal")),
                    TaxAmount = reader.GetDecimal(reader.GetOrdinal("TaxAmount")),
                    PaymentMethod = reader.GetString(reader.GetOrdinal("PaymentMethod")),
                    TerminalId = reader.GetInt32(reader.GetOrdinal("TerminalId")),
                    CashierName = reader.GetString(reader.GetOrdinal("CashierName"))
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
                    v.TICKET_ID as SaleId,
                    v.FECHA as SaleDate,
                    v.TOTAL as Total,
                    v.SUBTOTAL as Subtotal,
                    v.IMPUESTO1TOTAL as TaxAmount,
                    v.FORMA_PAGO as PaymentMethod,
                    v.CAJA_ID as TerminalId,
                    v.CAJERO_ID as CashierName
                FROM VENTATICKETS v
                WHERE v.TICKET_ID = @SaleId
                """;

            await using var cmd = new FbCommand(query, connection);
            cmd.Parameters.AddWithValue("@SaleId", saleId);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var sale = new EleventaSale
                {
                    SaleId = reader.GetInt64(reader.GetOrdinal("SaleId")),
                    SaleDate = reader.GetDateTime(reader.GetOrdinal("SaleDate")),
                    Total = reader.GetDecimal(reader.GetOrdinal("Total")),
                    Subtotal = reader.GetDecimal(reader.GetOrdinal("Subtotal")),
                    TaxAmount = reader.GetDecimal(reader.GetOrdinal("TaxAmount")),
                    PaymentMethod = reader.GetString(reader.GetOrdinal("PaymentMethod")),
                    TerminalId = reader.GetInt32(reader.GetOrdinal("TerminalId")),
                    CashierName = reader.GetString(reader.GetOrdinal("CashierName"))
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
        const string itemsQuery = """
            SELECT
                d.ARTICULO_ID as ItemId,
                d.TICKET_ID as SaleId,
                d.CLAVE as ProductCode,
                d.DESCRIPCION as Description,
                d.CANTIDAD as Quantity,
                'UND' as UnitOfMeasure,
                d.PRECIO_USADO as UnitPrice,
                d.DESCUENTO as Discount,
                d.IMPORTE as LineTotal,
                d.IMPUESTO_MONTO as TaxAmount
            FROM VENTATICKETS_ARTICULOS d
            WHERE d.TICKET_ID = @SaleId
            ORDER BY d.ARTICULO_ID ASC
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

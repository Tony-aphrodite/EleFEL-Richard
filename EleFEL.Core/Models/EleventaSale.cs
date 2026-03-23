namespace EleFEL.Core.Models;

/// <summary>
/// Represents a sale record read from Eleventa's Firebird database
/// </summary>
public class EleventaSale
{
    public long SaleId { get; set; }          // VENTATICKETS.ID
    public int Folio { get; set; }            // VENTATICKETS.FOLIO
    public DateTime SaleDate { get; set; }    // VENTATICKETS.VENDIDO_EN
    public decimal Total { get; set; }        // VENTATICKETS.TOTAL
    public decimal Subtotal { get; set; }     // VENTATICKETS.SUBTOTAL
    public decimal TaxAmount { get; set; }    // VENTATICKETS.IMPUESTOS
    public string PaymentMethod { get; set; } = string.Empty; // VENTATICKETS.FORMA_PAGO
    public int TerminalId { get; set; }       // VENTATICKETS.CAJA_ID
    public string CashierName { get; set; } = string.Empty;   // VENTATICKETS.NOMBRE (cajero)
    public int NumberOfItems { get; set; }    // VENTATICKETS.NUMERO_ARTICULOS
    public List<EleventaSaleItem> Items { get; set; } = new();
}

/// <summary>
/// Represents a line item within a sale
/// </summary>
public class EleventaSaleItem
{
    public long ItemId { get; set; }          // VENTATICKETS_ARTICULOS.ID
    public long SaleId { get; set; }          // VENTATICKETS_ARTICULOS.TICKET_ID
    public string ProductCode { get; set; } = string.Empty;  // PRODUCTO_CODIGO
    public string Description { get; set; } = string.Empty;  // PRODUCTO_NOMBRE
    public decimal Quantity { get; set; }     // CANTIDAD
    public string UnitOfMeasure { get; set; } = "UND";
    public decimal UnitPrice { get; set; }    // PRECIO_USADO
    public decimal Discount { get; set; }     // PORCENTAJE_DESCUENTO
    public decimal LineTotal { get; set; }    // TOTAL_ARTICULO
    public decimal TaxAmount { get; set; }    // IMPUESTO_UNITARIO
}

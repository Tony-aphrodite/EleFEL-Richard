namespace EleFEL.Core.Models;

/// <summary>
/// Represents a sale record read from Eleventa's Firebird database
/// </summary>
public class EleventaSale
{
    public long SaleId { get; set; }
    public DateTime SaleDate { get; set; }
    public decimal Total { get; set; }
    public decimal Subtotal { get; set; }
    public decimal TaxAmount { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public string CashierName { get; set; } = string.Empty;
    public List<EleventaSaleItem> Items { get; set; } = new();
}

/// <summary>
/// Represents a line item within a sale
/// </summary>
public class EleventaSaleItem
{
    public long ItemId { get; set; }
    public long SaleId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string UnitOfMeasure { get; set; } = "UND";
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal LineTotal { get; set; }
    public decimal TaxAmount { get; set; }
}

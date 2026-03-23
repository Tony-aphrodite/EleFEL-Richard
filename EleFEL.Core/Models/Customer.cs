namespace EleFEL.Core.Models;

/// <summary>
/// Customer stored in local SQLite database for NIT auto-completion
/// </summary>
public class Customer
{
    public int Id { get; set; }
    public string Nit { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = "Ciudad";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LastUsedAt { get; set; } = DateTime.Now;

    public static Customer ConsumidorFinal => new()
    {
        Nit = "CF",
        Name = "Consumidor Final",
        Address = "Ciudad"
    };
}

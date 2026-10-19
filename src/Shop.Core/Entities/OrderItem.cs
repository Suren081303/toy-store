namespace Shop.Core.Entities;

// One product in an order, as it was when the customer bought it: later changes to the product do not change the order.
public class OrderItem
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public int ProductId { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Brand { get; set; } = string.Empty;

    // Name in the language of the order, and in Armenian (for the shop's staff).
    public string Name { get; set; } = string.Empty;

    public string NameHy { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public decimal RegularPrice { get; set; }

    // The price per item that the customer pays.
    public decimal UnitPrice { get; set; }

    public int DiscountPercent { get; set; }

    public bool CustomerDiscountApplied { get; set; }

    public decimal LineTotal => UnitPrice * Quantity;

    public Order Order { get; set; } = null!;

    public Product Product { get; set; } = null!;
}

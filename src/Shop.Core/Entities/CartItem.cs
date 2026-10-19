namespace Shop.Core.Entities;

// One product in a cart. Only the product and the quantity are stored: prices are always read from the product,
// so a discount change shows up in the cart at once.
public class CartItem
{
    public Guid CartId { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public Cart Cart { get; set; } = null!;

    public Product Product { get; set; } = null!;
}

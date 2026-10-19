namespace Shop.Core.Entities;

// A shopping cart. A guest's cart has no UserId and is found through a browser cookie that holds its Id.
// A signed-in customer has at most one cart, found by UserId.
public class Cart
{
    public Guid Id { get; set; }

    public string? UserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Last change. Guest carts that were not touched for a while are deleted.
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<CartItem> Items { get; set; } = new List<CartItem>();
}

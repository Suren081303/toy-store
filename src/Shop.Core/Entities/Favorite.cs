namespace Shop.Core.Entities;

public class Favorite
{
    public string UserId { get; set; } = string.Empty;

    public int ProductId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ApplicationUser User { get; set; } = null!;

    public Product Product { get; set; } = null!;
}

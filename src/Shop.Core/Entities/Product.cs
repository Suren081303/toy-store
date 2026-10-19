namespace Shop.Core.Entities;

public class Product
{
    public int Id { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Brand { get; set; } = string.Empty;

    public int CategoryId { get; set; }

    // Regular price in AMD, before the product discount.
    public decimal Price { get; set; }

    // 0-100
    public int DiscountPercent { get; set; }

    public int StockQuantity { get; set; }

    // Hidden from the shop but kept, because old orders refer to it.
    public bool IsArchived { get; set; }

    // Admin forces the product into "New arrivals" even when it is older than the new-arrival period.
    public bool IsPinnedAsNew { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Used later to stop two orders taking the last item at the same time.
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public Category Category { get; set; } = null!;

    public ICollection<ProductTranslation> Translations { get; set; } = new List<ProductTranslation>();

    public ICollection<ProductImage> Images { get; set; } = new List<ProductImage>();

    public ICollection<AgeGroup> AgeGroups { get; set; } = new List<AgeGroup>();
}

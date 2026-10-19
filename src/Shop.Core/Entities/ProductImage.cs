namespace Shop.Core.Entities;

public class ProductImage
{
    public int Id { get; set; }

    public int ProductId { get; set; }

    // Web path such as /images/placeholders/cat-edu.svg or /uploads/products/12/1.jpg
    public string Path { get; set; } = string.Empty;

    // The small version for product cards (null for placeholders: they are small already).
    public string? ThumbPath { get; set; }

    public int SortOrder { get; set; }

    public Product Product { get; set; } = null!;
}

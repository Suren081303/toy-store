namespace Shop.Core.Entities;

public class Category
{
    public int Id { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public string? ImagePath { get; set; }

    public ICollection<CategoryTranslation> Translations { get; set; } = new List<CategoryTranslation>();

    public ICollection<Product> Products { get; set; } = new List<Product>();
}

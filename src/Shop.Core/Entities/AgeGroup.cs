namespace Shop.Core.Entities;

public class AgeGroup
{
    public int Id { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<AgeGroupTranslation> Translations { get; set; } = new List<AgeGroupTranslation>();

    public ICollection<Product> Products { get; set; } = new List<Product>();
}

using Shop.Core.Localization;

namespace Shop.Core.Entities;

public class CategoryTranslation : ITranslation
{
    public int CategoryId { get; set; }

    public string Culture { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public Category Category { get; set; } = null!;
}

using Shop.Core.Localization;

namespace Shop.Core.Entities;

public class ProductTranslation : ITranslation
{
    public int ProductId { get; set; }

    public string Culture { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public Product Product { get; set; } = null!;
}

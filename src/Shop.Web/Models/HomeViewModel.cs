using Shop.Core.Catalog;

namespace Shop.Web.Models;

public class HomeViewModel
{
    public IReadOnlyList<CategoryItem> Categories { get; init; } = Array.Empty<CategoryItem>();

    public IReadOnlyList<AgeGroupItem> AgeGroups { get; init; } = Array.Empty<AgeGroupItem>();

    public IReadOnlyList<ProductListItem> NewArrivals { get; init; } = Array.Empty<ProductListItem>();
}

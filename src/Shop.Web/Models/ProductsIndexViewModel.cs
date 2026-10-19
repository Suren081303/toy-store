using Shop.Core.Catalog;

namespace Shop.Web.Models;

public class ProductsIndexViewModel
{
    public PagedResult<ProductListItem> Result { get; init; } = new();

    public IReadOnlyList<CategoryItem> Categories { get; init; } = Array.Empty<CategoryItem>();

    public IReadOnlyList<AgeGroupItem> AgeGroups { get; init; } = Array.Empty<AgeGroupItem>();

    public int? CategoryId { get; init; }

    public int? AgeGroupId { get; init; }

    public string? Search { get; init; }

    public bool OnlyNew { get; init; }

    public decimal? PriceFrom { get; init; }

    public decimal? PriceTo { get; init; }

    public ProductSort Sort { get; init; } = ProductSort.Recommended;

    // Number of filters set in the Filters panel (shown as a badge on the Filters button).
    public int ActiveFilterCount =>
        (CategoryId.HasValue ? 1 : 0) +
        (AgeGroupId.HasValue ? 1 : 0) +
        (OnlyNew ? 1 : 0) +
        (PriceFrom.HasValue || PriceTo.HasValue ? 1 : 0) +
        (Sort != ProductSort.Recommended ? 1 : 0);

    // Query-string values of the current filters, so a link can change one of them and keep the others.
    public Dictionary<string, string> Route(Action<Dictionary<string, string>>? modify = null)
    {
        var values = new Dictionary<string, string>();

        if (CategoryId is int categoryId)
        {
            values["category"] = categoryId.ToString();
        }

        if (AgeGroupId is int ageGroupId)
        {
            values["age"] = ageGroupId.ToString();
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            values["q"] = Search;
        }

        if (OnlyNew)
        {
            values["isNew"] = "true";
        }

        if (PriceFrom is decimal priceFrom)
        {
            values["priceFrom"] = priceFrom.ToString("0");
        }

        if (PriceTo is decimal priceTo)
        {
            values["priceTo"] = priceTo.ToString("0");
        }

        if (Sort != ProductSort.Recommended)
        {
            values["sort"] = Sort.ToString();
        }

        modify?.Invoke(values);
        return values;
    }
}

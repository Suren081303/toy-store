using Shop.Core.Catalog;

namespace Shop.Web.Models;

// The gift finder page: the questions with the visitor's answers, and (once they asked) the toys that fit.
public class GiftsViewModel
{
    public IReadOnlyList<AgeGroupItem> AgeGroups { get; init; } = Array.Empty<AgeGroupItem>();

    public IReadOnlyList<CategoryItem> Categories { get; init; } = Array.Empty<CategoryItem>();

    public int? AgeGroupId { get; init; }

    public IReadOnlyList<int> CategoryIds { get; init; } = Array.Empty<int>();

    public decimal? PriceFrom { get; init; }

    public decimal? PriceTo { get; init; }

    // False until the visitor presses the button (or arrives with answers in the address).
    public bool Searched { get; init; }

    public IReadOnlyList<ProductListItem> Results { get; init; } = Array.Empty<ProductListItem>();

    public bool HasAnswers => AgeGroupId.HasValue || CategoryIds.Count > 0 || PriceFrom.HasValue || PriceTo.HasValue;

    // The same answers without the budget, as a query string (for the "widen the budget" link).
    public string QueryWithoutPrice()
    {
        var parts = new List<string> { "find=true" };

        if (AgeGroupId is int age)
        {
            parts.Add("age=" + age);
        }

        parts.AddRange(CategoryIds.Select(id => "category=" + id));
        return string.Join('&', parts);
    }
}

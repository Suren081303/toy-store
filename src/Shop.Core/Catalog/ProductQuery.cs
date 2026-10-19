namespace Shop.Core.Catalog;

public enum ProductSort
{
    Recommended,
    PriceAsc,
    PriceDesc,
    Newest,

    // Most pieces sold in confirmed orders over the last 90 days first (used by the gift finder).
    BestSelling
}

public class ProductQuery
{
    public string Culture { get; set; } = Localization.Cultures.Default;

    public int? CategoryId { get; set; }

    // Several categories at once (the gift finder). Used together with CategoryId, a product must match both.
    public IReadOnlyCollection<int>? CategoryIds { get; set; }

    public int? AgeGroupId { get; set; }

    // Matches product name (current language and Armenian), brand and SKU.
    public string? Search { get; set; }

    public bool OnlyNew { get; set; }

    public bool OnlyInStock { get; set; }

    // Price after discount, in whole AMD, both ends included. Null = no limit. Use PriceRange.Normalize on user input.
    public decimal? PriceFrom { get; set; }

    public decimal? PriceTo { get; set; }

    public ProductSort Sort { get; set; } = ProductSort.Recommended;

    public int Page { get; set; } = 1;
}

public static class PriceRange
{
    // Prices above this are not meant: the box is ignored (it keeps the number inside the database's range).
    public const decimal Max = 1_000_000_000;

    // Cleans what a visitor typed into the "From" and "To" boxes: empty, zero, negative or silly values mean "no limit";
    // cents are rounded to whole AMD; if From is bigger than To they swap places.
    public static (decimal? From, decimal? To) Normalize(decimal? from, decimal? to)
    {
        var low = Clean(from);
        var high = Clean(to);

        if (low is { } a && high is { } b && a > b)
        {
            (low, high) = (high, low);
        }

        return (low, high);
    }

    private static decimal? Clean(decimal? value)
    {
        if (value is not { } v || v <= 0 || v > Max)
        {
            return null;
        }

        return Math.Round(v, 0, MidpointRounding.AwayFromZero);
    }
}

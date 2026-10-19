namespace Shop.Core.Catalog;

public class CatalogOptions
{
    public const string SectionName = "Catalog";

    // Products created within this many days are "new arrivals".
    public int NewArrivalDays { get; set; } = 30;

    public int PageSize { get; set; } = 12;
}

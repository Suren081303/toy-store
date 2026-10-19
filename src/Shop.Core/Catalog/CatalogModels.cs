namespace Shop.Core.Catalog;

public record CategoryItem(int Id, string Name, string? ImagePath);

public record AgeGroupItem(int Id, string Name);

// The pages of the shop that search engines should know: the active categories and the products that are for sale.
public record SitemapData(IReadOnlyList<int> CategoryIds, IReadOnlyList<int> ProductIds);

public record ProductListItem(
    int Id,
    string Name,
    string CategoryName,
    string Brand,
    decimal Price,
    decimal FinalPrice,
    int DiscountPercent,
    string? ImagePath,
    bool InStock,
    bool IsNew);

public record ProductDetails(
    int Id,
    string Sku,
    string Name,
    string Description,
    string Brand,
    int CategoryId,
    string CategoryName,
    decimal Price,
    decimal FinalPrice,
    int DiscountPercent,
    int StockQuantity,
    bool InStock,
    bool IsNew,
    IReadOnlyList<string> Images,
    IReadOnlyList<string> AgeGroups);

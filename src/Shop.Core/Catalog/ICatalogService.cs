namespace Shop.Core.Catalog;

public interface ICatalogService
{
    Task<IReadOnlyList<CategoryItem>> GetCategoriesAsync(string culture);

    Task<IReadOnlyList<AgeGroupItem>> GetAgeGroupsAsync(string culture);

    Task<PagedResult<ProductListItem>> GetProductsAsync(ProductQuery query);

    // Returns null when the product does not exist, is archived, or its category is inactive.
    Task<ProductDetails?> GetProductAsync(int id, string culture);

    Task<IReadOnlyList<ProductListItem>> GetNewArrivalsAsync(string culture, int count);

    // Ids of the active categories and of the products for sale (at most 15000, so the sitemap stays under the 50000-address limit in 3 languages).
    Task<SitemapData> GetSitemapDataAsync();

    // The first `count` products (one page) that match the query, in the query's order. Used by the gift finder.
    Task<IReadOnlyList<ProductListItem>> FindAsync(ProductQuery query, int count);

    // The products that are for sale, in the order of the ids given. Unknown, archived or hidden ones are left out.
    Task<IReadOnlyList<ProductListItem>> GetProductsByIdsAsync(IReadOnlyCollection<int> ids, string culture);
}

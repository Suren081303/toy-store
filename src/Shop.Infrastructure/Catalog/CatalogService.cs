using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shop.Core.Catalog;
using Shop.Core.Entities;
using Shop.Core.Localization;
using Shop.Core.Orders;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Catalog;

public class CatalogService : ICatalogService
{
    private readonly ShopDbContext _db;
    private readonly CatalogOptions _options;
    private readonly TimeProvider _time;

    public CatalogService(ShopDbContext db, IOptions<CatalogOptions> options, TimeProvider time)
    {
        _db = db;
        _options = options.Value;
        _time = time;
    }

    public async Task<IReadOnlyList<CategoryItem>> GetCategoriesAsync(string culture)
    {
        var categories = await _db.Categories
            .AsNoTracking()
            .Where(c => c.IsActive)
            .Include(c => c.Translations.Where(t => t.Culture == culture || t.Culture == Cultures.Default))
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .ToListAsync();

        return categories
            .Select(c => new CategoryItem(c.Id, c.Translations.ResolveName(culture), c.ImagePath))
            .ToList();
    }

    public async Task<IReadOnlyList<AgeGroupItem>> GetAgeGroupsAsync(string culture)
    {
        var ageGroups = await _db.AgeGroups
            .AsNoTracking()
            .Where(a => a.IsActive)
            .Include(a => a.Translations.Where(t => t.Culture == culture || t.Culture == Cultures.Default))
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id)
            .ToListAsync();

        return ageGroups
            .Select(a => new AgeGroupItem(a.Id, a.Translations.ResolveName(culture)))
            .ToList();
    }

    public Task<PagedResult<ProductListItem>> GetProductsAsync(ProductQuery query)
    {
        return GetProductsPageAsync(query, _options.PageSize);
    }

    public async Task<IReadOnlyList<ProductListItem>> GetNewArrivalsAsync(string culture, int count)
    {
        var query = new ProductQuery
        {
            Culture = culture,
            OnlyNew = true,
            Sort = ProductSort.Recommended,
            Page = 1
        };

        var result = await GetProductsPageAsync(query, count);
        return result.Items;
    }

    public async Task<SitemapData> GetSitemapDataAsync()
    {
        var categoryIds = await _db.Categories.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync();

        var productIds = await _db.Products.AsNoTracking()
            .Where(p => !p.IsArchived && p.Category.IsActive)
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .Take(15000)
            .ToListAsync();

        return new SitemapData(categoryIds, productIds);
    }

    public async Task<IReadOnlyList<ProductListItem>> FindAsync(ProductQuery query, int count)
    {
        var result = await GetProductsPageAsync(query, count);
        return result.Items;
    }

    public async Task<IReadOnlyList<ProductListItem>> GetProductsByIdsAsync(IReadOnlyCollection<int> ids, string culture)
    {
        if (ids.Count == 0)
        {
            return Array.Empty<ProductListItem>();
        }

        var idList = ids.ToList();
        var newSince = GetNewSince();

        var products = await _db.Products
            .AsNoTracking()
            .Where(p => idList.Contains(p.Id) && !p.IsArchived && p.Category.IsActive)
            .Include(p => p.Translations.Where(t => t.Culture == culture || t.Culture == Cultures.Default))
            .Include(p => p.Images.OrderBy(i => i.SortOrder).Take(1))
            .Include(p => p.Category).ThenInclude(c => c.Translations.Where(t => t.Culture == culture || t.Culture == Cultures.Default))
            .AsSplitQuery()
            .ToListAsync();

        var byId = products.ToDictionary(p => p.Id);

        return idList
            .Where(byId.ContainsKey)
            .Select(id => ToListItem(byId[id], culture, newSince))
            .ToList();
    }

    public async Task<ProductDetails?> GetProductAsync(int id, string culture)
    {
        var newSince = GetNewSince();

        var product = await _db.Products
            .AsNoTracking()
            .Include(p => p.Translations.Where(t => t.Culture == culture || t.Culture == Cultures.Default))
            .Include(p => p.Images.OrderBy(i => i.SortOrder))
            .Include(p => p.Category).ThenInclude(c => c.Translations.Where(t => t.Culture == culture || t.Culture == Cultures.Default))
            .Include(p => p.AgeGroups.Where(a => a.IsActive).OrderBy(a => a.SortOrder))
                .ThenInclude(a => a.Translations.Where(t => t.Culture == culture || t.Culture == Cultures.Default))
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsArchived && p.Category.IsActive);

        if (product is null)
        {
            return null;
        }

        return new ProductDetails(
            product.Id,
            product.Sku,
            product.Translations.ResolveName(culture),
            product.Translations.ResolveDescription(culture),
            product.Brand,
            product.CategoryId,
            product.Category.Translations.ResolveName(culture),
            product.Price,
            ProductRules.GetFinalPrice(product.Price, product.DiscountPercent),
            product.DiscountPercent,
            product.StockQuantity,
            product.StockQuantity > 0,
            ProductRules.IsNew(product.IsPinnedAsNew, product.CreatedAt, newSince),
            product.Images.Select(i => i.Path).ToList(),
            product.AgeGroups.Select(a => a.Translations.ResolveName(culture)).ToList());
    }

    private async Task<PagedResult<ProductListItem>> GetProductsPageAsync(ProductQuery query, int pageSize)
    {
        var culture = query.Culture;
        var newSince = GetNewSince();

        var products = _db.Products
            .AsNoTracking()
            .Where(p => !p.IsArchived && p.Category.IsActive);

        if (query.CategoryId is int categoryId)
        {
            products = products.Where(p => p.CategoryId == categoryId);
        }

        if (query.CategoryIds is { Count: > 0 } categoryIds)
        {
            var ids = categoryIds.ToList();
            products = products.Where(p => ids.Contains(p.CategoryId));
        }

        if (query.AgeGroupId is int ageGroupId)
        {
            products = products.Where(p => p.AgeGroups.Any(a => a.Id == ageGroupId));
        }

        if (query.OnlyInStock)
        {
            products = products.Where(p => p.StockQuantity > 0);
        }

        // The final price is round(price * (100 - discount) / 100), halves up. So final >= From means price * (100 - discount) >= From * 100 - 50,
        // and final <= To means price * (100 - discount) < To * 100 + 50. This keeps the filter equal to the price shown on the card.
        if (query.PriceFrom is decimal from)
        {
            var limit = from * 100 - 50;
            products = products.Where(p => p.Price * (100 - p.DiscountPercent) >= limit);
        }

        if (query.PriceTo is decimal to)
        {
            var limit = to * 100 + 50;
            products = products.Where(p => p.Price * (100 - p.DiscountPercent) < limit);
        }

        if (query.OnlyNew)
        {
            products = products.Where(p => p.IsPinnedAsNew || p.CreatedAt >= newSince);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            products = products.Where(p =>
                p.Sku.Contains(term) ||
                p.Brand.Contains(term) ||
                p.Translations.Any(t => (t.Culture == culture || t.Culture == Cultures.Default) && t.Name.Contains(term)));
        }

        var totalCount = await products.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        var page = Math.Clamp(query.Page, 1, Math.Max(1, totalPages));

        var items = await ApplySort(products, query.Sort, _time.GetUtcNow().UtcDateTime.AddDays(-BestSellingDays))
            .Include(p => p.Translations.Where(t => t.Culture == culture || t.Culture == Cultures.Default))
            .Include(p => p.Images.OrderBy(i => i.SortOrder).Take(1))
            .Include(p => p.Category).ThenInclude(c => c.Translations.Where(t => t.Culture == culture || t.Culture == Cultures.Default))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync();

        return new PagedResult<ProductListItem>
        {
            Items = items.Select(p => ToListItem(p, culture, newSince)).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    // "Best selling" looks at this many days of confirmed orders.
    private const int BestSellingDays = 90;

    // Price sorting uses the final price (price after discount).
    private IOrderedQueryable<Product> ApplySort(IQueryable<Product> products, ProductSort sort, DateTime soldSince)
    {
        return sort switch
        {
            // Pieces in confirmed orders (ordered, packed, delivered) since soldSince; products nobody bought come last, newest first.
            ProductSort.BestSelling => products
                .OrderByDescending(p => _db.OrderItems
                    .Where(i => i.ProductId == p.Id && i.Order.CreatedAt >= soldSince
                        && (i.Order.Status == OrderStatus.Ordered || i.Order.Status == OrderStatus.Packed || i.Order.Status == OrderStatus.Delivered))
                    .Sum(i => (int?)i.Quantity) ?? 0)
                .ThenByDescending(p => p.CreatedAt)
                .ThenBy(p => p.Id),
            ProductSort.PriceAsc => products.OrderBy(p => p.Price * (100 - p.DiscountPercent)).ThenBy(p => p.Id),
            ProductSort.PriceDesc => products.OrderByDescending(p => p.Price * (100 - p.DiscountPercent)).ThenBy(p => p.Id),
            ProductSort.Newest => products.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id),
            _ => products
                .OrderByDescending(p => p.StockQuantity > 0)
                .ThenByDescending(p => p.IsPinnedAsNew)
                .ThenByDescending(p => p.CreatedAt)
                .ThenBy(p => p.Id)
        };
    }

    private static ProductListItem ToListItem(Product p, string culture, DateTime newSince)
    {
        return new ProductListItem(
            p.Id,
            p.Translations.ResolveName(culture),
            p.Category.Translations.ResolveName(culture),
            p.Brand,
            p.Price,
            ProductRules.GetFinalPrice(p.Price, p.DiscountPercent),
            p.DiscountPercent,
            p.Images.FirstOrDefault() is { } photo ? photo.ThumbPath ?? photo.Path : null,
            p.StockQuantity > 0,
            ProductRules.IsNew(p.IsPinnedAsNew, p.CreatedAt, newSince));
    }

    private DateTime GetNewSince()
    {
        return _time.GetUtcNow().UtcDateTime.AddDays(-_options.NewArrivalDays);
    }
}

using Microsoft.EntityFrameworkCore;
using Shop.Core.Catalog;
using Shop.Core.Entities;
using Shop.Core.Favorites;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Favorites;

public class FavoriteService : IFavoriteService
{
    private readonly ShopDbContext _db;
    private readonly ICatalogService _catalog;

    public FavoriteService(ShopDbContext db, ICatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<bool?> ToggleAsync(string userId, int productId)
    {
        var existing = await _db.Favorites.FindAsync(userId, productId);
        if (existing is not null)
        {
            _db.Favorites.Remove(existing);
            await _db.SaveChangesAsync();
            return false;
        }

        var forSale = await _db.Products.AnyAsync(p => p.Id == productId && !p.IsArchived && p.Category.IsActive);
        if (!forSale)
        {
            return null;
        }

        _db.Favorites.Add(new Favorite { UserId = userId, ProductId = productId, CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> IsForSaleAsync(int productId)
    {
        return await _db.Products.AnyAsync(p => p.Id == productId && !p.IsArchived && p.Category.IsActive);
    }

    public async Task<int> AddManyAsync(string userId, IEnumerable<int> productIds)
    {
        var wanted = productIds.Distinct().ToList();
        if (wanted.Count == 0)
        {
            return 0;
        }

        var forSale = await _db.Products
            .Where(p => wanted.Contains(p.Id) && !p.IsArchived && p.Category.IsActive)
            .Select(p => p.Id)
            .ToListAsync();

        var already = await _db.Favorites
            .Where(f => f.UserId == userId && wanted.Contains(f.ProductId))
            .Select(f => f.ProductId)
            .ToListAsync();

        var toAdd = forSale.Except(already).ToList();
        foreach (var productId in toAdd)
        {
            _db.Favorites.Add(new Favorite { UserId = userId, ProductId = productId, CreatedAt = DateTime.UtcNow });
        }

        await _db.SaveChangesAsync();
        return toAdd.Count;
    }

    public async Task<HashSet<int>> GetIdsAsync(string userId)
    {
        var ids = await _db.Favorites
            .AsNoTracking()
            .Where(f => f.UserId == userId)
            .Select(f => f.ProductId)
            .ToListAsync();

        return ids.ToHashSet();
    }

    public async Task<IReadOnlyList<ProductListItem>> GetProductsAsync(string userId, string culture)
    {
        var ids = await _db.Favorites
            .AsNoTracking()
            .Where(f => f.UserId == userId)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => f.ProductId)
            .ToListAsync();

        return await _catalog.GetProductsByIdsAsync(ids, culture);
    }
}

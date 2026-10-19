using Shop.Core.Entities;

namespace Shop.Tests;

[Collection("Catalog")]
public class FavoriteServiceTests
{
    private readonly CatalogFixture _fx;

    public FavoriteServiceTests(CatalogFixture fx)
    {
        _fx = fx;
    }

    [Fact]
    public async Task Toggle_AddsThenRemovesTheProduct()
    {
        var userId = await _fx.CreateUserAsync();
        var productId = _fx.ProductId("SKU-001");

        Assert.True(await _fx.CreateFavoriteService().ToggleAsync(userId, productId));
        Assert.Contains(productId, await _fx.CreateFavoriteService().GetIdsAsync(userId));

        Assert.False(await _fx.CreateFavoriteService().ToggleAsync(userId, productId));
        Assert.Empty(await _fx.CreateFavoriteService().GetIdsAsync(userId));
    }

    [Fact]
    public async Task Toggle_UnknownOrArchivedProduct_ReturnsNull()
    {
        var userId = await _fx.CreateUserAsync();

        Assert.Null(await _fx.CreateFavoriteService().ToggleAsync(userId, int.MaxValue));
        Assert.Null(await _fx.CreateFavoriteService().ToggleAsync(userId, _fx.ProductId("SKU-005")));      // archived
        Assert.Null(await _fx.CreateFavoriteService().ToggleAsync(userId, _fx.ProductId("SKU-006")));      // inactive category
        Assert.Empty(await _fx.CreateFavoriteService().GetIdsAsync(userId));
    }

    [Fact]
    public async Task Favorites_AreKeptPerUser()
    {
        var first = await _fx.CreateUserAsync();
        var second = await _fx.CreateUserAsync();

        await _fx.CreateFavoriteService().ToggleAsync(first, _fx.ProductId("SKU-001"));
        await _fx.CreateFavoriteService().ToggleAsync(second, _fx.ProductId("SKU-002"));

        Assert.Equal(new[] { _fx.ProductId("SKU-001") }, (await _fx.CreateFavoriteService().GetIdsAsync(first)).ToArray());
        Assert.Equal(new[] { _fx.ProductId("SKU-002") }, (await _fx.CreateFavoriteService().GetIdsAsync(second)).ToArray());
    }

    [Fact]
    public async Task IsForSale_IsTrueOnlyForProductsThatCanBeBought()
    {
        var service = _fx.CreateFavoriteService();

        Assert.True(await service.IsForSaleAsync(_fx.ProductId("SKU-001")));
        Assert.False(await service.IsForSaleAsync(_fx.ProductId("SKU-005")));   // archived
        Assert.False(await service.IsForSaleAsync(_fx.ProductId("SKU-006")));   // inactive category
        Assert.False(await service.IsForSaleAsync(int.MaxValue));
    }

    [Fact]
    public async Task AddMany_AddsOnlyNewProductsThatAreForSale()
    {
        var userId = await _fx.CreateUserAsync();
        await _fx.CreateFavoriteService().ToggleAsync(userId, _fx.ProductId("SKU-001"));

        var added = await _fx.CreateFavoriteService().AddManyAsync(userId, new[]
        {
            _fx.ProductId("SKU-001"),   // already saved
            _fx.ProductId("SKU-002"),
            _fx.ProductId("SKU-005"),   // archived
            int.MaxValue,               // unknown
            _fx.ProductId("SKU-002")    // repeated
        });

        Assert.Equal(1, added);
        var ids = await _fx.CreateFavoriteService().GetIdsAsync(userId);
        Assert.Equal(
            new[] { _fx.ProductId("SKU-001"), _fx.ProductId("SKU-002") }.Order(),
            ids.Order());
    }

    [Fact]
    public async Task AddMany_WithNothing_DoesNothing()
    {
        var userId = await _fx.CreateUserAsync();

        Assert.Equal(0, await _fx.CreateFavoriteService().AddManyAsync(userId, Array.Empty<int>()));
    }

    [Fact]
    public async Task GetProducts_ListsNewestFirst_AndHidesProductsNoLongerForSale()
    {
        var userId = await _fx.CreateUserAsync();

        await _fx.CreateFavoriteService().ToggleAsync(userId, _fx.ProductId("SKU-001"));
        await Task.Delay(30);
        await _fx.CreateFavoriteService().ToggleAsync(userId, _fx.ProductId("SKU-002"));
        await Task.Delay(30);
        await _fx.CreateFavoriteService().ToggleAsync(userId, _fx.ProductId("SKU-003"));

        // A favorite whose product was archived later (added directly, because the service refuses archived products).
        using (var db = _fx.CreateDbContext())
        {
            db.Favorites.Add(new Favorite { UserId = userId, ProductId = _fx.ProductId("SKU-005"), CreatedAt = DateTime.UtcNow.AddMinutes(1) });
            await db.SaveChangesAsync();
        }

        var products = await _fx.CreateFavoriteService().GetProductsAsync(userId, "en");

        Assert.Equal(
            new[] { _fx.ProductId("SKU-003"), _fx.ProductId("SKU-002"), _fx.ProductId("SKU-001") },
            products.Select(p => p.Id).ToArray());
    }
}

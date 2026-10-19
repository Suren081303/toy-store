using Microsoft.EntityFrameworkCore;
using Shop.Core.Carts;
using Shop.Core.Entities;
using Shop.Infrastructure.Carts;
using Shop.Infrastructure.Data;

namespace Shop.Tests;

[CollectionDefinition("Cart")]
public class CartCollection : ICollectionFixture<CartFixture>
{
}

// The cart tests change products and carts freely, so they get their own database (ShopDb_CartTests).
// Each test creates the products and users it needs.
public class CartFixture : IAsyncLifetime
{
    private const string ConnectionString =
        "Server=.\\SQLEXPRESS;Database=ShopDb_CartTests;Trusted_Connection=True;TrustServerCertificate=True";

    public static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly DbContextOptions<ShopDbContext> _options = new DbContextOptionsBuilder<ShopDbContext>()
        .UseSqlServer(ConnectionString)
        .Options;

    private int _categoryId;
    private int _inactiveCategoryId;

    public async Task InitializeAsync()
    {
        using var db = new ShopDbContext(_options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        var category = NewCategory(isActive: true);
        var inactive = NewCategory(isActive: false);
        db.Categories.AddRange(category, inactive);
        await db.SaveChangesAsync();

        _categoryId = category.Id;
        _inactiveCategoryId = inactive.Id;
    }

    public async Task DisposeAsync()
    {
        using var db = new ShopDbContext(_options);
        await db.Database.EnsureDeletedAsync();
    }

    public ShopDbContext CreateDbContext() => new(_options);

    public CartService CreateService(int maxQuantity = 10, int maxLines = 30)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CartOptions
        {
            MaxQuantityPerProduct = maxQuantity,
            MaxLines = maxLines
        });

        return new CartService(new ShopDbContext(_options), options, new FixedTimeProvider(Now));
    }

    public async Task<string> CreateUserAsync()
    {
        using var db = CreateDbContext();
        var user = new ApplicationUser
        {
            UserName = $"{Guid.NewGuid():N}@example.com",
            Email = $"{Guid.NewGuid():N}@example.com",
            FullName = "Test User"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    public async Task<int> CreateProductAsync(
        int stock = 10, decimal price = 10000, int discountPercent = 0, bool archived = false, bool inactiveCategory = false)
    {
        using var db = CreateDbContext();

        var product = new Product
        {
            Sku = "SKU-" + Guid.NewGuid().ToString("N")[..10],
            Brand = "Brand",
            CategoryId = inactiveCategory ? _inactiveCategoryId : _categoryId,
            Price = price,
            DiscountPercent = discountPercent,
            StockQuantity = stock,
            IsArchived = archived,
            CreatedAt = Now
        };
        product.Translations.Add(new ProductTranslation { Culture = "hy", Name = "Խաղ " + product.Sku, Description = "d" });
        product.Translations.Add(new ProductTranslation { Culture = "en", Name = "Toy " + product.Sku, Description = "d" });
        db.Products.Add(product);
        await db.SaveChangesAsync();

        return product.Id;
    }

    public async Task SetProductAsync(int productId, int? stock = null, bool? archived = null)
    {
        using var db = CreateDbContext();
        var product = await db.Products.FirstAsync(p => p.Id == productId);

        if (stock is not null)
        {
            product.StockQuantity = stock.Value;
        }

        if (archived is not null)
        {
            product.IsArchived = archived.Value;
        }

        await db.SaveChangesAsync();
    }

    private static Category NewCategory(bool isActive)
    {
        var category = new Category { IsActive = isActive };
        category.Translations.Add(new CategoryTranslation { Culture = "hy", Name = "Կատեգորիա" });
        category.Translations.Add(new CategoryTranslation { Culture = "en", Name = "Category" });
        return category;
    }
}

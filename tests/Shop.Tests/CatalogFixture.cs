using Microsoft.EntityFrameworkCore;
using Shop.Core.Catalog;
using Shop.Core.Entities;
using Shop.Infrastructure.Catalog;
using Shop.Infrastructure.Data;

namespace Shop.Tests;

// One shared fixture for every test class that uses the test database (they run one after another, never in parallel).
[CollectionDefinition("Catalog")]
public class CatalogCollection : ICollectionFixture<CatalogFixture>
{
}

// Creates a separate SQL Server database (ShopDb_Tests) with the real migrations and a small known dataset.
public class CatalogFixture : IAsyncLifetime
{
    private const string ConnectionString =
        "Server=.\\SQLEXPRESS;Database=ShopDb_Tests;Trusted_Connection=True;TrustServerCertificate=True";

    public static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly DbContextOptions<ShopDbContext> _options = new DbContextOptionsBuilder<ShopDbContext>()
        .UseSqlServer(ConnectionString)
        .Options;

    private readonly Dictionary<string, int> _productIds = new();

    public int InactiveAgeGroupId { get; private set; }

    public int CategoryAId { get; private set; }

    public int CategoryBId { get; private set; }

    public int AgeGroup1Id { get; private set; }

    public int AgeGroup2Id { get; private set; }

    public int ProductId(string sku) => _productIds[sku];

    // A new customer row, so each favorites test works with its own user.
    public async Task<string> CreateUserAsync()
    {
        using var db = new ShopDbContext(_options);
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

    public ShopDbContext CreateDbContext() => new(_options);

    public Shop.Infrastructure.Favorites.FavoriteService CreateFavoriteService()
    {
        var db = new ShopDbContext(_options);
        var options = Microsoft.Extensions.Options.Options.Create(new CatalogOptions());
        var catalog = new CatalogService(db, options, new FixedTimeProvider(Now));
        return new Shop.Infrastructure.Favorites.FavoriteService(db, catalog);
    }

    public CatalogService CreateService(int pageSize = 10, int newArrivalDays = 30)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CatalogOptions
        {
            PageSize = pageSize,
            NewArrivalDays = newArrivalDays
        });

        return new CatalogService(new ShopDbContext(_options), options, new FixedTimeProvider(Now));
    }

    public async Task InitializeAsync()
    {
        using var db = new ShopDbContext(_options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        var ageGroup1 = new AgeGroup { SortOrder = 1 };
        ageGroup1.Translations.Add(new AgeGroupTranslation { Culture = "hy", Name = "0–2 տ." });
        ageGroup1.Translations.Add(new AgeGroupTranslation { Culture = "ru", Name = "0–2 лет" });
        ageGroup1.Translations.Add(new AgeGroupTranslation { Culture = "en", Name = "0–2 yrs" });

        var ageGroup2 = new AgeGroup { SortOrder = 2 };
        ageGroup2.Translations.Add(new AgeGroupTranslation { Culture = "hy", Name = "3–5 տ." });
        ageGroup2.Translations.Add(new AgeGroupTranslation { Culture = "ru", Name = "3–5 лет" });
        ageGroup2.Translations.Add(new AgeGroupTranslation { Culture = "en", Name = "3–5 yrs" });

        var inactiveAgeGroup = new AgeGroup { SortOrder = 3, IsActive = false };
        inactiveAgeGroup.Translations.Add(new AgeGroupTranslation { Culture = "hy", Name = "Թաքնված" });

        var categoryA = new Category { SortOrder = 1 };
        categoryA.Translations.Add(new CategoryTranslation { Culture = "hy", Name = "Խաղ Ա" });
        categoryA.Translations.Add(new CategoryTranslation { Culture = "ru", Name = "Игра А" });
        categoryA.Translations.Add(new CategoryTranslation { Culture = "en", Name = "Toys A" });

        // Armenian only, so ru/en fall back to Armenian.
        var categoryB = new Category { SortOrder = 2 };
        categoryB.Translations.Add(new CategoryTranslation { Culture = "hy", Name = "Խաղ Բ" });

        var hiddenCategory = new Category { SortOrder = 3, IsActive = false };
        hiddenCategory.Translations.Add(new CategoryTranslation { Culture = "hy", Name = "Թաքնված կատեգորիա" });

        var p1 = NewProduct("SKU-001", "Alpha", categoryA, 10000, 0, 5, Now.AddDays(-5), "Արջ", "Мишка", "Bear");
        p1.AgeGroups.Add(ageGroup1);
        p1.Images.Add(new ProductImage { Path = "/img/p1-second.svg", SortOrder = 2 });
        p1.Images.Add(new ProductImage { Path = "/img/p1-first.svg", SortOrder = 1 });

        // Out of stock; discounted price (10000) equals P1's price.
        var p2 = NewProduct("SKU-002", "Beta", categoryA, 20000, 50, 0, Now.AddDays(-10), "Մեքենա", "Машина", "Car");
        p2.AgeGroups.Add(ageGroup1);
        p2.AgeGroups.Add(ageGroup2);

        // Old, but pinned as new.
        var p3 = NewProduct("SKU-003", "Alpha", categoryA, 15000, 10, 3, Now.AddDays(-60), "Տիկնիկ", "Кукла", "Doll");
        p3.IsPinnedAsNew = true;
        p3.AgeGroups.Add(ageGroup2);

        // Old, not pinned, Armenian only.
        var p4 = NewProduct("SKU-004", "Gamma", categoryB, 5000, 0, 8, Now.AddDays(-40), "Գնդակ", null, null);
        p4.AgeGroups.Add(ageGroup2);

        var p5Archived = NewProduct("SKU-005", "Alpha", categoryA, 9000, 0, 5, Now.AddDays(-2), "Արխիվ", "Архив", "Archived");
        p5Archived.IsArchived = true;

        var p6HiddenCategory = NewProduct("SKU-006", "Alpha", hiddenCategory, 9000, 0, 5, Now.AddDays(-2), "Թաքնված", "Скрытый", "Hidden");

        var p7 = NewProduct("SKU-007", "Alpha", categoryB, 7000, 0, 1, Now.AddDays(-1), "Կառուցող", "Конструктор", "Blocks");

        var products = new[] { p1, p2, p3, p4, p5Archived, p6HiddenCategory, p7 };

        db.AgeGroups.AddRange(ageGroup1, ageGroup2, inactiveAgeGroup);
        db.Categories.AddRange(categoryA, categoryB, hiddenCategory);
        db.Products.AddRange(products);
        await db.SaveChangesAsync();

        foreach (var product in products)
        {
            _productIds[product.Sku] = product.Id;
        }

        CategoryAId = categoryA.Id;
        CategoryBId = categoryB.Id;
        AgeGroup1Id = ageGroup1.Id;
        AgeGroup2Id = ageGroup2.Id;
        InactiveAgeGroupId = inactiveAgeGroup.Id;
    }

    public async Task DisposeAsync()
    {
        using var db = new ShopDbContext(_options);
        await db.Database.EnsureDeletedAsync();
    }

    private static Product NewProduct(
        string sku, string brand, Category category, decimal price, int discount, int stock,
        DateTime createdAt, string? nameHy, string? nameRu, string? nameEn)
    {
        var product = new Product
        {
            Sku = sku,
            Brand = brand,
            Category = category,
            Price = price,
            DiscountPercent = discount,
            StockQuantity = stock,
            CreatedAt = createdAt
        };

        if (nameHy is not null)
        {
            product.Translations.Add(new ProductTranslation { Culture = "hy", Name = nameHy, Description = "Նկարագրություն " + sku });
        }

        if (nameRu is not null)
        {
            product.Translations.Add(new ProductTranslation { Culture = "ru", Name = nameRu, Description = "Описание " + sku });
        }

        if (nameEn is not null)
        {
            product.Translations.Add(new ProductTranslation { Culture = "en", Name = nameEn, Description = "Description " + sku });
        }

        return product;
    }
}

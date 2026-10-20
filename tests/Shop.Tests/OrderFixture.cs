using Microsoft.EntityFrameworkCore;
using Shop.Core.Carts;
using Shop.Core.Entities;
using Shop.Core.Orders;
using Shop.Core.Payments;
using Shop.Infrastructure.Admin;
using Shop.Infrastructure.Carts;
using Shop.Infrastructure.Data;
using Shop.Infrastructure.Orders;
using Shop.Infrastructure.Payments;

namespace Shop.Tests;

[CollectionDefinition("Orders")]
public class OrdersCollection : ICollectionFixture<OrderFixture>
{
}

// The order tests get their own database (ShopDb_OrderTests). Each test creates the products and users it needs.
public class OrderFixture : IAsyncLifetime
{
    private const string ConnectionString =
        "Server=.\\SQLEXPRESS;Database=ShopDb_OrderTests;Trusted_Connection=True;TrustServerCertificate=True";

    // 16:00 in Armenia on June 1, so "today" is June 1 and the first delivery day is June 2.
    public static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    public static readonly DateOnly FirstDeliveryDate = new(2026, 6, 2);

    private readonly DbContextOptions<ShopDbContext> _options = new DbContextOptionsBuilder<ShopDbContext>()
        .UseSqlServer(ConnectionString)
        .Options;

    private int _categoryId;

    public int SlotId { get; private set; }

    public async Task InitializeAsync()
    {
        using var db = new ShopDbContext(_options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        var category = new Category { IsActive = true };
        category.Translations.Add(new CategoryTranslation { Culture = "hy", Name = "Կատեգորիա" });
        category.Translations.Add(new CategoryTranslation { Culture = "en", Name = "Category" });
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        _categoryId = category.Id;

        await StoreSettingsSeeder.SeedAsync(db);
        SlotId = await db.DeliverySlots.OrderBy(s => s.SortOrder).Select(s => s.Id).FirstAsync();
    }

    public async Task DisposeAsync()
    {
        using var db = new ShopDbContext(_options);
        await db.Database.EnsureDeletedAsync();
    }

    public ShopDbContext CreateDbContext() => new(_options);

    // The cart, order and payment services on one database connection, like they work during a web request.
    // withCardProvider: false builds a payment service without a bank (card payment off).
    public (CartService Cart, OrderService Orders, PaymentService Payments) CreateServices(DateTime? now = null, bool withCardProvider = true)
    {
        var time = new FixedTimeProvider(now ?? Now);
        var db = new ShopDbContext(_options);
        var cart = new CartService(
            db,
            Microsoft.Extensions.Options.Options.Create(new CartOptions { MaxQuantityPerProduct = 10, MaxLines = 30 }),
            time);
        var orders = new OrderService(db, cart, new StoreSettingsService(db), time);
        var payments = new PaymentService(
            db,
            orders,
            Microsoft.Extensions.Options.Options.Create(new PaymentOptions { CardTimeoutMinutes = 30, BankTransferTimeoutDays = 3 }),
            time,
            withCardProvider ? new StubPaymentProvider() : null);

        return (cart, orders, payments);
    }

    // The admin services, on one database connection.
    public (AdminOrderService Orders, AdminReturnService Returns, AdminSettingsService Settings) CreateAdminServices(DateTime? now = null)
    {
        var time = new FixedTimeProvider(now ?? Now);
        var db = new ShopDbContext(_options);
        var cart = new CartService(
            db,
            Microsoft.Extensions.Options.Options.Create(new CartOptions { MaxQuantityPerProduct = 10, MaxLines = 30 }),
            time);
        var orders = new OrderService(db, cart, new StoreSettingsService(db), time);

        return (new AdminOrderService(db, orders, new StoreSettingsService(db), time), new AdminReturnService(db, orders, time), new AdminSettingsService(db, time));
    }

    // The catalog admin services, on one database connection.
    public (AdminCategoryService Categories, AdminAgeGroupService AgeGroups, AdminProductService Products) CreateCatalogAdminServices(DateTime? now = null)
    {
        var time = new FixedTimeProvider(now ?? Now);
        var db = new ShopDbContext(_options);

        return (new AdminCategoryService(db, time), new AdminAgeGroupService(db, time), new AdminProductService(db, time));
    }

    // The catalog tools (duplicate, bulk change, CSV export and import), on their own database connection.
    public AdminProductToolsService CreateProductToolsService(DateTime? now = null) => new(CreateDbContext(), new FixedTimeProvider(now ?? Now));

    // The admin dashboard service, with its own order service.
    public AdminDashboardService CreateDashboardService(DateTime? now = null) =>
        new(CreateDbContext(), CreateAdminServices(now).Orders, new FixedTimeProvider(now ?? Now));

    // The customer admin service (list, block, personal discount, messages), on its own database connection.
    public AdminCustomerService CreateCustomerService(DateTime? now = null) => new(CreateDbContext(), new FixedTimeProvider(now ?? Now));

    public PaymentService CreatePaymentService(DateTime? now = null, bool withCardProvider = true) => CreateServices(now, withCardProvider).Payments;

    public OrderService CreateOrderService(DateTime? now = null) => CreateServices(now).Orders;

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

    public async Task<int> CreateProductAsync(int stock = 10, decimal price = 10000, int discountPercent = 0, bool archived = false)
    {
        using var db = CreateDbContext();

        var product = new Product
        {
            Sku = "SKU-" + Guid.NewGuid().ToString("N")[..10],
            Brand = "Brand",
            CategoryId = _categoryId,
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

    public async Task<int> GetStockAsync(int productId)
    {
        using var db = CreateDbContext();
        return await db.Products.Where(p => p.Id == productId).Select(p => p.StockQuantity).FirstAsync();
    }

    public async Task SetStockAsync(int productId, int stock)
    {
        using var db = CreateDbContext();
        await db.Products.Where(p => p.Id == productId).ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, stock));
    }

    public async Task ArchiveAsync(int productId)
    {
        using var db = CreateDbContext();
        await db.Products.Where(p => p.Id == productId).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsArchived, true));
    }

    // Puts products into a cart (a guest cart, or the user's cart) and returns who owns it.
    public async Task<CartOwner> FillCartAsync(string? userId, params (int ProductId, int Quantity)[] items)
    {
        var cart = CreateServices().Cart;
        var owner = new CartOwner(userId, null);

        foreach (var (productId, quantity) in items)
        {
            var result = await cart.AddAsync(owner, productId, quantity);
            owner = userId is not null ? owner : new CartOwner(null, result.NewGuestCartId ?? owner.GuestCartId);
        }

        return owner;
    }

    // The checkout form data the customer would send, with the total they saw. Pass the total, or leave it to be filled from the cart.
    public async Task<CheckoutInput> InputAsync(
        CartOwner owner, PaymentMethod method = PaymentMethod.CashOnDelivery, decimal? expectedTotal = null)
    {
        var cart = await CreateServices().Cart.GetAsync(owner, "en");
        var settings = await new StoreSettingsService(CreateDbContext()).GetAsync();
        var fee = DeliveryRules.GetFee(cart.Total, settings.DeliveryFee, settings.FreeDeliveryFrom);

        return new CheckoutInput
        {
            CheckoutId = Guid.NewGuid(),
            CustomerName = "Anna Hakobyan",
            Email = $"{Guid.NewGuid():N}@example.com",
            Phone = "+37491123456",
            Region = "yerevan",
            City = "Yerevan",
            Street = "Abovyan",
            Building = "12",
            Apartment = "5",
            DeliveryDate = FirstDeliveryDate,
            DeliverySlotId = SlotId,
            PaymentMethod = method,
            ExpectedTotal = expectedTotal ?? cart.Total + fee
        };
    }

    // Places an order for a new guest cart with one product (price 10000 + delivery 1500 = 11500 for one piece).
    public async Task<OrderSummary> PlaceOrderAsync(
        string? userId = null, PaymentMethod method = PaymentMethod.CashOnDelivery, int quantity = 1, int stock = 10)
    {
        var productId = await CreateProductAsync(stock);
        var owner = await FillCartAsync(userId, (productId, quantity));
        var input = await InputAsync(owner, method);

        var result = await CreateOrderService().PlaceAsync(owner, input, "en");

        Assert.Equal(PlaceOrderStatus.Placed, result.Status);
        return result.Order!;
    }
}

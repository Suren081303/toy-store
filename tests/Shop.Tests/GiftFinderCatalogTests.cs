using Microsoft.EntityFrameworkCore;
using Shop.Core.Catalog;
using Shop.Core.Entities;
using Shop.Core.Orders;
using Shop.Core.Payments;

namespace Shop.Tests;

public class PriceRangeTests
{
    [Fact]
    public void Normalize_CleansWhatWasTyped()
    {
        var cases = new (decimal? From, decimal? To, decimal? ExpectedFrom, decimal? ExpectedTo, string Why)[]
        {
            (null, null, null, null, "nothing typed"),
            (1000, 5000, 1000, 5000, "a normal range"),
            (1000, null, 1000, null, "only From"),
            (null, 5000, null, 5000, "only To"),
            (0, 0, null, null, "zero means no limit"),
            (-5, -1, null, null, "so do negative numbers"),
            (5000, 1000, 1000, 5000, "From bigger than To: they swap places"),
            (1000.4m, 1999.5m, 1000, 2000, "cents are rounded to whole drams, halves up"),
            (2_000_000_000, 5000, null, 5000, "a silly From is ignored"),
            (5000, 2_000_000_000, 5000, null, "a silly To is ignored"),
            (0, 5000, null, 5000, "From zero, To set"),
        };

        foreach (var c in cases)
        {
            var (low, high) = PriceRange.Normalize(c.From, c.To);

            Assert.True(c.ExpectedFrom == low && c.ExpectedTo == high, $"{c.Why}: got {low}-{high}, expected {c.ExpectedFrom}-{c.ExpectedTo}");
        }
    }
}

// The gift finder asks the catalog for products. The dataset (CatalogFixture), final prices after discount:
// SKU-001 10000 (stock 5, cat A, 0-2), SKU-002 10000 (stock 0, cat A, 0-2 and 3-5), SKU-003 13500 (stock 3, cat A, 3-5),
// SKU-004 5000 (stock 8, cat B, 3-5), SKU-007 7000 (stock 1, cat B, no age group). SKU-005 is archived, SKU-006 is in a hidden category.
[Collection("Catalog")]
public class GiftFinderCatalogTests
{
    private readonly CatalogFixture _fx;

    public GiftFinderCatalogTests(CatalogFixture fx)
    {
        _fx = fx;
    }

    private int[] Ids(params string[] skus) => skus.Select(_fx.ProductId).ToArray();

    private static int[] ItemIds(IReadOnlyList<ProductListItem> items) => items.Select(i => i.Id).ToArray();

    private async Task<IReadOnlyList<ProductListItem>> FindAsync(ProductQuery query, int count = 50)
    {
        query.Culture = "en";
        return await _fx.CreateService().FindAsync(query, count);
    }

    [Fact]
    public async Task PriceFilter_UsesThePriceAfterDiscount_BothEndsIncluded()
    {
        // SKU-002 costs 20000 minus 50% = 10000 and SKU-003 costs 15000 minus 10% = 13500.
        var tenThousand = await FindAsync(new ProductQuery { PriceFrom = 10000, PriceTo = 10000 });
        Assert.Equal(Ids("SKU-001", "SKU-002").Order(), ItemIds(tenThousand).Order());

        var upTo13500 = await FindAsync(new ProductQuery { PriceFrom = 10001, PriceTo = 13500 });
        Assert.Equal(Ids("SKU-003"), ItemIds(upTo13500));

        Assert.Empty(await FindAsync(new ProductQuery { PriceFrom = 13501 }));
        Assert.Empty(await FindAsync(new ProductQuery { PriceFrom = 10001, PriceTo = 13499 }));
    }

    [Fact]
    public async Task PriceFilter_OnlyFrom_OrOnlyTo()
    {
        Assert.Equal(Ids("SKU-004", "SKU-007").Order(), ItemIds(await FindAsync(new ProductQuery { PriceTo = 7000 })).Order());
        Assert.Equal(Ids("SKU-003"), ItemIds(await FindAsync(new ProductQuery { PriceFrom = 11000 })));
    }

    [Fact]
    public async Task PriceFilter_AgreesWithTheShownPrice_AlsoWhenTheDiscountedPriceNeedsRounding()
    {
        // 1005 minus 50% is 502.5, shown as 503; 1001 minus 50% is 500.5, shown as 501; 999 minus 33% is 669.33, shown as 669.
        var cases = new[] { (Price: 1005m, Discount: 50), (Price: 1001m, Discount: 50), (Price: 999m, Discount: 33) };
        var created = new List<int>();

        try
        {
            foreach (var (price, discount) in cases)
            {
                created.Add(await AddProductAsync(price, discount));
            }

            for (var i = 0; i < cases.Length; i++)
            {
                var shown = ProductRules.GetFinalPrice(cases[i].Price, cases[i].Discount);

                foreach (var bound in new[] { shown - 1, shown, shown + 1 })
                {
                    var from = await FindAsync(new ProductQuery { PriceFrom = bound });
                    Assert.Equal(shown >= bound, ItemIds(from).Contains(created[i]));

                    var to = await FindAsync(new ProductQuery { PriceTo = bound });
                    Assert.Equal(shown <= bound, ItemIds(to).Contains(created[i]));
                }
            }
        }
        finally
        {
            // Archived products are invisible to the shop, so the other tests are not affected.
            using var db = _fx.CreateDbContext();
            await db.Products.Where(p => created.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsArchived, true));
        }
    }

    private async Task<int> AddProductAsync(decimal price, int discount)
    {
        using var db = _fx.CreateDbContext();
        var product = new Product
        {
            Sku = "RND-" + Guid.NewGuid().ToString("N")[..8],
            Brand = "Round",
            CategoryId = _fx.CategoryAId,
            Price = price,
            DiscountPercent = discount,
            StockQuantity = 1,
            CreatedAt = CatalogFixture.Now.AddYears(-5)
        };
        product.Translations.Add(new ProductTranslation { Culture = "hy", Name = "Կլորացում", Description = "d" });
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    [Fact]
    public async Task OnlyInStock_LeavesOutProductsWithNoStock()
    {
        var all = await FindAsync(new ProductQuery());
        var inStock = await FindAsync(new ProductQuery { OnlyInStock = true });

        Assert.Contains(_fx.ProductId("SKU-002"), ItemIds(all));
        Assert.DoesNotContain(_fx.ProductId("SKU-002"), ItemIds(inStock));
        Assert.Equal(4, inStock.Count);
        Assert.All(inStock, p => Assert.True(p.InStock));
    }

    [Fact]
    public async Task CategoryIds_AcceptSeveralCategories_AndIgnoreHiddenOnes()
    {
        var both = await FindAsync(new ProductQuery { CategoryIds = [_fx.CategoryAId, _fx.CategoryBId] });
        Assert.Equal(Ids("SKU-001", "SKU-002", "SKU-003", "SKU-004", "SKU-007").Order(), ItemIds(both).Order());

        var onlyB = await FindAsync(new ProductQuery { CategoryIds = [_fx.CategoryBId] });
        Assert.Equal(Ids("SKU-004", "SKU-007").Order(), ItemIds(onlyB).Order());

        // An empty list means "any category".
        Assert.Equal(5, (await FindAsync(new ProductQuery { CategoryIds = [] })).Count);
    }

    [Fact]
    public async Task EveryAnswerNarrowsTheResult()
    {
        var result = await FindAsync(new ProductQuery
        {
            AgeGroupId = _fx.AgeGroup2Id,
            CategoryIds = [_fx.CategoryAId, _fx.CategoryBId],
            PriceFrom = 5000,
            PriceTo = 13500,
            OnlyInStock = true
        });

        // 3-5 years: SKU-002 (out of stock, left out), SKU-003 (13500), SKU-004 (5000).
        Assert.Equal(Ids("SKU-003", "SKU-004").Order(), ItemIds(result).Order());

        var cheaper = await FindAsync(new ProductQuery { AgeGroupId = _fx.AgeGroup2Id, PriceTo = 9999, OnlyInStock = true });
        Assert.Equal(Ids("SKU-004"), ItemIds(cheaper));
    }

    [Fact]
    public async Task Find_ReturnsAtMostTheAskedNumber()
    {
        Assert.Equal(2, (await FindAsync(new ProductQuery(), count: 2)).Count);
    }

    // ---- Best selling ----

    private async Task AddOrderAsync(DateTime createdAt, OrderStatus status, params (string Sku, int Quantity)[] lines)
    {
        using var db = _fx.CreateDbContext();

        var order = new Order
        {
            AccessKey = Guid.NewGuid().ToString("N"),
            CheckoutId = Guid.NewGuid(),
            CustomerName = "Buyer",
            Email = "buyer@example.com",
            Phone = "+37499000000",
            Region = "yerevan",
            City = "City",
            Street = "Street",
            Building = "1",
            DeliveryDate = DateOnly.FromDateTime(createdAt.AddDays(1)),
            DeliverySlot = "10:00-14:00",
            PaymentMethod = PaymentMethod.CashOnDelivery,
            PaymentStatus = PaymentStatus.Pending,
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            AcceptedTermsAt = createdAt
        };

        foreach (var (sku, quantity) in lines)
        {
            var product = await db.Products.SingleAsync(p => p.Sku == sku);
            order.Items.Add(new OrderItem
            {
                ProductId = product.Id, Sku = sku, Brand = product.Brand, Name = sku, NameHy = sku,
                Quantity = quantity, RegularPrice = product.Price, UnitPrice = product.Price
            });
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task BestSelling_ComesFirst_ByPiecesInConfirmedOrdersOfTheLast90Days()
    {
        var now = CatalogFixture.Now;
        await AddOrderAsync(now.AddDays(-3), OrderStatus.Delivered, ("SKU-004", 2));
        await AddOrderAsync(now.AddDays(-2), OrderStatus.Ordered, ("SKU-004", 1), ("SKU-001", 1));
        await AddOrderAsync(now.AddDays(-1), OrderStatus.Cancelled, ("SKU-003", 20));          // cancelled: not a sale
        await AddOrderAsync(now.AddDays(-1), OrderStatus.AwaitingPayment, ("SKU-003", 20));    // not paid yet: not a sale
        await AddOrderAsync(now.AddDays(-91), OrderStatus.Delivered, ("SKU-007", 50));         // too long ago

        var result = await FindAsync(new ProductQuery { OnlyInStock = true, Sort = ProductSort.BestSelling });

        // SKU-004 sold 3, SKU-001 sold 1; the rest sold nothing and come newest first: SKU-007 (1 day old), SKU-003 (60 days).
        Assert.Equal(Ids("SKU-004", "SKU-001", "SKU-007", "SKU-003"), ItemIds(result));
    }
}

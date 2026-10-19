using System.Text;
using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Entities;
using Shop.Core.Orders;
using Shop.Core.Payments;
using Shop.Infrastructure.Admin;

namespace Shop.Tests;

// The orders here are made by hand with exact times, in a far-away year of their own for each test,
// so the other tests that share the database cannot change the numbers.
// Armenia is 4 hours ahead of UTC: 20:00 UTC is already midnight of the next Armenian day.
[Collection("Orders")]
public class AdminAnalyticsServiceTests
{
    private readonly OrderFixture _fx;

    public AdminAnalyticsServiceTests(OrderFixture fx)
    {
        _fx = fx;
    }

    private record Line(int ProductId, int Quantity, decimal Unit, string Brand = "Brand");

    private AdminAnalyticsService Service(DateTime? now = null) =>
        new(_fx.CreateDbContext(), new FixedTimeProvider(now ?? OrderFixture.Now));

    // March 10 to 12 of the given year, Armenian days.
    private static AnalyticsPeriod Window(int year) => new(new DateOnly(year, 3, 10), new DateOnly(year, 3, 12));

    private static DateTime Utc(int year, int month, int day, int hour = 10, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private async Task<int> AddOrderAsync(
        DateTime createdAt,
        OrderStatus status,
        decimal subtotal,
        decimal discount = 0,
        decimal fee = 0,
        string? userId = null,
        string email = "buyer@example.com",
        string region = "yerevan",
        string slot = "10:00-14:00",
        PaymentMethod method = PaymentMethod.CashOnDelivery,
        bool customerDiscount = false,
        params Line[] lines)
    {
        using var db = _fx.CreateDbContext();

        var order = new Order
        {
            AccessKey = Guid.NewGuid().ToString("N"),
            CheckoutId = Guid.NewGuid(),
            UserId = userId,
            CustomerName = "Buyer",
            Email = email,
            Phone = "+37499000000",
            Region = region,
            City = "City",
            Street = "Street",
            Building = "1",
            DeliveryDate = DateOnly.FromDateTime(createdAt.AddDays(1)),
            DeliverySlot = slot,
            PaymentMethod = method,
            PaymentStatus = PaymentStatus.Pending,
            Status = status,
            Subtotal = subtotal,
            Discount = discount,
            DeliveryFee = fee,
            Total = subtotal - discount + fee,
            CustomerDiscountApplied = customerDiscount,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            AcceptedTermsAt = createdAt
        };

        foreach (var line in lines)
        {
            var sku = await db.Products.Where(p => p.Id == line.ProductId).Select(p => p.Sku).SingleAsync();
            order.Items.Add(new OrderItem
            {
                ProductId = line.ProductId,
                Sku = sku,
                Brand = line.Brand,
                Name = "Toy " + sku,
                NameHy = "Խաղ " + sku,
                Quantity = line.Quantity,
                RegularPrice = line.Unit,
                UnitPrice = line.Unit
            });
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Number;
    }

    [Fact]
    public async Task Sales_CountOnlyConfirmedOrders_ByTheArmenianDay()
    {
        const int year = 2030;
        await AddOrderAsync(Utc(year, 3, 9, 19, 59), OrderStatus.Ordered, 1000);      // 23:59 on March 9: before the period
        await AddOrderAsync(Utc(year, 3, 9, 20, 0), OrderStatus.Ordered, 2000);       // 00:00 on March 10: first minute
        await AddOrderAsync(Utc(year, 3, 12, 19, 59), OrderStatus.Delivered, 4000);   // 23:59 on March 12: last minute
        await AddOrderAsync(Utc(year, 3, 12, 20, 0), OrderStatus.Ordered, 8000);      // 00:00 on March 13: after the period
        await AddOrderAsync(Utc(year, 3, 11, 8), OrderStatus.Cancelled, 16000);
        await AddOrderAsync(Utc(year, 3, 11, 9), OrderStatus.AwaitingPayment, 32000);

        var report = await Service().GetReportAsync(Window(year), "en");

        Assert.Equal(6000, report.Current.Sales);
        Assert.Equal(2, report.Current.Orders);
        Assert.Equal(1, report.Current.Cancelled);
        Assert.Equal(33.3m, report.Current.CancelRate); // 1 of 3; the order waiting for payment is not counted

        // The previous period (March 7-9) holds only the 23:59 order.
        Assert.Equal(1000, report.Before.Sales);
        Assert.Equal(1, report.Before.Orders);

        Assert.Equal(3, report.Days.Count);
        Assert.Equal(2000, report.Days[0].Sales);
        Assert.Equal(0, report.Days[1].Sales);
        Assert.Equal(4000, report.Days[2].Sales);
    }

    [Fact]
    public async Task Totals_ShowDiscountsDeliveryItemsAndRegisteredOrdersSeparately()
    {
        const int year = 2031;
        var userId = await _fx.CreateUserAsync();
        var a = await _fx.CreateProductAsync();
        var b = await _fx.CreateProductAsync();

        await AddOrderAsync(Utc(year, 3, 10), OrderStatus.Ordered, 12000, discount: 2000, fee: 1000, userId: userId, customerDiscount: true,
            lines: [new Line(a, 2, 5000), new Line(b, 1, 2000)]);
        await AddOrderAsync(Utc(year, 3, 11), OrderStatus.Delivered, 5000, lines: [new Line(a, 1, 5000)]);
        await AddOrderAsync(Utc(year, 3, 11), OrderStatus.Cancelled, 9000, lines: [new Line(b, 4, 2250)]);

        var totals = (await Service().GetReportAsync(Window(year), "en")).Current;

        Assert.Equal(16000, totals.Sales);   // 11000 + 5000
        Assert.Equal(2, totals.Orders);
        Assert.Equal(8000, totals.AverageOrder);
        Assert.Equal(4, totals.Items);       // the cancelled order's 4 pieces are not sold
        Assert.Equal(2000, totals.Discounts);
        Assert.Equal(1000, totals.DeliveryFees);
        Assert.Equal(1, totals.RegisteredOrders);
        Assert.Equal(1, totals.GuestOrders);
        Assert.Equal(1, totals.DiscountedOrders);
    }

    [Fact]
    public async Task Refunds_AreCountedOnTheDayOfTheRefund_AndTakenOffNetSales()
    {
        const int year = 2032;
        await AddOrderAsync(Utc(year, 3, 10), OrderStatus.Delivered, 10000);
        var oldOrder = await AddOrderAsync(Utc(year, 2, 1), OrderStatus.Delivered, 7000); // bought long before the period
        var notRefunded = await AddOrderAsync(Utc(year, 2, 2), OrderStatus.Delivered, 3000);

        using (var db = _fx.CreateDbContext())
        {
            var oldId = await db.Orders.Where(o => o.Number == oldOrder).Select(o => o.Id).SingleAsync();
            var otherId = await db.Orders.Where(o => o.Number == notRefunded).Select(o => o.Id).SingleAsync();

            db.ReturnRequests.Add(new ReturnRequest
            {
                OrderId = oldId, Reason = "r", Status = ReturnStatus.Refunded, CreatedAt = Utc(year, 3, 10), RefundedAt = Utc(year, 3, 11)
            });
            db.ReturnRequests.Add(new ReturnRequest
            {
                OrderId = otherId, Reason = "r", Status = ReturnStatus.Requested, CreatedAt = Utc(year, 3, 12)
            });
            await db.SaveChangesAsync();
        }

        var totals = (await Service().GetReportAsync(Window(year), "en")).Current;

        Assert.Equal(10000, totals.Sales);
        Assert.Equal(2, totals.Returns);       // both requests were made in the period
        Assert.Equal(7000, totals.Refunded);   // only one was paid back
        Assert.Equal(3000, totals.NetSales);
    }

    [Fact]
    public async Task Breakdowns_GroupByPaymentRegionSlotStatusWeekdayAndHour()
    {
        const int year = 2033; // March 10, 2033 is a Thursday
        await AddOrderAsync(Utc(year, 3, 10, 6), OrderStatus.Ordered, 1000, region: "yerevan", slot: "10:00-14:00", method: PaymentMethod.CashOnDelivery);
        await AddOrderAsync(Utc(year, 3, 10, 6, 30), OrderStatus.Packed, 2000, region: "yerevan", slot: "14:00-18:00", method: PaymentMethod.Card);
        await AddOrderAsync(Utc(year, 3, 11, 20, 30), OrderStatus.Delivered, 4000, region: "shirak", slot: "10:00-14:00", method: PaymentMethod.Card); // 00:30 on Saturday March 12
        await AddOrderAsync(Utc(year, 3, 11, 8), OrderStatus.Cancelled, 500, region: "shirak");

        var report = await Service().GetReportAsync(Window(year), "en");

        var card = report.ByPayment.Single(r => r.Key == nameof(PaymentMethod.Card));
        Assert.Equal(6000, card.Sales);
        Assert.Equal(2, card.Orders);
        Assert.Equal(1000, report.ByPayment.Single(r => r.Key == nameof(PaymentMethod.CashOnDelivery)).Sales);
        Assert.Equal(0, report.ByPayment.Single(r => r.Key == nameof(PaymentMethod.BankTransfer)).Orders); // every method is listed

        Assert.Equal(3000, report.ByRegion.Single(r => r.Key == "yerevan").Sales);
        Assert.Equal(4000, report.ByRegion.Single(r => r.Key == "shirak").Sales);
        Assert.Equal("shirak", report.ByRegion[0].Key); // biggest first
        Assert.Equal(5000, report.BySlot.Single(r => r.Key == "10:00-14:00").Sales);

        Assert.Equal(1, report.ByStatus.Single(r => r.Key == nameof(OrderStatus.Cancelled)).Orders); // the status table lists all orders
        Assert.Equal(3, report.ByStatus.Where(r => r.Key != nameof(OrderStatus.Cancelled)).Sum(r => r.Orders));

        Assert.Equal(7, report.ByWeekday.Count);
        Assert.Equal(3000, report.ByWeekday[3].Sales); // Thursday, Monday = 0
        Assert.Equal(4000, report.ByWeekday[5].Sales); // Saturday: the late order counts on the Armenian day
        Assert.Equal(24, report.ByHour.Count);
        Assert.Equal(2, report.ByHour[10].Orders);     // 06:00 and 06:30 UTC = 10:00 and 10:30 in Armenia
        Assert.Equal(4000, report.ByHour[0].Sales);    // 20:30 UTC = 00:30
    }

    [Fact]
    public async Task Breakdowns_GroupByBrandCategoryAndAgeGroup()
    {
        const int year = 2034;
        var cars = await _fx.CreateProductAsync();
        var dolls = await _fx.CreateProductAsync();
        int dollsCategoryId, toddlersId, kidsId;

        using (var db = _fx.CreateDbContext())
        {
            var category = new Category { IsActive = true };
            category.Translations.Add(new CategoryTranslation { Culture = "hy", Name = "Տիկնիկներ" });
            category.Translations.Add(new CategoryTranslation { Culture = "en", Name = "Dolls " + year });
            var toddlers = new AgeGroup();
            toddlers.Translations.Add(new AgeGroupTranslation { Culture = "hy", Name = "Փոքրեր" });
            toddlers.Translations.Add(new AgeGroupTranslation { Culture = "en", Name = "Toddlers " + year });
            var kids = new AgeGroup();
            kids.Translations.Add(new AgeGroupTranslation { Culture = "hy", Name = "Երեխաներ" });
            kids.Translations.Add(new AgeGroupTranslation { Culture = "en", Name = "Kids " + year });
            db.Categories.Add(category);
            db.AgeGroups.AddRange(toddlers, kids);
            await db.SaveChangesAsync();

            var doll = await db.Products.Include(p => p.AgeGroups).SingleAsync(p => p.Id == dolls);
            doll.CategoryId = category.Id;
            doll.AgeGroups.Add(toddlers);
            doll.AgeGroups.Add(kids); // in two groups: counts in both

            (await db.Products.Include(p => p.AgeGroups).SingleAsync(p => p.Id == cars)).AgeGroups.Add(kids);
            await db.SaveChangesAsync();

            dollsCategoryId = category.Id;
            toddlersId = toddlers.Id;
            kidsId = kids.Id;
        }

        await AddOrderAsync(Utc(year, 3, 10), OrderStatus.Ordered, 9000,
            lines: [new Line(cars, 2, 2000, "Speedy"), new Line(dolls, 1, 5000, "Lalka")]);
        await AddOrderAsync(Utc(year, 3, 11), OrderStatus.Cancelled, 1000, lines: [new Line(cars, 1, 1000, "Speedy")]);

        var report = await Service().GetReportAsync(Window(year), "en");

        Assert.Equal(4000, report.ByBrand.Single(r => r.Key == "Speedy").Sales);
        Assert.Equal(2, report.ByBrand.Single(r => r.Key == "Speedy").Units);
        Assert.Equal(5000, report.ByBrand.Single(r => r.Key == "Lalka").Sales);
        Assert.Equal("Lalka", report.ByBrand[0].Key);

        var dollRow = report.ByCategory.Single(r => r.Key == "Dolls " + year);
        Assert.Equal(5000, dollRow.Sales);
        Assert.Equal(1, dollRow.Units);
        Assert.Equal(4000, report.ByCategory.Single(r => r.Key == "Category").Sales);

        Assert.Equal(5000, report.ByAgeGroup.Single(r => r.Key == "Toddlers " + year).Sales);
        Assert.Equal(9000, report.ByAgeGroup.Single(r => r.Key == "Kids " + year).Sales); // cars 4000 + dolls 5000

        // Armenian names when the language is Armenian.
        var armenian = await Service().GetReportAsync(Window(year), "hy");
        Assert.Contains(armenian.ByCategory, r => r.Key == "Տիկնիկներ");

        Assert.NotEqual(0, dollsCategoryId + toddlersId + kidsId);
    }

    [Fact]
    public async Task Products_TopLists_NoSalesAndLowStock()
    {
        const int year = 2035;
        var many = await _fx.CreateProductAsync();
        var pricey = await _fx.CreateProductAsync();
        var unsold = await _fx.CreateProductAsync(stock: 50);
        var low = await _fx.CreateProductAsync(stock: 0);
        var archivedLow = await _fx.CreateProductAsync(stock: 0, archived: true);

        // The list shows only 10 products: leftovers of other tests with little stock must not push this one out.
        using (var db = _fx.CreateDbContext())
        {
            await db.Products.Where(p => p.Id != low && p.Id != archivedLow && p.StockQuantity <= StockLevels.Low)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, 10));
        }

        await AddOrderAsync(Utc(year, 3, 10), OrderStatus.Ordered, 10000 + 15000,
            lines: [new Line(many, 10, 1000), new Line(pricey, 1, 15000)]);
        await AddOrderAsync(Utc(year, 3, 11), OrderStatus.Cancelled, 100000, lines: [new Line(unsold, 5, 20000)]);

        var report = await Service().GetReportAsync(Window(year), "en");

        Assert.Equal(many, report.TopByUnits[0].ProductId);
        Assert.Equal(10, report.TopByUnits[0].Units);
        Assert.Equal(pricey, report.TopByMoney[0].ProductId);
        Assert.Equal(15000, report.TopByMoney[0].Sales);
        Assert.StartsWith("Toy ", report.TopByMoney[0].Name);

        Assert.DoesNotContain(report.TopByUnits, p => p.ProductId == unsold); // a cancelled order is not a sale
        Assert.True(report.TopByUnits.Count <= 10 && report.TopByMoney.Count <= 10);

        // A product with only a cancelled order has no sale; the newest products come first, so the ones made above are on the list.
        Assert.Contains(report.NoSales, p => p.ProductId == unsold);
        Assert.DoesNotContain(report.NoSales, p => p.ProductId == many);
        Assert.True(report.NoSales.Count <= 10);

        Assert.Contains(report.LowStock, p => p.ProductId == low && p.Stock == 0);
        Assert.DoesNotContain(report.LowStock, p => p.ProductId == unsold);
        Assert.DoesNotContain(report.LowStock, p => p.ProductId == archivedLow); // archived products are not for sale
    }

    [Fact]
    public async Task Customers_AreNewOrReturning_AndTopBuyersAreRankedBySpending()
    {
        const int year = 2036;
        var firstTimer = $"first-{year}@example.com";
        var regular = $"regular-{year}@example.com";

        await AddOrderAsync(Utc(year, 1, 5), OrderStatus.Delivered, 1000, email: regular);        // before the period
        await AddOrderAsync(Utc(year, 3, 10), OrderStatus.Ordered, 3000, email: regular);
        await AddOrderAsync(Utc(year, 3, 11), OrderStatus.Packed, 4000, email: regular);
        await AddOrderAsync(Utc(year, 3, 11), OrderStatus.Ordered, 9000, email: firstTimer);
        await AddOrderAsync(Utc(year, 1, 6), OrderStatus.Cancelled, 500, email: firstTimer);        // a cancelled order does not make a returning buyer

        var customers = (await Service().GetReportAsync(Window(year), "en")).Customers;

        Assert.Equal(2, customers.Buyers);
        Assert.Equal(1, customers.NewBuyers);
        Assert.Equal(1, customers.ReturningBuyers);
        Assert.Equal(firstTimer, customers.Top[0].Email);
        Assert.Equal(9000, customers.Top[0].Spent);
        Assert.Equal(regular, customers.Top[1].Email);
        Assert.Equal(7000, customers.Top[1].Spent);
        Assert.Equal(2, customers.Top[1].Orders);
    }

    [Fact]
    public async Task NewAccounts_AreCountedByTheirCreationDay()
    {
        const int year = 2037;
        using (var db = _fx.CreateDbContext())
        {
            foreach (var at in new[] { Utc(year, 3, 9, 19, 59), Utc(year, 3, 9, 20, 0), Utc(year, 3, 12, 19, 59), Utc(year, 3, 12, 20, 0) })
            {
                db.Users.Add(new ApplicationUser
                {
                    UserName = $"{Guid.NewGuid():N}@example.com", Email = $"{Guid.NewGuid():N}@example.com", FullName = "New", CreatedAt = at
                });
            }

            await db.SaveChangesAsync();
        }

        var report = await Service().GetReportAsync(Window(year), "en");

        Assert.Equal(2, report.Current.NewAccounts);
        Assert.Equal(1, report.Before.NewAccounts);
    }

    [Fact]
    public async Task WaitingCarts_CountOldCartsWithItems_AtTodaysPrices()
    {
        var product = await _fx.CreateProductAsync(price: 10000, discountPercent: 10); // costs 9000
        var archived = await _fx.CreateProductAsync(price: 7000, archived: true);
        var before = (await Service().GetReportAsync(Window(2038), "en")).Waiting;

        using (var db = _fx.CreateDbContext())
        {
            var old = new Cart { Id = Guid.NewGuid(), UpdatedAt = OrderFixture.Now.AddHours(-25) };
            old.Items.Add(new CartItem { ProductId = product, Quantity = 2 });
            old.Items.Add(new CartItem { ProductId = archived, Quantity = 1 }); // gone from the shop: not counted
            var fresh = new Cart { Id = Guid.NewGuid(), UpdatedAt = OrderFixture.Now.AddHours(-23) };
            fresh.Items.Add(new CartItem { ProductId = product, Quantity = 5 });
            var empty = new Cart { Id = Guid.NewGuid(), UpdatedAt = OrderFixture.Now.AddDays(-3) };
            db.Carts.AddRange(old, fresh, empty);
            await db.SaveChangesAsync();
        }

        var after = (await Service().GetReportAsync(Window(2038), "en")).Waiting;

        Assert.Equal(1, after.Carts - before.Carts);
        Assert.Equal(18000, after.Value - before.Value);
    }

    [Fact]
    public async Task EmptyPeriod_GivesZeros_AndKeepsEveryDay()
    {
        var report = await Service().GetReportAsync(Window(2039), "en");

        Assert.Equal(0, report.Current.Sales);
        Assert.Equal(0, report.Current.Orders);
        Assert.Equal(3, report.Days.Count);
        Assert.All(report.Days, d => Assert.Equal(0, d.Sales));
        Assert.Empty(report.ByRegion);
        Assert.Empty(report.TopByUnits);
        Assert.Equal(0, report.Customers.Buyers);
    }

    // ---- Exports ----

    private static string Csv(byte[] file) => Encoding.UTF8.GetString(file, 3, file.Length - 3);

    [Fact]
    public async Task OrdersExport_ListsEveryOrderOfThePeriod_WithoutPersonalData_AndIsAudited()
    {
        const int year = 2040;
        var adminId = await _fx.CreateUserAsync();
        var product = await _fx.CreateProductAsync();
        var number = await AddOrderAsync(Utc(year, 3, 11, 9, 5), OrderStatus.Delivered, 5000, fee: 1000, email: "secret.person@example.com", region: "lori",
            lines: [new Line(product, 1, 5000)]);
        var cancelled = await AddOrderAsync(Utc(year, 3, 11, 10), OrderStatus.Cancelled, 700);
        await AddOrderAsync(Utc(year, 3, 20), OrderStatus.Ordered, 9999); // outside

        var file = await Service().ExportOrdersCsvAsync(Window(year), adminId);
        var text = Csv(file);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file[..3]);
        Assert.StartsWith("Number,Placed,Status,", lines[0]);
        Assert.Equal(3, lines.Length); // header + 2 orders; the cancelled one is listed with its status
        Assert.Contains($"{number},2040-03-11 13:05,Delivered,CashOnDelivery,Pending,lori,1,5000,0,1000,6000,Guest,no", text);
        Assert.Contains($"{cancelled},2040-03-11 14:00,Cancelled", text);
        Assert.DoesNotContain("9999", text);

        // No names, phones, emails, addresses.
        Assert.DoesNotContain("secret.person", text);
        Assert.DoesNotContain("Buyer", text);
        Assert.DoesNotContain("+37499000000", text);
        Assert.DoesNotContain("Street", text);

        using var db = _fx.CreateDbContext();
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "Export.Orders" && a.EntityId == "2040-03-10..2040-03-12");
        Assert.Equal(adminId, audit.UserId);
        Assert.Equal("Analytics", audit.EntityType);
        Assert.Equal("2 rows", audit.Details);
    }

    [Fact]
    public async Task ItemsExport_HasOneLinePerItem_WithTheArmenianName_AndNoPersonalData()
    {
        const int year = 2041;
        var adminId = await _fx.CreateUserAsync();
        var product = await _fx.CreateProductAsync();
        var other = await _fx.CreateProductAsync();
        var number = await AddOrderAsync(Utc(year, 3, 10), OrderStatus.Ordered, 9000, email: "private@example.com",
            lines: [new Line(product, 2, 3000, "Speedy"), new Line(other, 1, 3000, "=cmd|' /C calc'!A0")]);

        var text = Csv(await Service().ExportItemsCsvAsync(Window(year), adminId));
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("Number,Placed,Status,Sku,Name,Brand,Quantity,UnitPrice,RegularPrice,LineTotal", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains("Խաղ SKU-", text);
        Assert.Contains($"{number},2041-03-10 14:00,Ordered,", text);
        Assert.Contains(",Speedy,2,3000,3000,6000", text);
        Assert.Contains("'=cmd", text); // a brand that looks like a formula is made harmless
        Assert.DoesNotContain("private@example.com", text);

        using var db = _fx.CreateDbContext();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Export.Items" && a.UserId == adminId && a.Details == "2 rows"));
    }
}

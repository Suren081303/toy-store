using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Orders;
using Shop.Infrastructure.Admin;

namespace Shop.Tests;

public class DashboardUrgencyTests
{
    [Theory]
    [InlineData(-3, DeliveryUrgency.Late)]
    [InlineData(-1, DeliveryUrgency.Late)]
    [InlineData(0, DeliveryUrgency.Today)]
    [InlineData(1, DeliveryUrgency.Tomorrow)]
    [InlineData(2, DeliveryUrgency.Later)]
    public void UrgencyOf_ComparesTheDeliveryDayWithToday(int daysFromToday, DeliveryUrgency expected)
    {
        var today = new DateOnly(2026, 6, 10);

        Assert.Equal(expected, AdminDashboardService.UrgencyOf(today.AddDays(daysFromToday), today));
    }
}

// Each test uses its own "now" in a year no other test uses, so the sales and the orders to pack are only its own.
[Collection("Orders")]
public class AdminDashboardServiceTests
{
    private readonly OrderFixture _fx;

    public AdminDashboardServiceTests(OrderFixture fx)
    {
        _fx = fx;
    }

    private async Task SetOrderAsync(int number, DateTime? createdAt = null, DateOnly? deliveryDate = null, string? deliverySlot = null, string? createdByUserId = null)
    {
        using var db = _fx.CreateDbContext();
        var order = await db.Orders.SingleAsync(o => o.Number == number);

        if (createdAt is not null) order.CreatedAt = createdAt.Value;
        if (deliveryDate is not null) order.DeliveryDate = deliveryDate.Value;
        if (deliverySlot is not null) order.DeliverySlot = deliverySlot;
        if (createdByUserId is not null) order.CreatedByUserId = createdByUserId;

        await db.SaveChangesAsync();
    }

    private async Task SetUserCreatedAsync(string id, DateTime createdAt)
    {
        using var db = _fx.CreateDbContext();
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.CreatedAt, createdAt));
    }

    [Fact]
    public async Task Sales_AreCountedByTheArmenianDay_ForConfirmedOrdersOnly_AndTheChartHasFourteenDays()
    {
        // 16:00 on March 10, 2051 in Armenia.
        var now = new DateTime(2051, 3, 10, 12, 0, 0, DateTimeKind.Utc);

        var todayMorning = await _fx.PlaceOrderAsync();
        var todayJustAfterMidnight = await _fx.PlaceOrderAsync();   // 20:30 UTC on March 9 = 00:30 on March 10 in Armenia
        var yesterdayLate = await _fx.PlaceOrderAsync();            // 19:30 UTC on March 9 = 23:30 on March 9 in Armenia
        var cancelled = await _fx.PlaceOrderAsync();
        var firstChartDay = await _fx.PlaceOrderAsync();            // February 25: the 14th day back
        var tooOld = await _fx.PlaceOrderAsync();                   // February 24: not on the chart

        await SetOrderAsync(todayMorning.Number, createdAt: new DateTime(2051, 3, 10, 6, 0, 0, DateTimeKind.Utc));
        await SetOrderAsync(todayJustAfterMidnight.Number, createdAt: new DateTime(2051, 3, 9, 20, 30, 0, DateTimeKind.Utc));
        await SetOrderAsync(yesterdayLate.Number, createdAt: new DateTime(2051, 3, 9, 19, 30, 0, DateTimeKind.Utc));
        await SetOrderAsync(cancelled.Number, createdAt: new DateTime(2051, 3, 10, 7, 0, 0, DateTimeKind.Utc));
        await SetOrderAsync(firstChartDay.Number, createdAt: new DateTime(2051, 2, 25, 8, 0, 0, DateTimeKind.Utc));
        await SetOrderAsync(tooOld.Number, createdAt: new DateTime(2051, 2, 24, 8, 0, 0, DateTimeKind.Utc));
        await _fx.CreateOrderService().ChangeStatusAsync(cancelled.Number, OrderStatus.Cancelled, null, null);

        var data = await _fx.CreateDashboardService(now).GetAsync("en");

        Assert.Equal(14, data.Days.Count);
        Assert.Equal(new DateOnly(2051, 2, 25), data.Days[0].Day);
        Assert.Equal(new DateOnly(2051, 3, 10), data.Days[^1].Day);
        Assert.Equal(1, data.Days[0].Orders);                  // the 14th day back is on the chart
        Assert.Equal(11500m, data.Days[0].Sales);
        Assert.Equal(0, data.Days[1].Orders);                  // days without orders are there, with zeroes

        Assert.Equal(2, data.Sales.OrdersToday);
        Assert.Equal(23000m, data.Sales.SalesToday);
        Assert.Equal(1, data.Sales.OrdersYesterday);
        Assert.Equal(11500m, data.Sales.SalesYesterday);
        Assert.Equal(4, data.Days.Sum(d => d.Orders));         // the cancelled and the too old orders are not there
    }

    [Fact]
    public async Task NewCustomersToday_AreCounted_WithoutAdmins_AndByTheArmenianDay()
    {
        var now = new DateTime(2052, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        var before = (await _fx.CreateDashboardService(now).GetAsync("en")).Sales.NewCustomersToday;

        var early = await _fx.CreateUserAsync();       // 20:30 UTC the day before = 00:30 in Armenia: today
        var noon = await _fx.CreateUserAsync();
        var lateYesterday = await _fx.CreateUserAsync(); // 19:30 UTC the day before = 23:30 in Armenia: yesterday
        var admin = await _fx.CreateUserAsync();
        await SetUserCreatedAsync(early, new DateTime(2052, 3, 9, 20, 30, 0, DateTimeKind.Utc));
        await SetUserCreatedAsync(noon, new DateTime(2052, 3, 10, 9, 0, 0, DateTimeKind.Utc));
        await SetUserCreatedAsync(lateYesterday, new DateTime(2052, 3, 9, 19, 30, 0, DateTimeKind.Utc));
        await SetUserCreatedAsync(admin, new DateTime(2052, 3, 10, 9, 0, 0, DateTimeKind.Utc));

        using (var db = _fx.CreateDbContext())
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == "Admin");
            if (role is null)
            {
                role = new IdentityRole("Admin") { NormalizedName = "ADMIN" };
                db.Roles.Add(role);
                await db.SaveChangesAsync();
            }

            db.UserRoles.Add(new IdentityUserRole<string> { UserId = admin, RoleId = role.Id });
            await db.SaveChangesAsync();
        }

        var after = (await _fx.CreateDashboardService(now).GetAsync("en")).Sales.NewCustomersToday;

        Assert.Equal(before + 2, after);
    }

    [Fact]
    public async Task OrdersToPack_AreSortedByDeliveryDay_WithUrgency_AndLeaveOutPackedOrders()
    {
        // 16:00 on May 2, 2019 in Armenia.
        var now = new DateTime(2019, 5, 2, 12, 0, 0, DateTimeKind.Utc);
        var phoneAdmin = await _fx.CreateUserAsync();

        var tomorrow = await _fx.PlaceOrderAsync();
        var late = await _fx.PlaceOrderAsync();
        var today = await _fx.PlaceOrderAsync();
        var packed = await _fx.PlaceOrderAsync();

        await SetOrderAsync(tomorrow.Number, deliveryDate: new DateOnly(2019, 5, 3), deliverySlot: "10:00-12:00");
        await SetOrderAsync(late.Number, deliveryDate: new DateOnly(2019, 5, 1), deliverySlot: "14:00-16:00", createdByUserId: phoneAdmin);
        await SetOrderAsync(today.Number, deliveryDate: new DateOnly(2019, 5, 2), deliverySlot: "12:00-14:00");
        await SetOrderAsync(packed.Number, deliveryDate: new DateOnly(2019, 5, 1), deliverySlot: "10:00-12:00");
        await _fx.CreateOrderService().ChangeStatusAsync(packed.Number, OrderStatus.Packed, null, null);

        var data = await _fx.CreateDashboardService(now).GetAsync("en");

        var ours = new[] { tomorrow.Number, late.Number, today.Number, packed.Number };
        var rows = data.ToPack.Where(r => ours.Contains(r.Number)).ToList();

        Assert.Equal([late.Number, today.Number, tomorrow.Number], rows.Select(r => r.Number));
        Assert.Equal([DeliveryUrgency.Late, DeliveryUrgency.Today, DeliveryUrgency.Tomorrow], rows.Select(r => r.Urgency));
        Assert.True(rows[0].IsPhoneOrder);
        Assert.False(rows[1].IsPhoneOrder);
        Assert.Equal("14:00-16:00", rows[0].DeliverySlot);

        // The number next to the list counts all orders to pack, the same as the card.
        Assert.Equal(data.Counts.ToPack, data.ToPackTotal);
        Assert.True(data.ToPack.Count <= AdminDashboardService.ListSize);
    }

    [Fact]
    public async Task LowStock_ListsOutOfStockFirst_LeavesOutArchivedProducts_AndUsesTheNameInTheLanguage()
    {
        var empty = await _fx.CreateProductAsync(stock: 0);
        var two = await _fx.CreateProductAsync(stock: 2);
        var three = await _fx.CreateProductAsync(stock: StockLevels.Low);
        var four = await _fx.CreateProductAsync(stock: StockLevels.Low + 1);
        var archived = await _fx.CreateProductAsync(stock: 0, archived: true);

        // Other tests leave low products in this database; the list shows ten, so make them not low.
        var ours = new[] { empty, two, three, four, archived };
        using (var db = _fx.CreateDbContext())
        {
            await db.Products.Where(p => !ours.Contains(p.Id) && p.StockQuantity <= StockLevels.Low)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, 100));
        }

        var english = await _fx.CreateDashboardService().GetAsync("en");
        var russian = await _fx.CreateDashboardService().GetAsync("ru");

        Assert.Equal([empty, two, three], english.LowStock.Select(r => r.Id));
        Assert.Equal([0, 2, StockLevels.Low], english.LowStock.Select(r => r.Stock));
        Assert.Equal(3, english.LowStockTotal);
        Assert.StartsWith("Toy ", english.LowStock[0].Name);
        Assert.StartsWith("Խաղ ", russian.LowStock[0].Name);   // no Russian name: the Armenian one
        Assert.Equal(english.Counts.LowStock, english.LowStockTotal);
    }

    [Fact]
    public async Task LatestOrders_AreTheNewestEight_NewestFirst()
    {
        var now = new DateTime(2053, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        var numbers = new List<int>();

        for (var i = 0; i < 9; i++)
        {
            var order = await _fx.PlaceOrderAsync();
            await SetOrderAsync(order.Number, createdAt: new DateTime(2053, 3, 10, 1 + i, 0, 0, DateTimeKind.Utc));
            numbers.Add(order.Number);
        }

        var data = await _fx.CreateDashboardService(now).GetAsync("en");

        Assert.Equal(AdminDashboardService.LatestOrdersSize, data.LatestOrders.Count);
        Assert.Equal(Enumerable.Reverse(numbers).Take(8), data.LatestOrders.Select(r => r.Number));
    }
}

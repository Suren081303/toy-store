using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Identity;
using Shop.Core.Orders;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Admin;

// What the admin sees first: the counts, today's sales, the last 14 days, the orders to pack, the products running low
// and the latest orders. One query after another (a DbContext runs one query at a time).
public class AdminDashboardService : IAdminDashboardService
{
    // The lists show this many rows; the numbers next to them say how many there are in all.
    public const int ListSize = 10;

    public const int LatestOrdersSize = 8;

    // The chart: the last 14 Armenian days, today included.
    public const int ChartDays = 14;

    private readonly ShopDbContext _db;
    private readonly IAdminOrderService _orders;
    private readonly TimeProvider _time;

    public AdminDashboardService(ShopDbContext db, IAdminOrderService orders, TimeProvider time)
    {
        _db = db;
        _orders = orders;
        _time = time;
    }

    public async Task<DashboardData> GetAsync(string culture)
    {
        var today = ShopClock.Today(_time);
        var counts = await _orders.GetDashboardAsync();

        // ---- Sales: confirmed orders of the last 14 Armenian days ----
        var first = today.AddDays(-(ChartDays - 1));
        var start = ShopClock.StartOfDayUtc(first);
        var end = ShopClock.StartOfDayUtc(today.AddDays(1));

        var confirmed = await _db.Orders.AsNoTracking()
            .Where(o => o.CreatedAt >= start && o.CreatedAt < end)
            .Where(o => o.Status == OrderStatus.Ordered || o.Status == OrderStatus.Packed || o.Status == OrderStatus.Delivered)
            .Select(o => new { o.CreatedAt, o.Total })
            .ToListAsync();

        var perDay = confirmed
            .GroupBy(o => DateOnly.FromDateTime(ShopClock.ToShopTime(o.CreatedAt)))
            .ToDictionary(g => g.Key, g => (Sales: g.Sum(o => o.Total), Orders: g.Count()));

        var days = Enumerable.Range(0, ChartDays)
            .Select(offset => first.AddDays(offset))
            .Select(day => perDay.TryGetValue(day, out var p) ? new DayPoint(day, p.Sales, p.Orders) : new DayPoint(day, 0, 0))
            .ToList();

        var startToday = ShopClock.StartOfDayUtc(today);
        var newCustomers = await _db.Users.AsNoTracking()
            .Where(u => u.CreatedAt >= startToday && u.CreatedAt < end)
            .Where(u => !_db.UserRoles.Any(ur => ur.UserId == u.Id && _db.Roles.Any(r => r.Id == ur.RoleId && r.Name == Roles.Admin)))
            .CountAsync();

        var sales = new DashboardSales(days[^1].Sales, days[^1].Orders, days[^2].Sales, days[^2].Orders, newCustomers);

        // ---- Orders to pack: the earliest delivery first ----
        var toPack = await _db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Ordered)
            .OrderBy(o => o.DeliveryDate).ThenBy(o => o.DeliverySlot).ThenBy(o => o.Number)
            .Take(ListSize)
            .Select(o => new { o.Number, o.CustomerName, o.Phone, o.DeliveryDate, o.DeliverySlot, o.Total, Phone2 = o.CreatedByUserId != null })
            .ToListAsync();

        var toPackRows = toPack
            .Select(o => new DashboardToPackRow(o.Number, o.CustomerName, o.Phone, o.DeliveryDate, o.DeliverySlot, o.Total, o.Phone2, UrgencyOf(o.DeliveryDate, today)))
            .ToList();

        // ---- Products running low: out of stock first ----
        var lowQuery = _db.Products.AsNoTracking().Where(p => !p.IsArchived && p.StockQuantity <= StockLevels.Low);
        var lowTotal = await lowQuery.CountAsync();
        var low = await lowQuery
            .OrderBy(p => p.StockQuantity).ThenBy(p => p.Sku)
            .Take(ListSize)
            .Select(p => new { p.Id, p.Sku, p.StockQuantity, Names = p.Translations.Select(t => new { t.Culture, t.Name }).ToList() })
            .ToListAsync();

        var lowRows = low
            .Select(p => new DashboardLowStockRow(p.Id, p.Sku, PickName(p.Names.Select(n => (n.Culture, n.Name)), culture), p.StockQuantity))
            .ToList();

        // ---- Latest orders ----
        var latest = (await _orders.ListAsync(new AdminOrderFilter())).Items.Take(LatestOrdersSize).ToList();

        return new DashboardData(counts, sales, days, toPackRows, counts.ToPack, lowRows, lowTotal, latest, _time.GetUtcNow().UtcDateTime);
    }

    public static DeliveryUrgency UrgencyOf(DateOnly deliveryDate, DateOnly today) =>
        deliveryDate < today ? DeliveryUrgency.Late
        : deliveryDate == today ? DeliveryUrgency.Today
        : deliveryDate == today.AddDays(1) ? DeliveryUrgency.Tomorrow
        : DeliveryUrgency.Later;

    private static string PickName(IEnumerable<(string Culture, string Name)> names, string culture)
    {
        var list = names.ToList();

        return list.FirstOrDefault(n => n.Culture == culture).Name
            ?? list.FirstOrDefault(n => n.Culture == "hy").Name
            ?? list.FirstOrDefault().Name
            ?? string.Empty;
    }
}

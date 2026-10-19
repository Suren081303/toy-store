using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Catalog;
using Shop.Core.Entities;
using Shop.Core.Localization;
using Shop.Core.Orders;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Admin;

// The numbers behind the Analytics screen. Everything is read live from the orders.
// "Sales" are confirmed orders (ordered, packed, delivered) by the Armenian day they were placed; cancelled orders and orders still
// waiting for payment are not sales. Every day boundary is midnight in Armenia (UTC+4).
public class AdminAnalyticsService : IAdminAnalyticsService
{
    private const int TopCount = 10;

    private readonly ShopDbContext _db;
    private readonly TimeProvider _time;

    public AdminAnalyticsService(ShopDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    // The same rule twice: once for an order, once for an item (through its order).
    private static readonly Expression<Func<Order, bool>> IsConfirmed =
        o => o.Status == OrderStatus.Ordered || o.Status == OrderStatus.Packed || o.Status == OrderStatus.Delivered;

    private static readonly Expression<Func<OrderItem, bool>> ItemIsConfirmed =
        i => i.Order.Status == OrderStatus.Ordered || i.Order.Status == OrderStatus.Packed || i.Order.Status == OrderStatus.Delivered;

    // ---- Report ----

    public async Task<AnalyticsReport> GetReportAsync(AnalyticsPeriod period, string culture)
    {
        var (start, end) = Bounds(period);
        var previous = period.Previous;
        var (previousStart, previousEnd) = Bounds(previous);

        var current = await TotalsAsync(start, end);
        var before = await TotalsAsync(previousStart, previousEnd);

        var confirmed = Orders(start, end).Where(IsConfirmed);
        var items = Items(start, end).Where(ItemIsConfirmed);

        // Time-based tables: the (time, total) pair of each confirmed order, grouped here by Armenian time.
        var placed = (await confirmed.Select(o => new { o.CreatedAt, o.Total }).ToListAsync())
            .Select(o => (Local: ShopClock.ToShopTime(o.CreatedAt), o.Total))
            .ToList();

        var days = Enumerable.Range(0, period.Days)
            .Select(offset => period.From.AddDays(offset))
            .Select(day =>
            {
                var ofDay = placed.Where(p => DateOnly.FromDateTime(p.Local) == day).ToList();
                return new DayPoint(day, ofDay.Sum(p => p.Total), ofDay.Count);
            })
            .ToList();

        var byWeekday = Enumerable.Range(0, 7)
            .Select(d => Row(d.ToString(), placed.Where(p => ((int)p.Local.DayOfWeek + 6) % 7 == d)))
            .ToList();
        var byHour = Enumerable.Range(0, 24)
            .Select(h => Row(h.ToString(), placed.Where(p => p.Local.Hour == h)))
            .ToList();

        var byPayment = (await confirmed.GroupBy(o => o.PaymentMethod)
                .Select(g => new { g.Key, Sales = g.Sum(o => o.Total), Orders = g.Count() }).ToListAsync())
            .ToDictionary(x => x.Key.ToString(), x => new BreakdownRow(x.Key.ToString(), x.Sales, x.Orders));

        var byStatus = (await Orders(start, end).GroupBy(o => o.Status)
                .Select(g => new { g.Key, Sales = g.Sum(o => o.Total), Orders = g.Count() }).ToListAsync())
            .ToDictionary(x => x.Key.ToString(), x => new BreakdownRow(x.Key.ToString(), x.Sales, x.Orders));

        var byRegion = (await confirmed.GroupBy(o => o.Region)
                .Select(g => new { g.Key, Sales = g.Sum(o => o.Total), Orders = g.Count() }).ToListAsync())
            .Select(x => new BreakdownRow(x.Key, x.Sales, x.Orders))
            .OrderByDescending(r => r.Sales).ThenBy(r => r.Key)
            .ToList();

        var bySlot = (await confirmed.GroupBy(o => o.DeliverySlot)
                .Select(g => new { g.Key, Sales = g.Sum(o => o.Total), Orders = g.Count() }).ToListAsync())
            .Select(x => new BreakdownRow(x.Key, x.Sales, x.Orders))
            .OrderBy(r => r.Key)
            .ToList();

        var byBrand = (await items.GroupBy(i => i.Brand)
                .Select(g => new { g.Key, Sales = g.Sum(i => i.UnitPrice * i.Quantity), Units = g.Sum(i => i.Quantity), Orders = g.Select(i => i.OrderId).Distinct().Count() })
                .ToListAsync())
            .Select(x => new BreakdownRow(x.Key, x.Sales, x.Orders, x.Units))
            .OrderByDescending(r => r.Sales).ThenBy(r => r.Key)
            .ToList();

        var categories = await _db.Categories.AsNoTracking().Include(c => c.Translations).ToListAsync();
        var byCategory = (await items.GroupBy(i => i.Product.CategoryId)
                .Select(g => new { g.Key, Sales = g.Sum(i => i.UnitPrice * i.Quantity), Units = g.Sum(i => i.Quantity), Orders = g.Select(i => i.OrderId).Distinct().Count() })
                .ToListAsync())
            .Select(x => new BreakdownRow(categories.First(c => c.Id == x.Key).Translations.ResolveName(culture), x.Sales, x.Orders, x.Units))
            .OrderByDescending(r => r.Sales).ThenBy(r => r.Key)
            .ToList();

        // An item with several age groups counts in each of them, so these rows can add up to more than the total.
        var ageGroups = await _db.AgeGroups.AsNoTracking().Include(a => a.Translations).ToListAsync();
        var byAgeGroup = (await items
                .SelectMany(i => i.Product.AgeGroups, (i, a) => new { AgeGroupId = a.Id, i.OrderId, i.Quantity, Line = i.UnitPrice * i.Quantity })
                .GroupBy(x => x.AgeGroupId)
                .Select(g => new { g.Key, Sales = g.Sum(x => x.Line), Units = g.Sum(x => x.Quantity), Orders = g.Select(x => x.OrderId).Distinct().Count() })
                .ToListAsync())
            .Select(x => new BreakdownRow(ageGroups.First(a => a.Id == x.Key).Translations.ResolveName(culture), x.Sales, x.Orders, x.Units))
            .OrderByDescending(r => r.Sales).ThenBy(r => r.Key)
            .ToList();

        var productSales = await items.GroupBy(i => i.ProductId)
            .Select(g => new { ProductId = g.Key, Units = g.Sum(i => i.Quantity), Sales = g.Sum(i => i.UnitPrice * i.Quantity) })
            .ToListAsync();
        var topByUnits = productSales.OrderByDescending(p => p.Units).ThenByDescending(p => p.Sales).Take(TopCount).ToList();
        var topByMoney = productSales.OrderByDescending(p => p.Sales).ThenByDescending(p => p.Units).Take(TopCount).ToList();

        var needNames = topByUnits.Concat(topByMoney).Select(p => p.ProductId).Distinct().ToList();
        var names = await _db.Products.AsNoTracking()
            .Where(p => needNames.Contains(p.Id))
            .Include(p => p.Translations)
            .ToDictionaryAsync(p => p.Id);

        ProductSalesRow ToRow(int id, int units, decimal sales) =>
            new(id, names[id].Translations.ResolveName(culture), names[id].Sku, units, sales);

        var noSalesProducts = await _db.Products.AsNoTracking()
            .Where(p => !p.IsArchived && !_db.OrderItems.Any(i => i.ProductId == p.Id && i.Order.CreatedAt >= start && i.Order.CreatedAt < end
                && (i.Order.Status == OrderStatus.Ordered || i.Order.Status == OrderStatus.Packed || i.Order.Status == OrderStatus.Delivered)))
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Take(TopCount)
            .Include(p => p.Translations)
            .ToListAsync();

        var lowStock = await _db.Products.AsNoTracking()
            .Where(p => !p.IsArchived && p.StockQuantity <= StockLevels.Low)
            .OrderBy(p => p.StockQuantity).ThenBy(p => p.Id)
            .Take(TopCount)
            .Include(p => p.Translations)
            .ToListAsync();

        return new AnalyticsReport(
            period,
            current,
            before,
            days,
            AllOf<PaymentMethod>(byPayment),
            AllOf<OrderStatus>(byStatus),
            byRegion,
            byCategory,
            byBrand,
            byAgeGroup,
            bySlot,
            byWeekday,
            byHour,
            topByUnits.Select(p => ToRow(p.ProductId, p.Units, p.Sales)).ToList(),
            topByMoney.Select(p => ToRow(p.ProductId, p.Units, p.Sales)).ToList(),
            noSalesProducts.Select(p => new ProductStockRow(p.Id, p.Translations.ResolveName(culture), p.Sku, p.StockQuantity)).ToList(),
            lowStock.Select(p => new ProductStockRow(p.Id, p.Translations.ResolveName(culture), p.Sku, p.StockQuantity)).ToList(),
            await CustomersAsync(start, end),
            await WaitingCartsAsync());
    }

    // ---- Exports ----

    public async Task<byte[]> ExportOrdersCsvAsync(AnalyticsPeriod period, string adminUserId)
    {
        var (start, end) = Bounds(period);

        var orders = await Orders(start, end)
            .OrderBy(o => o.CreatedAt).ThenBy(o => o.Number)
            .Select(o => new
            {
                o.Number, o.CreatedAt, o.Status, o.PaymentMethod, o.PaymentStatus, o.Region,
                Items = o.Items.Sum(i => i.Quantity), o.Subtotal, o.Discount, o.DeliveryFee, o.Total,
                Registered = o.UserId != null, o.CustomerDiscountApplied
            })
            .ToListAsync();

        var file = CsvWriter.Build(
            ["Number", "Placed", "Status", "Payment", "PaymentStatus", "Region", "Items", "Subtotal", "Discount", "DeliveryFee", "Total", "Customer", "CustomerDiscount"],
            orders.Select(o => (IReadOnlyList<object?>)new object?[]
            {
                o.Number, ShopClock.ToShopTime(o.CreatedAt), o.Status.ToString(), o.PaymentMethod.ToString(), o.PaymentStatus.ToString(), o.Region,
                o.Items, o.Subtotal, o.Discount, o.DeliveryFee, o.Total, o.Registered ? "Registered" : "Guest", o.CustomerDiscountApplied ? "yes" : "no"
            }));

        await AuditExportAsync("Export.Orders", period, orders.Count, adminUserId);
        return file;
    }

    public async Task<byte[]> ExportItemsCsvAsync(AnalyticsPeriod period, string adminUserId)
    {
        var (start, end) = Bounds(period);

        var lines = await Items(start, end)
            .OrderBy(i => i.Order.CreatedAt).ThenBy(i => i.Order.Number).ThenBy(i => i.Id)
            .Select(i => new
            {
                i.Order.Number, i.Order.CreatedAt, i.Order.Status, i.Sku, i.NameHy, i.Brand, i.Quantity, i.UnitPrice, i.RegularPrice
            })
            .ToListAsync();

        var file = CsvWriter.Build(
            ["Number", "Placed", "Status", "Sku", "Name", "Brand", "Quantity", "UnitPrice", "RegularPrice", "LineTotal"],
            lines.Select(l => (IReadOnlyList<object?>)new object?[]
            {
                l.Number, ShopClock.ToShopTime(l.CreatedAt), l.Status.ToString(), l.Sku, l.NameHy, l.Brand, l.Quantity, l.UnitPrice, l.RegularPrice, l.UnitPrice * l.Quantity
            }));

        await AuditExportAsync("Export.Items", period, lines.Count, adminUserId);
        return file;
    }

    // ---- helpers ----

    // The UTC moments that enclose the period's Armenian days: from the start of the first day up to (not including) the end of the last.
    private static (DateTime Start, DateTime End) Bounds(AnalyticsPeriod period) =>
        (ShopClock.StartOfDayUtc(period.From), ShopClock.StartOfDayUtc(period.To.AddDays(1)));

    private IQueryable<Order> Orders(DateTime start, DateTime end) =>
        _db.Orders.AsNoTracking().Where(o => o.CreatedAt >= start && o.CreatedAt < end);

    private IQueryable<OrderItem> Items(DateTime start, DateTime end) =>
        _db.OrderItems.AsNoTracking().Where(i => i.Order.CreatedAt >= start && i.Order.CreatedAt < end);

    private async Task<SalesTotals> TotalsAsync(DateTime start, DateTime end)
    {
        var orders = Orders(start, end);

        var sums = await orders.Where(IsConfirmed).GroupBy(o => 1)
            .Select(g => new
            {
                Sales = g.Sum(o => o.Total),
                Orders = g.Count(),
                Discounts = g.Sum(o => o.Discount),
                Fees = g.Sum(o => o.DeliveryFee),
                Registered = g.Count(o => o.UserId != null),
                Discounted = g.Count(o => o.CustomerDiscountApplied)
            })
            .FirstOrDefaultAsync();

        var itemCount = await Items(start, end).Where(ItemIsConfirmed).SumAsync(i => (int?)i.Quantity) ?? 0;
        var cancelled = await orders.CountAsync(o => o.Status == OrderStatus.Cancelled);
        var returns = await _db.ReturnRequests.CountAsync(r => r.CreatedAt >= start && r.CreatedAt < end);
        var refunded = await _db.ReturnRequests
            .Where(r => r.RefundedAt >= start && r.RefundedAt < end)
            .SumAsync(r => (decimal?)r.Order.Total) ?? 0;
        var newAccounts = await _db.Users.CountAsync(u => u.CreatedAt >= start && u.CreatedAt < end);

        return new SalesTotals(
            sums?.Sales ?? 0, sums?.Orders ?? 0, itemCount, sums?.Discounts ?? 0, sums?.Fees ?? 0,
            cancelled, returns, refunded, newAccounts, sums?.Registered ?? 0, sums?.Discounted ?? 0);
    }

    private async Task<CustomerStats> CustomersAsync(DateTime start, DateTime end)
    {
        var confirmed = Orders(start, end).Where(IsConfirmed);

        var buyers = await confirmed.Select(o => o.Email).Distinct().ToListAsync();

        // Returning = had a confirmed order before this period.
        var returning = buyers.Count == 0
            ? 0
            : await _db.Orders.AsNoTracking()
                .Where(o => o.CreatedAt < start && buyers.Contains(o.Email))
                .Where(IsConfirmed)
                .Select(o => o.Email).Distinct().CountAsync();

        var top = await confirmed.GroupBy(o => o.Email)
            .Select(g => new
            {
                Email = g.Key,
                Name = g.OrderByDescending(o => o.CreatedAt).Select(o => o.CustomerName).First(),
                Orders = g.Count(),
                Spent = g.Sum(o => o.Total)
            })
            .OrderByDescending(x => x.Spent).ThenBy(x => x.Email)
            .Take(TopCount)
            .ToListAsync();

        return new CustomerStats(
            buyers.Count,
            buyers.Count - returning,
            returning,
            top.Select(t => new TopCustomerRow(t.Name, t.Email, t.Orders, t.Spent)).ToList());
    }

    private async Task<WaitingCarts> WaitingCartsAsync()
    {
        var limit = _time.GetUtcNow().UtcDateTime.AddHours(-24);

        var lines = await _db.CartItems.AsNoTracking()
            .Where(ci => ci.Cart.UpdatedAt < limit && !ci.Product.IsArchived)
            .Select(ci => new { ci.CartId, ci.Quantity, ci.Product.Price, ci.Product.DiscountPercent })
            .ToListAsync();

        return new WaitingCarts(
            lines.Select(l => l.CartId).Distinct().Count(),
            lines.Sum(l => l.Quantity * ProductRules.GetFinalPrice(l.Price, l.DiscountPercent)));
    }

    private async Task AuditExportAsync(string action, AnalyticsPeriod period, int rows, string adminUserId)
    {
        await AuditWriter.AddAsync(
            _db, adminUserId, _time.GetUtcNow().UtcDateTime, action, "Analytics", $"{period.From:yyyy-MM-dd}..{period.To:yyyy-MM-dd}", $"{rows} rows");
        await _db.SaveChangesAsync();
    }

    private static BreakdownRow Row(string key, IEnumerable<(DateTime Local, decimal Total)> placed)
    {
        var list = placed.ToList();
        return new BreakdownRow(key, list.Sum(p => p.Total), list.Count);
    }

    // Every value of the enum, in its natural order, with zeros where nothing happened.
    private static IReadOnlyList<BreakdownRow> AllOf<T>(Dictionary<string, BreakdownRow> found) where T : struct, Enum =>
        Enum.GetValues<T>().Select(v => found.GetValueOrDefault(v.ToString()) ?? new BreakdownRow(v.ToString(), 0, 0)).ToList();
}

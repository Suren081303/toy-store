namespace Shop.Core.Admin;

// How soon an order to pack has to be delivered, compared with today (an Armenian calendar day).
public enum DeliveryUrgency
{
    // The delivery day has passed and the order is still not packed.
    Late,

    Today,

    Tomorrow,

    Later
}

// An order waiting to be packed (status Ordered).
public record DashboardToPackRow(
    int Number,
    string CustomerName,
    string Phone,
    DateOnly DeliveryDate,
    string DeliverySlot,
    decimal Total,
    bool IsPhoneOrder,
    DeliveryUrgency Urgency);

// A product that is running low or is out of stock.
public record DashboardLowStockRow(int Id, string Sku, string Name, int Stock);

// Confirmed orders (ordered, packed, delivered) of the Armenian day, as in the analytics; NewCustomersToday does not count admins.
public record DashboardSales(
    decimal SalesToday,
    int OrdersToday,
    decimal SalesYesterday,
    int OrdersYesterday,
    int NewCustomersToday);

// Everything the dashboard shows. Days: the last 14 days, oldest first, today last (days without orders are there with zeroes).
// ToPackTotal is the number of all orders to pack; ToPack holds the first few (see AdminDashboardService.ListSize).
public record DashboardData(
    DashboardCounts Counts,
    DashboardSales Sales,
    IReadOnlyList<DayPoint> Days,
    IReadOnlyList<DashboardToPackRow> ToPack,
    int ToPackTotal,
    IReadOnlyList<DashboardLowStockRow> LowStock,
    int LowStockTotal,
    IReadOnlyList<AdminOrderRow> LatestOrders,
    DateTime GeneratedAt);

public interface IAdminDashboardService
{
    Task<DashboardData> GetAsync(string culture);
}

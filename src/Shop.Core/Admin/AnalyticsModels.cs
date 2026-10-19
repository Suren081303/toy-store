using System.Globalization;
using System.Text;

namespace Shop.Core.Admin;

// ---- Periods ----

public enum PeriodPreset
{
    Today,
    Last7Days,
    Last30Days,
    ThisMonth,
    LastMonth,
    Custom
}

// A period of whole Armenian calendar days, both ends included.
public record AnalyticsPeriod(DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber + 1;

    // The period of the same length just before this one (for "+12% compared with before").
    public AnalyticsPeriod Previous => new(From.AddDays(-Days), From.AddDays(-1));
}

public static class AnalyticsPeriods
{
    // A custom period may not be longer than this.
    public const int MaxDays = 366;

    public static AnalyticsPeriod Resolve(PeriodPreset preset, DateOnly? from, DateOnly? to, DateOnly today, out bool customWasInvalid)
    {
        customWasInvalid = false;

        switch (preset)
        {
            case PeriodPreset.Today:
                return new AnalyticsPeriod(today, today);

            case PeriodPreset.Last7Days:
                return new AnalyticsPeriod(today.AddDays(-6), today);

            case PeriodPreset.ThisMonth:
                return new AnalyticsPeriod(new DateOnly(today.Year, today.Month, 1), today);

            case PeriodPreset.LastMonth:
            {
                var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
                return new AnalyticsPeriod(first, first.AddMonths(1).AddDays(-1));
            }

            case PeriodPreset.Custom:
            {
                if (from is not null && to is not null && from <= to && to.Value.DayNumber - from.Value.DayNumber + 1 <= MaxDays)
                {
                    return new AnalyticsPeriod(from.Value, to.Value);
                }

                // Missing, reversed or too long: show the usual 30 days and tell the admin.
                customWasInvalid = true;
                return new AnalyticsPeriod(today.AddDays(-29), today);
            }

            default:
                return new AnalyticsPeriod(today.AddDays(-29), today);
        }
    }
}

// ---- Report ----

// What was sold in a period. "Sales" counts confirmed orders (ordered, packed, delivered) by the day they were placed.
public record SalesTotals(
    decimal Sales,
    int Orders,
    int Items,
    decimal Discounts,
    decimal DeliveryFees,
    int Cancelled,
    int Returns,
    decimal Refunded,
    int NewAccounts,
    int RegisteredOrders,
    int DiscountedOrders)
{
    public decimal AverageOrder => Orders == 0 ? 0 : Math.Round(Sales / Orders, 0, MidpointRounding.AwayFromZero);

    // Money sent back in the period (whenever the order was placed) is taken off.
    public decimal NetSales => Sales - Refunded;

    public int GuestOrders => Orders - RegisteredOrders;

    // Cancelled orders as a percentage of confirmed + cancelled ones (orders still waiting for payment are not counted).
    public decimal CancelRate => Orders + Cancelled == 0 ? 0 : Math.Round(Cancelled * 100m / (Orders + Cancelled), 1);
}

public record DayPoint(DateOnly Day, decimal Sales, int Orders);

// Key is a code or a name, depending on the table (payment method, region code, category name, "0".."6" weekday, "0".."23" hour...).
public record BreakdownRow(string Key, decimal Sales, int Orders, int Units = 0);

public record ProductSalesRow(int ProductId, string Name, string Sku, int Units, decimal Sales);

public record ProductStockRow(int ProductId, string Name, string Sku, int Stock);

public record TopCustomerRow(string Name, string Email, int Orders, decimal Spent);

// Buyers = different customers (by email) who had a confirmed order in the period.
public record CustomerStats(int Buyers, int NewBuyers, int ReturningBuyers, IReadOnlyList<TopCustomerRow> Top);

// Carts with something in them that nobody touched for 24 hours, and what they would cost at today's prices.
public record WaitingCarts(int Carts, decimal Value);

public record AnalyticsReport(
    AnalyticsPeriod Period,
    SalesTotals Current,
    SalesTotals Before,
    IReadOnlyList<DayPoint> Days,
    IReadOnlyList<BreakdownRow> ByPayment,
    IReadOnlyList<BreakdownRow> ByStatus,
    IReadOnlyList<BreakdownRow> ByRegion,
    IReadOnlyList<BreakdownRow> ByCategory,
    IReadOnlyList<BreakdownRow> ByBrand,
    IReadOnlyList<BreakdownRow> ByAgeGroup,
    IReadOnlyList<BreakdownRow> BySlot,
    // Keys "0".."6" (Monday = 0) and "0".."23", Armenian time. All days and hours are present, with zeros.
    IReadOnlyList<BreakdownRow> ByWeekday,
    IReadOnlyList<BreakdownRow> ByHour,
    IReadOnlyList<ProductSalesRow> TopByUnits,
    IReadOnlyList<ProductSalesRow> TopByMoney,
    IReadOnlyList<ProductStockRow> NoSales,
    IReadOnlyList<ProductStockRow> LowStock,
    CustomerStats Customers,
    WaitingCarts Waiting);

public interface IAdminAnalyticsService
{
    Task<AnalyticsReport> GetReportAsync(AnalyticsPeriod period, string culture);

    // Every order placed in the period, one line each, without any personal data. The export is written to the audit log.
    Task<byte[]> ExportOrdersCsvAsync(AnalyticsPeriod period, string adminUserId);

    // Every item of those orders, one line each.
    Task<byte[]> ExportItemsCsvAsync(AnalyticsPeriod period, string adminUserId);
}

// ---- CSV ----

public static class CsvWriter
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    // A table as a CSV file: UTF-8 with the byte-order mark (so Excel reads Armenian and Russian letters correctly),
    // commas between cells, CR LF at the end of each line.
    // Text cells are made safe against "formula injection": a cell that a spreadsheet could read as a formula
    // (starts with = + - @ tab or CR) gets an apostrophe in front. Numbers (int, long, decimal) are written plainly and never touched.
    public static byte[] Build(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<object?>> rows)
    {
        var text = new StringBuilder();
        text.Append(string.Join(',', header.Select(h => Cell(h))));
        text.Append("\r\n");

        foreach (var row in rows)
        {
            text.Append(string.Join(',', row.Select(Cell)));
            text.Append("\r\n");
        }

        return [.. Bom, .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    public static string Cell(object? value)
    {
        switch (value)
        {
            case null:
                return string.Empty;

            case int or long:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;

            case decimal d:
                return d.ToString("0.##", CultureInfo.InvariantCulture);

            case DateTime dt:
                return dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            default:
                var text = value.ToString() ?? string.Empty;

                if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
                {
                    text = "'" + text;
                }

                return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        }
    }
}

// ---- Chart ----

// One bar of the sales chart: a day, or a week for long periods.
public record ChartBar(DateOnly From, DateOnly To, decimal Sales, int Orders);

public static class AnalyticsChart
{
    // Up to this many days every day gets its own bar; longer periods are shown by weeks (7 days from the first day).
    public const int MaxDailyBars = 62;

    public static IReadOnlyList<ChartBar> Bars(IReadOnlyList<DayPoint> days)
    {
        if (days.Count <= MaxDailyBars)
        {
            return days.Select(d => new ChartBar(d.Day, d.Day, d.Sales, d.Orders)).ToList();
        }

        return days
            .Chunk(7)
            .Select(week => new ChartBar(week[0].Day, week[^1].Day, week.Sum(d => d.Sales), week.Sum(d => d.Orders)))
            .ToList();
    }
}

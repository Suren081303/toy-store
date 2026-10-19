namespace Shop.Core.Orders;

// Notes that the system itself writes in an order's history. The customer's cancel email recognises the timeout by this text.
public static class OrderNotes
{
    public const string PaymentTimeout = "The time to pay ran out";
}

// Armenia's regions (Yerevan and the ten marzes). The code is stored on the order; names are translated when shown.
public static class Regions
{
    public static readonly IReadOnlyList<string> Codes =
    [
        "yerevan", "aragatsotn", "ararat", "armavir", "gegharkunik", "lori",
        "kotayk", "shirak", "syunik", "vayots_dzor", "tavush"
    ];

    public static bool IsValid(string? code) => code is not null && Codes.Contains(code);
}

public static class ShopClock
{
    // Armenia is UTC+4 all year (no daylight saving since 2012).
    private static readonly TimeSpan ArmeniaOffset = TimeSpan.FromHours(4);

    public static DateOnly Today(TimeProvider time)
    {
        return DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime.Add(ArmeniaOffset));
    }

    // The UTC moment when an Armenian calendar day starts (for "orders today" and date filters).
    public static DateTime StartOfDayUtc(DateOnly day)
    {
        return DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue) - ArmeniaOffset, DateTimeKind.Utc);
    }

    // A UTC time from the database, as the wall clock in Armenia (for showing to customers).
    public static DateTime ToShopTime(DateTime utc)
    {
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc).Add(ArmeniaOffset);
    }
}

public static class DeliveryRules
{
    // Flat fee, or free when the items total reaches the free-delivery threshold.
    public static decimal GetFee(decimal itemsTotal, decimal flatFee, decimal freeFrom)
    {
        if (freeFrom > 0 && itemsTotal >= freeFrom)
        {
            return 0;
        }

        return flatFee;
    }

    // The days a customer may choose: starting firstDayOffset days from today, daysAhead days in a row.
    public static IReadOnlyList<DateOnly> GetDeliveryDates(DateOnly today, int firstDayOffset, int daysAhead)
    {
        return Enumerable.Range(0, Math.Max(0, daysAhead))
            .Select(i => today.AddDays(firstDayOffset + i))
            .ToList();
    }

    public static bool IsAllowedDate(DateOnly date, DateOnly today, int firstDayOffset, int daysAhead)
    {
        var first = today.AddDays(firstDayOffset);
        var last = today.AddDays(firstDayOffset + Math.Max(0, daysAhead) - 1);

        return date >= first && date <= last;
    }
}

public static class OrderStatusRules
{
    // AwaitingPayment -> Ordered | Cancelled
    // Ordered         -> Packed | Cancelled
    // Packed          -> Delivered | Cancelled
    // Delivered and Cancelled are final.
    public static IReadOnlyList<OrderStatus> AllowedNext(OrderStatus from) => from switch
    {
        OrderStatus.AwaitingPayment => [OrderStatus.Ordered, OrderStatus.Cancelled],
        OrderStatus.Ordered => [OrderStatus.Packed, OrderStatus.Cancelled],
        OrderStatus.Packed => [OrderStatus.Delivered, OrderStatus.Cancelled],
        _ => []
    };

    public static bool CanChange(OrderStatus from, OrderStatus to) => AllowedNext(from).Contains(to);
}

public static class ReturnStatusRules
{
    // Requested -> Rejected | Returned (the goods came back); Returned -> Refunded (the money went back).
    // Rejected and Refunded are final.
    public static IReadOnlyList<ReturnStatus> AllowedNext(ReturnStatus from) => from switch
    {
        ReturnStatus.Requested => [ReturnStatus.Rejected, ReturnStatus.Returned],
        ReturnStatus.Returned => [ReturnStatus.Refunded],
        _ => []
    };

    public static bool CanChange(ReturnStatus from, ReturnStatus to) => AllowedNext(from).Contains(to);
}

public static class CustomerDiscountRules
{
    // A registered customer gets the 10% price on their first 5 orders. Cancelled orders do not count.
    public const int MaxDiscountedOrders = 5;

    public static int OrdersLeft(int notCancelledOrders) => Math.Max(0, MaxDiscountedOrders - notCancelledOrders);

    public static bool IsEligible(int notCancelledOrders) => OrdersLeft(notCancelledOrders) > 0;

    // A personal discount (set by an admin for one customer) is a whole percent from 1 to MaxPersonalPercent.
    public const int MaxPersonalPercent = 50;

    public static bool IsValidPersonalPercent(int percent) => percent is >= 1 and <= MaxPersonalPercent;

    // It counts while it is set and its last day (an Armenian calendar day, or none) has not passed.
    public static bool IsPersonalActive(int? percent, DateOnly? until, DateOnly today) =>
        percent is { } p && IsValidPersonalPercent(p) && (until is null || until.Value >= today);
}

public static class CustomerMessageRules
{
    public const int MaxSubjectLength = 150;

    public const int MaxBodyLength = 2000;

    // So a customer is never flooded: at most this many messages to one customer in 24 hours.
    public const int MaxPerCustomerPerDay = 5;
}

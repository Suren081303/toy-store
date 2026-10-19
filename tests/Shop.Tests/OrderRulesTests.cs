using Shop.Core.Orders;

namespace Shop.Tests;

public class OrderRulesTests
{
    // ---- Delivery fee ----

    [Theory]
    [InlineData(5000, 1500)]
    [InlineData(19999, 1500)]
    [InlineData(20000, 0)]
    [InlineData(35000, 0)]
    public void DeliveryFee_IsFlat_UntilTheFreeThresholdIsReached(int itemsTotal, int expected)
    {
        Assert.Equal(expected, DeliveryRules.GetFee(itemsTotal, flatFee: 1500, freeFrom: 20000));
    }

    [Fact]
    public void DeliveryFee_WithoutAThreshold_IsAlwaysTheFlatFee()
    {
        Assert.Equal(1500, DeliveryRules.GetFee(1_000_000, flatFee: 1500, freeFrom: 0));
    }

    // ---- Delivery dates ----

    [Fact]
    public void DeliveryDates_StartAtTheOffset_AndRunForTheGivenDays()
    {
        var today = new DateOnly(2026, 6, 1);

        var dates = DeliveryRules.GetDeliveryDates(today, firstDayOffset: 1, daysAhead: 3);

        Assert.Equal([new DateOnly(2026, 6, 2), new DateOnly(2026, 6, 3), new DateOnly(2026, 6, 4)], dates);
    }

    [Fact]
    public void DeliveryDates_WithZeroOrNegativeDays_AreEmpty()
    {
        var today = new DateOnly(2026, 6, 1);

        Assert.Empty(DeliveryRules.GetDeliveryDates(today, 1, 0));
        Assert.Empty(DeliveryRules.GetDeliveryDates(today, 1, -5));
    }

    [Theory]
    [InlineData("2026-06-01", false)] // today: too early (offset is 1)
    [InlineData("2026-06-02", true)]  // first day
    [InlineData("2026-06-04", true)]  // last day
    [InlineData("2026-06-05", false)] // one day too late
    public void IsAllowedDate_AcceptsOnlyTheChoosableDays(string date, bool expected)
    {
        var today = new DateOnly(2026, 6, 1);

        Assert.Equal(expected, DeliveryRules.IsAllowedDate(DateOnly.Parse(date), today, firstDayOffset: 1, daysAhead: 3));
    }

    [Fact]
    public void IsAllowedDate_AgreesWithGetDeliveryDates()
    {
        var today = new DateOnly(2026, 6, 1);
        var allowed = DeliveryRules.GetDeliveryDates(today, 2, 7);

        for (var offset = -3; offset < 15; offset++)
        {
            var date = today.AddDays(offset);
            Assert.Equal(allowed.Contains(date), DeliveryRules.IsAllowedDate(date, today, 2, 7));
        }
    }

    // ---- Status rules ----

    [Theory]
    [InlineData(OrderStatus.AwaitingPayment, OrderStatus.Ordered, true)]
    [InlineData(OrderStatus.AwaitingPayment, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.AwaitingPayment, OrderStatus.Packed, false)]
    [InlineData(OrderStatus.Ordered, OrderStatus.Packed, true)]
    [InlineData(OrderStatus.Ordered, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Ordered, OrderStatus.Delivered, false)]
    [InlineData(OrderStatus.Packed, OrderStatus.Delivered, true)]
    [InlineData(OrderStatus.Packed, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Packed, OrderStatus.Ordered, false)]
    [InlineData(OrderStatus.Delivered, OrderStatus.Cancelled, false)]
    [InlineData(OrderStatus.Delivered, OrderStatus.Packed, false)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Ordered, false)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Delivered, false)]
    public void StatusChange_FollowsTheAllowedPath(OrderStatus from, OrderStatus to, bool expected)
    {
        Assert.Equal(expected, OrderStatusRules.CanChange(from, to));
    }

    [Theory]
    [InlineData(OrderStatus.Delivered)]
    [InlineData(OrderStatus.Cancelled)]
    public void FinalStatuses_HaveNoNextStatus(OrderStatus status)
    {
        Assert.Empty(OrderStatusRules.AllowedNext(status));
    }

    [Fact]
    public void NoStatus_CanChangeToItself()
    {
        foreach (var status in Enum.GetValues<OrderStatus>())
        {
            Assert.False(OrderStatusRules.CanChange(status, status));
        }
    }

    // ---- First-5-orders discount ----

    [Theory]
    [InlineData(0, 5, true)]
    [InlineData(4, 1, true)]
    [InlineData(5, 0, false)]
    [InlineData(9, 0, false)]
    public void CustomerDiscount_IsGivenOnTheFirstFiveOrders(int notCancelledOrders, int left, bool eligible)
    {
        Assert.Equal(left, CustomerDiscountRules.OrdersLeft(notCancelledOrders));
        Assert.Equal(eligible, CustomerDiscountRules.IsEligible(notCancelledOrders));
    }

    // ---- Armenian time ----

    [Fact]
    public void Today_IsTheDateInArmenia_NotInUtc()
    {
        // 22:00 UTC on June 1 is already 02:00 on June 2 in Armenia (UTC+4).
        var time = new FixedTimeProvider(new DateTime(2026, 6, 1, 22, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateOnly(2026, 6, 2), ShopClock.Today(time));
    }

    [Fact]
    public void ToShopTime_Adds_FourHours()
    {
        var utc = new DateTime(2026, 6, 1, 10, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 6, 1, 14, 30, 0), ShopClock.ToShopTime(utc));
    }

    // ---- Regions ----

    [Theory]
    [InlineData("yerevan", true)]
    [InlineData("vayots_dzor", true)]
    [InlineData("Yerevan", false)]
    [InlineData("moscow", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Region_MustBeOneOfTheKnownCodes(string? code, bool expected)
    {
        Assert.Equal(expected, Regions.IsValid(code));
    }

    [Fact]
    public void Regions_AreYerevanAndTenMarzes_WithoutDuplicates()
    {
        Assert.Equal(11, Regions.Codes.Count);
        Assert.Equal(Regions.Codes.Count, Regions.Codes.Distinct().Count());
    }
}

using Shop.Core.Catalog;

namespace Shop.Tests;

public class ProductRulesTests
{
    [Theory]
    [InlineData(10000, 0, 10000)]
    [InlineData(10000, 15, 8500)]
    [InlineData(12900, 10, 11610)]
    [InlineData(8999, 15, 7649)]    // 7649.15 rounds down
    [InlineData(10001, 50, 5001)]   // 5000.5 rounds up
    [InlineData(5000, 100, 0)]
    public void GetFinalPrice_RoundsToWholeDram(decimal price, int discountPercent, decimal expected)
    {
        Assert.Equal(expected, ProductRules.GetFinalPrice(price, discountPercent));
    }

    [Theory]
    [InlineData(10000, 0, 9000, true)]      // no product discount: the 10% customer price
    [InlineData(10000, 15, 8500, false)]    // product discount is better: it stays, customer price not applied
    [InlineData(10000, 10, 9000, false)]    // equal: it counts as the product discount
    [InlineData(10000, 20, 8000, false)]
    [InlineData(12900, 0, 11610, true)]
    [InlineData(10001, 0, 9001, true)]      // 9000.9 rounds up
    public void GetUnitPrice_ForARegisteredCustomer_IsTheLowerOfProductAndCustomerPrice(
        decimal price, int discountPercent, decimal expectedUnitPrice, bool expectedCustomerApplied)
    {
        var unitPrice = ProductRules.GetUnitPrice(price, discountPercent, customerDiscount: true, out var applied);

        Assert.Equal(expectedUnitPrice, unitPrice);
        Assert.Equal(expectedCustomerApplied, applied);
    }

    [Theory]
    [InlineData(10000, 0, 10000)]
    [InlineData(10000, 15, 8500)]
    public void GetUnitPrice_ForAGuest_IsTheProductPrice(decimal price, int discountPercent, decimal expectedUnitPrice)
    {
        var unitPrice = ProductRules.GetUnitPrice(price, discountPercent, customerDiscount: false, out var applied);

        Assert.Equal(expectedUnitPrice, unitPrice);
        Assert.False(applied);
    }

    [Fact]
    public void IsNew_PinnedProduct_IsNewEvenWhenOld()
    {
        var newSince = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(ProductRules.IsNew(true, newSince.AddDays(-100), newSince));
    }

    [Fact]
    public void IsNew_CreatedExactlyAtLimit_IsNew()
    {
        var newSince = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(ProductRules.IsNew(false, newSince, newSince));
    }

    [Fact]
    public void IsNew_CreatedJustBeforeLimit_IsNotNew()
    {
        var newSince = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.False(ProductRules.IsNew(false, newSince.AddTicks(-1), newSince));
    }
}

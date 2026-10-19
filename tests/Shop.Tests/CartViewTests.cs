using Shop.Core.Carts;

namespace Shop.Tests;

public class CartViewTests
{
    private static CartLine Line(
        int id, int quantity, decimal regular, decimal unit, CartLineStatus status = CartLineStatus.Available, bool customerApplied = false)
    {
        return new CartLine(id, "Product " + id, "Brand", null, quantity, regular, unit, 0, customerApplied, 10, 10, status);
    }

    [Fact]
    public void Totals_CountOnlyTheProductsThatCanBeBought()
    {
        var view = new CartView(
            new[]
            {
                Line(1, quantity: 2, regular: 10000, unit: 9000, customerApplied: true),
                Line(2, quantity: 1, regular: 5000, unit: 5000),
                Line(3, quantity: 3, regular: 7000, unit: 7000, status: CartLineStatus.OutOfStock),
                Line(4, quantity: 1, regular: 8000, unit: 8000, status: CartLineStatus.NotForSale)
            },
            Array.Empty<CartNotice>(),
            CustomerDiscountActive: true);

        Assert.Equal(3, view.ItemCount);
        Assert.Equal(25000m, view.RegularTotal);
        Assert.Equal(23000m, view.Total);
        Assert.Equal(2000m, view.Savings);
        Assert.Equal(2, view.CustomerDiscountItemCount);
        Assert.True(view.HasUnavailableLines);
    }

    [Fact]
    public void EmptyCart_HasZeroTotals()
    {
        var view = CartView.Empty(customerDiscountActive: false);

        Assert.Equal(0, view.ItemCount);
        Assert.Equal(0m, view.Total);
        Assert.Equal(0m, view.Savings);
        Assert.False(view.HasUnavailableLines);
    }

    [Fact]
    public void LineTotals_MultiplyByTheQuantity()
    {
        var line = Line(1, quantity: 3, regular: 10000, unit: 8500);

        Assert.Equal(25500m, line.LineTotal);
        Assert.Equal(30000m, line.RegularLineTotal);
    }
}

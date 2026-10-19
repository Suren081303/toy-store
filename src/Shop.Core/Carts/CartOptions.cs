namespace Shop.Core.Carts;

public class CartOptions
{
    public const string SectionName = "Cart";

    // Most of one product in a cart (never more than the stock).
    public int MaxQuantityPerProduct { get; set; } = 10;

    // Most different products in a cart.
    public int MaxLines { get; set; } = 30;

    // Guest carts not touched for this many days are deleted.
    public int GuestCartDays { get; set; } = 30;
}

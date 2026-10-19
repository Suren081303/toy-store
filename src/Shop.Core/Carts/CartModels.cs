namespace Shop.Core.Carts;

// Who a cart belongs to: a signed-in customer, or a guest identified by the id in the cart cookie (null when there is no cookie yet).
public record CartOwner(string? UserId, Guid? GuestCartId)
{
    public bool IsSignedIn => UserId is not null;
}

public enum CartLineStatus
{
    Available,
    OutOfStock,
    NotForSale
}

public record CartLine(
    int ProductId,
    string Name,
    string Brand,
    string? ImagePath,
    int Quantity,
    decimal RegularPrice,
    decimal UnitPrice,
    int DiscountPercent,
    bool CustomerDiscountApplied,
    int StockQuantity,
    int MaxQuantity,
    CartLineStatus Status)
{
    public bool IsAvailable => Status == CartLineStatus.Available;

    public decimal LineTotal => UnitPrice * Quantity;

    public decimal RegularLineTotal => RegularPrice * Quantity;
}

public enum CartNoticeKind
{
    // The stock dropped below the quantity in the cart, so the quantity was reduced.
    QuantityReduced
}

public record CartNotice(CartNoticeKind Kind, string ProductName, int Quantity);

// CustomerDiscountOrdersLeft: how many more orders get the registered-customer price (0 for guests and after the 5th order).
public record CartView(
    IReadOnlyList<CartLine> Lines,
    IReadOnlyList<CartNotice> Notices,
    bool CustomerDiscountActive,
    int CustomerDiscountOrdersLeft = 0)
{
    public static CartView Empty(bool customerDiscountActive, int customerDiscountOrdersLeft = 0) =>
        new(Array.Empty<CartLine>(), Array.Empty<CartNotice>(), customerDiscountActive, customerDiscountOrdersLeft);

    // Totals count only the products that can be bought now.
    public int ItemCount => Lines.Where(l => l.IsAvailable).Sum(l => l.Quantity);

    public decimal RegularTotal => Lines.Where(l => l.IsAvailable).Sum(l => l.RegularLineTotal);

    public decimal Total => Lines.Where(l => l.IsAvailable).Sum(l => l.LineTotal);

    public decimal Savings => RegularTotal - Total;

    public int CustomerDiscountItemCount => Lines.Where(l => l.IsAvailable && l.CustomerDiscountApplied).Sum(l => l.Quantity);

    public bool HasUnavailableLines => Lines.Any(l => !l.IsAvailable);
}

public enum CartChangeStatus
{
    Added,

    // Done, but the quantity was held back by the stock or the per-product limit.
    LimitReached,

    // The product does not exist, is not for sale, or is out of stock.
    NotAvailable,

    TooManyLines,

    NotInCart
}

// The result of adding to or changing a cart. NewGuestCartId is set when a guest cart was just created (the site then sets the cookie).
public record CartChange(CartChangeStatus Status, int Quantity, int Limit, int Count, Guid? NewGuestCartId);

namespace Shop.Core.Orders;

public record SlotOption(int Id, string Label);

public record StoreSettingsView(
    decimal DeliveryFee,
    decimal FreeDeliveryFrom,
    int DeliveryDaysAhead,
    int DeliveryFirstDayOffset,
    int ReturnWindowDays,
    IReadOnlyList<SlotOption> Slots,
    string BankName,
    string BankAccount,
    string BankRecipient);

// What the checkout page sends.
public class CheckoutInput
{
    // One-time id created when the checkout page is shown.
    public Guid CheckoutId { get; set; }

    public string CustomerName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    // Already in the +374... form.
    public string Phone { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public string Street { get; set; } = string.Empty;

    public string Building { get; set; } = string.Empty;

    public string? Apartment { get; set; }

    public string? Comment { get; set; }

    public DateOnly DeliveryDate { get; set; }

    public int DeliverySlotId { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    // The total the customer saw. If the real total is different now, the order is not placed.
    public decimal ExpectedTotal { get; set; }
}

public enum PlaceOrderStatus
{
    Placed,

    // The same checkout id was already used: the existing order is returned.
    AlreadyPlaced,

    EmptyCart,

    // Some products in the cart can no longer be bought.
    UnavailableItems,

    // The total changed since the customer saw it (NewTotal has the new one).
    PriceChanged,

    // Somebody else took the stock first (Problems says which products and how many are left).
    OutOfStock,

    InvalidDelivery,

    // An order made by an admin cannot be paid by card (there is no customer at the bank's page).
    InvalidPayment
}

public record StockProblem(int ProductId, string Name, int Available);

public record PlaceOrderResult(
    PlaceOrderStatus Status,
    OrderSummary? Order = null,
    decimal NewTotal = 0,
    IReadOnlyList<StockProblem>? Problems = null);

public record OrderLine(
    int ProductId,
    string Name,
    string Brand,
    string? ImagePath,
    int Quantity,
    decimal RegularPrice,
    decimal UnitPrice,
    int DiscountPercent,
    bool CustomerDiscountApplied)
{
    public decimal LineTotal => UnitPrice * Quantity;
}

public record ReturnInfo(ReturnStatus Status, string Reason, DateTime CreatedAt, string? AdminNote);

public record OrderSummary(
    int Number,
    string AccessKey,
    DateTime CreatedAt,
    OrderStatus Status,
    PaymentMethod PaymentMethod,
    PaymentStatus PaymentStatus,
    string CustomerName,
    string Email,
    string Phone,
    string Region,
    string City,
    string Street,
    string Building,
    string? Apartment,
    string? Comment,
    DateOnly DeliveryDate,
    string DeliverySlot,
    decimal Subtotal,
    decimal Discount,
    decimal DeliveryFee,
    decimal Total,
    bool CustomerDiscountApplied,
    string Culture,
    IReadOnlyList<OrderLine> Lines,
    DateTime? DeliveredAt,
    ReturnInfo? Return,
    bool CanRequestReturn);

public record OrderListItem(int Number, DateTime CreatedAt, OrderStatus Status, decimal Total, int ItemCount);

// The contact and address of a customer's latest order (to prefill the next checkout).
public record LastAddress(
    string CustomerName,
    string Phone,
    string Region,
    string City,
    string Street,
    string Building,
    string? Apartment);

public enum ChangeStatusResult
{
    Changed,

    NotFound,

    NotAllowed
}

public enum ReturnRequestResult
{
    Created,

    NotFound,

    NotDelivered,

    WindowClosed,

    AlreadyRequested,

    InvalidReason
}

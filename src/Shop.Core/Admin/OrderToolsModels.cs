using Shop.Core.Orders;

namespace Shop.Core.Admin;

// ---- Internal notes, editing, packing slips ----

public record AdminNoteRow(DateTime At, string Text, string ByEmail);

// What an admin may still change in an order, by its status.
public enum OrderEditScope
{
    // Delivered or cancelled: nothing (internal notes can still be added).
    None,

    // Packed: the order is ready to go, so only the comment can change.
    CommentOnly,

    // Awaiting payment or ordered: contact, address, delivery day and time, comment.
    Full
}

public static class OrderEditRules
{
    public const int MaxNoteLength = 1000;

    public static OrderEditScope ScopeOf(OrderStatus status) => status switch
    {
        OrderStatus.AwaitingPayment or OrderStatus.Ordered => OrderEditScope.Full,
        OrderStatus.Packed => OrderEditScope.CommentOnly,
        _ => OrderEditScope.None
    };
}

// The form of the "Edit order" page. Items and prices are never edited (cancel the order and make a new one).
public class AdminOrderEdit
{
    public string CustomerName { get; set; } = string.Empty;

    // Empty is allowed (an order made by phone without an email).
    public string Email { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public string Street { get; set; } = string.Empty;

    public string Building { get; set; } = string.Empty;

    public string? Apartment { get; set; }

    public string? Comment { get; set; }

    public DateOnly DeliveryDate { get; set; }

    public int DeliverySlotId { get; set; }
}

public enum EditOrderStatus
{
    Saved,

    // Nothing was different from what is saved.
    NoChange,

    NotFound,

    // The status does not allow this change (or a field other than the comment was changed on a packed order).
    NotEditable,

    // Region, day or time is not valid.
    InvalidDelivery,

    // Name, phone or email is not valid.
    InvalidContact
}

public enum AddNoteStatus
{
    Added,

    NotFound,

    // Empty or longer than the limit.
    Invalid
}

public record PackingSlipLine(string Sku, string Brand, string Name, int Quantity, decimal UnitPrice, decimal LineTotal);

// Everything printed on a packing slip. AmountToCollect: what the courier takes in cash (0 when nothing is to be collected).
public record PackingSlip(
    int Number,
    DateTime CreatedAt,
    bool IsPhoneOrder,
    OrderStatus Status,
    PaymentMethod PaymentMethod,
    PaymentStatus PaymentStatus,
    DateOnly DeliveryDate,
    string DeliverySlot,
    string CustomerName,
    string Phone,
    string Email,
    string Region,
    string City,
    string Street,
    string Building,
    string? Apartment,
    string? Comment,
    IReadOnlyList<PackingSlipLine> Lines,
    decimal Subtotal,
    decimal Discount,
    decimal DeliveryFee,
    decimal Total,
    decimal AmountToCollect);

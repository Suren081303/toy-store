using Shop.Core.Orders;
using Shop.Core.Payments;

namespace Shop.Core.Admin;

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

// ---- Dashboard ----

// A product with this much stock or less counts as "low".
public static class StockLevels
{
    public const int Low = 3;
}

public record DashboardCounts(
    int OrdersToday,
    int ToPack,
    int AwaitingPayment,
    int OpenReturns,
    int RefundsNeeded,
    int LowStock,
    // Emails that could not be sent after all attempts.
    int FailedEmails);

// ---- Orders ----

public class AdminOrderFilter
{
    // An order number, or part of a name, email or phone.
    public string? Query { get; set; }

    public OrderStatus? Status { get; set; }

    public PaymentMethod? Method { get; set; }

    // Dates are Armenian calendar days.
    public DateOnly? From { get; set; }

    public DateOnly? To { get; set; }

    // Only orders with a payment that must be refunded by hand.
    public bool RefundNeeded { get; set; }

    public int Page { get; set; } = 1;
}

public record AdminOrderRow(
    int Number,
    DateTime CreatedAt,
    OrderStatus Status,
    PaymentMethod Method,
    PaymentStatus PaymentStatus,
    string CustomerName,
    string Phone,
    decimal Total,
    bool NeedsRefund,
    bool OpenReturn,
    int NoteCount = 0,
    bool IsPhoneOrder = false);

public record AdminEventRow(DateTime At, OrderStatus Status, string? Note, string? ByEmail);

public record AdminPaymentRow(int Id, string Provider, DateTime CreatedAt, decimal Amount, PaymentAttemptStatus Status, bool NeedsRefund);

public record AdminOrderDetail(
    OrderSummary Order,
    IReadOnlyList<AdminEventRow> Events,
    IReadOnlyList<AdminPaymentRow> Payments,
    // The statuses the admin may set by hand (a card order becomes Ordered only by the bank's answer).
    IReadOnlyList<OrderStatus> NextStatuses,
    bool CanMarkTransferReceived,
    // The newest return request of the order, if any.
    int? ReturnId,
    IReadOnlyList<AdminNoteRow>? Notes = null,
    bool IsPhoneOrder = false,
    string? CreatedByEmail = null,
    OrderEditScope EditScope = OrderEditScope.None);

public enum RefundDoneResult
{
    Done,

    NotFound,

    // The payment does not need a refund (any more).
    NotNeeded
}

public interface IAdminOrderService
{
    Task<DashboardCounts> GetDashboardAsync();

    Task<PagedResult<AdminOrderRow>> ListAsync(AdminOrderFilter filter);

    Task<AdminOrderDetail?> GetAsync(int number);

    // The money of a payment that could not be used was sent back by hand.
    Task<RefundDoneResult> MarkRefundDoneAsync(int paymentId, string adminUserId);

    // Changes contact, address, delivery day/time and comment, as far as the order's status allows (see OrderEditRules).
    // Written to the order's history and the audit log; a changed day, time or address queues an email to the customer.
    Task<EditOrderStatus> UpdateAsync(int number, AdminOrderEdit edit, string adminUserId);

    // An internal note (only admins see it).
    Task<AddNoteStatus> AddNoteAsync(int number, string text, string adminUserId);

    Task<PackingSlip?> GetSlipAsync(int number);

    // The slips of the orders that match the filter, in delivery order (day, time, number); at most max.
    Task<IReadOnlyList<PackingSlip>> GetSlipsAsync(AdminOrderFilter filter, int max);
}

// ---- Returns ----

public record AdminReturnRow(int Id, int OrderNumber, string CustomerName, DateTime CreatedAt, ReturnStatus Status, decimal OrderTotal, string Reason);

public record AdminReturnDetail(
    int Id,
    ReturnStatus Status,
    string Reason,
    string? AdminNote,
    DateTime CreatedAt,
    DateTime? DecidedAt,
    OrderSummary Order,
    IReadOnlyList<ReturnStatus> NextStatuses);

public enum ChangeReturnResult
{
    Changed,

    NotFound,

    NotAllowed
}

public interface IAdminReturnService
{
    Task<PagedResult<AdminReturnRow>> ListAsync(ReturnStatus? status, int page);

    Task<AdminReturnDetail?> GetAsync(int id);

    // Returned + restock: the items go back into stock. Refunded: a paid order is marked as refunded.
    Task<ChangeReturnResult> ChangeStatusAsync(int id, ReturnStatus newStatus, string? note, bool restock, string adminUserId);
}

// ---- Settings ----

public class AdminSettingsInput
{
    public decimal DeliveryFee { get; set; }

    public decimal FreeDeliveryFrom { get; set; }

    public int DeliveryDaysAhead { get; set; }

    public int DeliveryFirstDayOffset { get; set; }

    public int ReturnWindowDays { get; set; }

    public string BankName { get; set; } = string.Empty;

    public string BankAccount { get; set; } = string.Empty;

    public string BankRecipient { get; set; } = string.Empty;
}

public record AdminSlot(int Id, string Label, int SortOrder, bool IsActive);

public record AdminSettingsView(AdminSettingsInput Settings, IReadOnlyList<AdminSlot> Slots);

public enum SaveSlotResult
{
    Saved,

    NotFound,

    // At least one time slot must stay active, or customers could not choose a delivery time.
    LastActiveSlot
}

public interface IAdminSettingsService
{
    Task<AdminSettingsView> GetAsync();

    Task SaveSettingsAsync(AdminSettingsInput input, string adminUserId);

    // id = null adds a new slot.
    Task<SaveSlotResult> SaveSlotAsync(int? id, string label, int sortOrder, bool isActive, string adminUserId);
}

using Shop.Core.Admin;

namespace Shop.Core.Notifications;

public enum NotificationType
{
    // A new order to prepare (cash or bank transfer when placed, card when paid).
    NewOrder,

    ReturnRequested,

    // Money arrived that the order cannot use: send it back by hand.
    RefundNeeded,

    // A product dropped to StockLevels.Low or less (once, when it crosses that line).
    LowStock
}

public enum EmailKind
{
    // To the customer.
    OrderConfirmation,
    OrderPacked,
    OrderDelivered,
    OrderCancelled,
    TransferReceived,
    ReturnRejected,
    ReturnReceived,
    ReturnRefunded,

    // The delivery day, time or address of the order was changed by an admin.
    OrderUpdated,

    // A message an admin wrote to a customer (see CustomerMessage).
    CustomerMessage,

    // To the shop (the address in Shop:Email).
    ShopNewOrder,
    ShopReturnRequested,
    ShopRefundNeeded
}

public enum OutboxStatus
{
    // Not sent yet (new, or waiting for the next try).
    Pending,

    Sent,

    // Not sent on purpose: the order has no email address (an order made by phone without one).
    Skipped,

    // Gave up after the last try. An admin can put it back with "Retry".
    Failed
}

public static class EmailKinds
{
    public static bool IsForShop(EmailKind kind) => kind is EmailKind.ShopNewOrder or EmailKind.ShopReturnRequested or EmailKind.ShopRefundNeeded;
}

public static class OutboxRules
{
    // The sender tries this many times in all, waiting longer after each failure.
    public const int MaxAttempts = 5;

    // Pause after the n-th failed attempt (1-based): 1, 5, 15, 60 minutes.
    public static TimeSpan RetryDelay(int failedAttempts) => failedAttempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(60)
    };

    public const int NotificationDays = 90;

    public const int SentEmailDays = 30;
}

public record NotificationRow(
    int Id,
    DateTime CreatedAt,
    NotificationType Type,
    int? OrderNumber,
    int? ReturnId,
    int? ProductId,
    decimal? Amount,
    string? Name,
    int? Stock,
    bool IsRead);

public record NotificationSummary(int Unread, IReadOnlyList<NotificationRow> Latest);

public interface IAdminNotificationService
{
    Task<int> UnreadCountAsync();

    Task<NotificationSummary> SummaryAsync(int latest);

    // Newest first, 30 per page.
    Task<PagedResult<NotificationRow>> ListAsync(bool unreadOnly, int page);

    Task<NotificationRow?> GetAsync(int id);

    // Safe to call twice.
    Task MarkReadAsync(int id, string adminUserId);

    // Returns how many were unread.
    Task<int> MarkAllReadAsync(string adminUserId);
}

public record EmailRow(
    int Id,
    DateTime CreatedAt,
    EmailKind Kind,
    int OrderNumber,
    OutboxStatus Status,
    int Attempts,
    DateTime NextAttemptAt,
    DateTime? SentAt,
    string? ToAddress,
    string? LastError);

public interface IAdminEmailService
{
    // Newest first, 30 per page.
    Task<PagedResult<EmailRow>> ListAsync(OutboxStatus? status, int page);

    // Puts a failed email back in the queue with a fresh set of attempts. False when it is not a failed one.
    Task<bool> RetryAsync(int id, string adminUserId);

    Task<int> FailedCountAsync();
}

// Sends the emails that are due. Called by the background job (and by the tests).
public interface IEmailOutboxProcessor
{
    // Returns how many emails it dealt with (sent or failed this round).
    Task<int> ProcessAsync();
}

// Deletes old notifications and emails (called once a day).
public interface INotificationCleanup
{
    Task<(int Notifications, int Emails)> CleanAsync();
}

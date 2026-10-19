using Shop.Core.Notifications;

namespace Shop.Core.Admin;

// ---- Customers (registered shop accounts; admins are not listed) ----

public enum CustomerStatusFilter
{
    All,
    Active,
    Blocked
}

public enum CustomerSort
{
    // Newest accounts first.
    Newest,
    Name,
    Orders,
    Spent,
    LastOrder
}

public class AdminCustomerFilter
{
    // Part of the name, email or phone.
    public string? Query { get; set; }

    public CustomerStatusFilter Status { get; set; }

    public CustomerSort Sort { get; set; }

    public int Page { get; set; } = 1;
}

// A message as shown in the customer's row: what was sent, and how the email is doing (null while the queue has no row).
public record AdminMessageRow(int Id, DateTime CreatedAt, string Subject, string Body, string SentByEmail, OutboxStatus? EmailStatus);

// Orders, TotalSpent and LastOrderAt count only confirmed orders (Ordered, Packed, Delivered), like the analytics;
// TotalSpent is what they paid minus the money sent back for returns.
// PersonalDiscountPercent is null when none is set or it has run out (PersonalDiscountUntil then still shows the last day).
public record AdminCustomerRow(
    string Id,
    string FullName,
    string Email,
    string Phone,
    bool EmailConfirmed,
    DateTime CreatedAt,
    int Orders,
    decimal TotalSpent,
    DateTime? LastOrderAt,
    bool IsBlocked,
    int? PersonalDiscountPercent,
    DateOnly? PersonalDiscountUntil,
    bool PersonalDiscountActive,
    IReadOnlyList<AdminMessageRow> Messages);

public enum CustomerActionStatus
{
    Done,

    NotFound,

    // The account is an admin account (those are managed on the Admins page).
    NotACustomer,

    // Block: already blocked; unblock: not blocked.
    NothingToChange,

    // A percent outside 1-50, or an end day that is already past.
    InvalidDiscount,

    // Subject or text empty or too long.
    InvalidMessage,

    // The limit of messages to one customer in 24 hours is used up.
    TooManyMessages,

    // The customer's email was never confirmed, so it is not proven to be theirs.
    EmailNotConfirmed
}

// A customer as the phone-order pages need them.
public record PhoneOrderCustomer(
    string Id,
    string FullName,
    string Email,
    string Phone,
    bool EmailConfirmed,
    bool IsBlocked);

public interface IAdminCustomerService
{
    // One customer by id, or null when there is no such account or it is an admin account.
    Task<PhoneOrderCustomer?> FindAsync(string userId);

    // 25 per page.
    Task<PagedResult<AdminCustomerRow>> ListAsync(AdminCustomerFilter filter);

    // The customer cannot sign in until unblocked, and a session they have open ends at once. Audited.
    Task<CustomerActionStatus> BlockAsync(string userId, string byAdminId);

    Task<CustomerActionStatus> UnblockAsync(string userId, string byAdminId);

    // percent 1-50 sets the personal discount (until: last Armenian day it counts, or null); percent null or 0 removes it. Audited.
    Task<CustomerActionStatus> SetDiscountAsync(string userId, int? percent, DateOnly? until, string byAdminId);

    // Saves the message and queues the email (plain text). culture: the language of the email's frame. Audited.
    Task<CustomerActionStatus> SendMessageAsync(string userId, string subject, string body, string culture, string byAdminId);
}

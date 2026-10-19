using Shop.Core.Orders;

namespace Shop.Core.Payments;

// The result of one card payment attempt.
public enum PaymentAttemptStatus
{
    // Started, the bank has not answered yet.
    Created,

    Paid,

    Failed,

    // The customer cancelled on the bank's page, or a newer attempt replaced this one.
    Cancelled
}

// appsettings section "Payments".
public class PaymentOptions
{
    public const string SectionName = "Payments";

    // The card payment provider: "Fake" (Development only) or, later, a real bank. Empty = card payment is off.
    public string Provider { get; set; } = string.Empty;

    // Signs the callbacks of the fake bank. Not a real secret: the fake bank only exists in Development.
    public string FakeSecret { get; set; } = string.Empty;

    // An unpaid card order is cancelled after this many minutes.
    public int CardTimeoutMinutes { get; set; } = 30;

    // An unpaid bank transfer order is cancelled after this many days.
    public int BankTransferTimeoutDays { get; set; } = 3;
}

public static class PaymentRules
{
    // When an unpaid order is cancelled automatically. Null for cash on delivery (it is never "unpaid").
    public static DateTime? GetDeadline(PaymentMethod method, DateTime createdAtUtc, PaymentOptions options) => method switch
    {
        PaymentMethod.Card => createdAtUtc.AddMinutes(options.CardTimeoutMinutes),
        PaymentMethod.BankTransfer => createdAtUtc.AddDays(options.BankTransferTimeoutDays),
        _ => null
    };

    public static bool IsExpired(PaymentMethod method, DateTime createdAtUtc, DateTime nowUtc, PaymentOptions options)
    {
        var deadline = GetDeadline(method, createdAtUtc, options);
        return deadline is not null && nowUtc > deadline;
    }
}

// What we send to the provider to start a payment.
public record ProviderPaymentRequest(
    string Reference,
    decimal Amount,
    int OrderNumber,
    string Culture,
    string ReturnUrl,
    string CallbackUrl);

// Where the customer is sent to pay.
public record ProviderSession(string RedirectUrl);

// The provider's answer, after its signature was checked.
public record ProviderCallback(string Reference, PaymentAttemptStatus Result, decimal Amount);

// A card payment provider (a bank). The fake bank and, later, a real bank implement this.
public interface IPaymentProvider
{
    string Name { get; }

    Task<ProviderSession> CreateSessionAsync(ProviderPaymentRequest request);

    // Reads the fields of a callback. Returns null when the signature is wrong or the fields are incomplete.
    ProviderCallback? ParseCallback(IReadOnlyDictionary<string, string> fields);
}

public enum StartPaymentStatus
{
    Started,

    OrderNotFound,

    // Not a card order, not awaiting payment any more, or the time to pay ran out.
    NotPayable,

    // No card provider is configured.
    ProviderUnavailable
}

public record StartPaymentResult(StartPaymentStatus Status, string? RedirectUrl = null);

public enum CallbackStatus
{
    // First "paid" answer: the order is now Ordered (Order has the summary, so the caller can send the emails).
    Paid,

    // Paid, but the order was cancelled meanwhile or already paid by another attempt. The money must be refunded by hand.
    PaidNeedsRefund,

    Failed,

    Cancelled,

    // The same answer again, or an answer that changes nothing.
    AlreadyProcessed,

    InvalidSignature,

    UnknownReference,

    // The amount in the answer is not the amount of the payment.
    AmountMismatch
}

public record CallbackResult(CallbackStatus Status, OrderSummary? Order = null);

public enum MarkPaidResult
{
    Marked,

    NotFound,

    NotAllowed
}

public interface IPaymentService
{
    // True when a card provider is configured.
    bool CardPaymentAvailable { get; }

    // Starts a new card payment attempt for an order that is waiting for payment.
    Task<StartPaymentResult> StartCardPaymentAsync(int orderNumber, string culture, string returnUrl, string callbackUrl);

    // Handles the answer of the provider. Safe to call twice with the same answer.
    Task<CallbackResult> HandleCallbackAsync(IReadOnlyDictionary<string, string> fields);

    // The state of the newest payment attempt of an order, or null when there is none.
    Task<PaymentAttemptStatus?> GetLatestAttemptAsync(int orderNumber);

    // The amount and order number of an attempt (for the fake bank's page), or null.
    Task<PaymentAttemptInfo?> GetAttemptAsync(string reference);

    // Cancels unpaid orders whose time ran out and puts their stock back. Returns how many were cancelled.
    Task<int> ExpireUnpaidOrdersAsync();

    // The shop received a bank transfer: the order becomes Ordered and paid.
    Task<MarkPaidResult> MarkBankTransferPaidAsync(int orderNumber, string? byUserId);
}

public record PaymentAttemptInfo(string Reference, int OrderNumber, decimal Amount, PaymentAttemptStatus Status);

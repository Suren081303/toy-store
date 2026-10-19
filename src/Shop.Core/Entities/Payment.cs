using Shop.Core.Payments;

namespace Shop.Core.Entities;

// One card payment attempt of an order. An order can have several (the customer may try again after a failure).
public class Payment
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    // The provider that handled it, for example "Fake".
    public string Provider { get; set; } = string.Empty;

    // Random, unguessable id that the provider sends back in its answer.
    public string Reference { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public PaymentAttemptStatus Status { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }

    // The money arrived but the order cannot use it (cancelled meanwhile, or already paid): refund by hand.
    public bool NeedsRefund { get; set; }

    public Order Order { get; set; } = null!;
}

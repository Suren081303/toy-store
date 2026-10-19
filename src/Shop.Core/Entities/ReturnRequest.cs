using Shop.Core.Orders;

namespace Shop.Core.Entities;

public class ReturnRequest
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public ReturnStatus Status { get; set; } = ReturnStatus.Requested;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? DecidedAt { get; set; }

    public string? AdminNote { get; set; }

    // When the money was sent back (set with the "Refunded" status). The analytics count refunds by this time.
    public DateTime? RefundedAt { get; set; }

    public Order Order { get; set; } = null!;
}

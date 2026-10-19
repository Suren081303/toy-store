namespace Shop.Core.Entities;

// An internal note on an order (for example "customer asked to call before delivery"). Only admins see it: never the customer,
// never an email, never an export. Notes are only added, never changed or removed.
public class OrderNote
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public string Text { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? ByUserId { get; set; }

    // The admin's email at that moment (kept even if the account is later deleted).
    public string ByEmail { get; set; } = string.Empty;

    public Order Order { get; set; } = null!;
}

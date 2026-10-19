namespace Shop.Core.Entities;

// One line of the admin audit log: who changed what, and when. Written in the same save as the change itself.
public class AuditLog
{
    public int Id { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;

    public string? UserId { get; set; }

    // The admin's email at that moment (kept even if the account is later removed).
    public string UserEmail { get; set; } = string.Empty;

    // For example "Settings.Saved", "Return.Returned", "Payment.RefundDone".
    public string Action { get; set; } = string.Empty;

    // For example "Order", "Product", "Category".
    public string EntityType { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public string? Details { get; set; }
}

using Microsoft.AspNetCore.Identity;

namespace Shop.Core.Entities;

public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Set when an admin blocks the customer: they cannot sign in, and a session they have open is ended. Null = active.
    public DateTime? BlockedAt { get; set; }

    // A discount an admin gave this customer: a whole percent (1 to 50) on the regular price, replacing the standard
    // "10% on the first 5 orders" while it is active. PersonalDiscountUntil is the last Armenian day it counts (null = no end).
    public int? PersonalDiscountPercent { get; set; }

    public DateOnly? PersonalDiscountUntil { get; set; }
}

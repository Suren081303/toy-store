using Microsoft.EntityFrameworkCore;
using Shop.Core.Entities;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Admin;

internal static class AuditWriter
{
    // Adds an audit line to the context. It is saved together with the change that is being recorded (one SaveChanges),
    // so there is never a change without its record, or a record without its change.
    public static async Task AddAsync(
        ShopDbContext db, string? userId, DateTime at, string action, string entityType, string entityId, string? details = null)
    {
        var email = userId is null
            ? string.Empty
            : await db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync() ?? string.Empty;

        Add(db, userId, email, at, action, entityType, entityId, details);
    }

    // The same, when the admin's email is already known (many lines in one change: one lookup, not one per line).
    public static void Add(
        ShopDbContext db, string? userId, string userEmail, DateTime at, string action, string entityType, string entityId, string? details = null)
    {
        var email = userEmail;

        db.AuditLogs.Add(new AuditLog
        {
            At = at,
            UserId = userId,
            UserEmail = email,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details is { Length: > 1000 } ? details[..1000] : details
        });
    }
}

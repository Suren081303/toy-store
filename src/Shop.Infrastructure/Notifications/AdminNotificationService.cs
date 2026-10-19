using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Notifications;
using Shop.Infrastructure.Admin;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Notifications;

public class AdminNotificationService : IAdminNotificationService
{
    private const int PageSize = 30;

    private readonly ShopDbContext _db;
    private readonly TimeProvider _time;

    public AdminNotificationService(ShopDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public Task<int> UnreadCountAsync() => _db.AdminNotifications.CountAsync(n => !n.IsRead);

    public async Task<NotificationSummary> SummaryAsync(int latest)
    {
        var unread = await UnreadCountAsync();

        var rows = await _db.AdminNotifications.AsNoTracking()
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Take(Math.Clamp(latest, 1, 20))
            .Select(n => ToRow(n))
            .ToListAsync();

        return new NotificationSummary(unread, rows);
    }

    public async Task<PagedResult<NotificationRow>> ListAsync(bool unreadOnly, int page)
    {
        var query = _db.AdminNotifications.AsNoTracking().AsQueryable();
        if (unreadOnly)
        {
            query = query.Where(n => !n.IsRead);
        }

        var total = await query.CountAsync();
        page = Math.Max(1, page);

        var rows = await query
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(n => ToRow(n))
            .ToListAsync();

        return new PagedResult<NotificationRow>(rows, page, PageSize, total);
    }

    public Task<NotificationRow?> GetAsync(int id)
    {
        return _db.AdminNotifications.AsNoTracking().Where(n => n.Id == id).Select(n => ToRow(n)).Cast<NotificationRow?>().FirstOrDefaultAsync();
    }

    public async Task MarkReadAsync(int id, string adminUserId)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        // Only an unread item changes, so reading twice (or two admins at once) keeps the first reader.
        await _db.AdminNotifications
            .Where(n => n.Id == id && !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, now)
                .SetProperty(n => n.ReadByUserId, adminUserId));
    }

    public async Task<int> MarkAllReadAsync(string adminUserId)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        return await _db.AdminNotifications
            .Where(n => !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, now)
                .SetProperty(n => n.ReadByUserId, adminUserId));
    }

    private static NotificationRow ToRow(Shop.Core.Entities.AdminNotification n) =>
        new(n.Id, n.CreatedAt, n.Type, n.OrderNumber, n.ReturnId, n.ProductId, n.Amount, n.Name, n.Stock, n.IsRead);
}

public class AdminEmailService : IAdminEmailService
{
    private const int PageSize = 30;

    private readonly ShopDbContext _db;
    private readonly TimeProvider _time;

    public AdminEmailService(ShopDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<PagedResult<EmailRow>> ListAsync(OutboxStatus? status, int page)
    {
        var query = _db.OutboxEmails.AsNoTracking().AsQueryable();
        if (status is not null)
        {
            query = query.Where(e => e.Status == status);
        }

        var total = await query.CountAsync();
        page = Math.Max(1, page);

        var rows = await query
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(e => new EmailRow(e.Id, e.CreatedAt, e.Kind, e.OrderNumber, e.Status, e.Attempts, e.NextAttemptAt, e.SentAt, e.ToAddress, e.LastError))
            .ToListAsync();

        return new PagedResult<EmailRow>(rows, page, PageSize, total);
    }

    public async Task<bool> RetryAsync(int id, string adminUserId)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var changed = await _db.OutboxEmails
            .Where(e => e.Id == id && e.Status == OutboxStatus.Failed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, OutboxStatus.Pending)
                .SetProperty(e => e.Attempts, 0)
                .SetProperty(e => e.NextAttemptAt, now));

        if (changed == 0)
        {
            return false;
        }

        await AuditWriter.AddAsync(_db, adminUserId, now, "Email.Retry", "Email", id.ToString());
        await _db.SaveChangesAsync();
        return true;
    }

    public Task<int> FailedCountAsync() => _db.OutboxEmails.CountAsync(e => e.Status == OutboxStatus.Failed);
}

// Deletes old notifications and emails so the tables do not grow forever.
public class NotificationCleanup : INotificationCleanup
{
    private readonly ShopDbContext _db;
    private readonly TimeProvider _time;

    public NotificationCleanup(ShopDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<(int Notifications, int Emails)> CleanAsync()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var notificationLimit = now.AddDays(-OutboxRules.NotificationDays);
        var sentLimit = now.AddDays(-OutboxRules.SentEmailDays);

        var notifications = await _db.AdminNotifications.Where(n => n.CreatedAt < notificationLimit).ExecuteDeleteAsync();

        // Sent emails go after 30 days; failed ones stay as long as the bell items (an admin may still want to retry them).
        var emails = await _db.OutboxEmails
            .Where(e => (e.Status == OutboxStatus.Sent && e.SentAt < sentLimit) || (e.Status == OutboxStatus.Failed && e.CreatedAt < notificationLimit))
            .ExecuteDeleteAsync();

        return (notifications, emails);
    }
}

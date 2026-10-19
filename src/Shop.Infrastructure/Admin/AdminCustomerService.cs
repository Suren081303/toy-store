using Microsoft.EntityFrameworkCore;
using Shop.Core;
using Shop.Core.Admin;
using Shop.Core.Entities;
using Shop.Core.Identity;
using Shop.Core.Notifications;
using Shop.Core.Orders;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Admin;

// The customer list and what an admin can do with one customer: block, give a personal discount, send a message.
// "Customers" are the accounts that are not admins. Everything an admin changes is written to the audit log in the same save.
public class AdminCustomerService : IAdminCustomerService
{
    private const int PageSize = 25;

    // "Confirmed order" below (Ordered, Packed, Delivered) is the same as in the analytics.

    // How many of the latest messages the list shows under a customer (only those of the last 30 days are loaded).
    private const int MessagesShown = 3;

    private static readonly string[] SupportedCultures = ["hy", "ru", "en"];

    private readonly ShopDbContext _db;
    private readonly TimeProvider _time;

    public AdminCustomerService(ShopDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<PagedResult<AdminCustomerRow>> ListAsync(AdminCustomerFilter filter)
    {
        var today = ShopClock.Today(_time);

        var users = _db.Users.AsNoTracking()
            .Where(u => !_db.UserRoles.Any(ur => ur.UserId == u.Id && _db.Roles.Any(r => r.Id == ur.RoleId && r.Name == Roles.Admin)));

        var text = filter.Query?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            users = users.Where(u => u.FullName.Contains(text) || u.Email!.Contains(text) || (u.PhoneNumber != null && u.PhoneNumber.Contains(text)));
        }

        users = filter.Status switch
        {
            CustomerStatusFilter.Blocked => users.Where(u => u.BlockedAt != null),
            CustomerStatusFilter.Active => users.Where(u => u.BlockedAt == null),
            _ => users
        };

        var total = await users.CountAsync();
        var page = Math.Max(1, filter.Page);

        var rows = users.Select(u => new
        {
            u.Id,
            u.FullName,
            u.Email,
            u.PhoneNumber,
            u.EmailConfirmed,
            u.CreatedAt,
            u.BlockedAt,
            u.PersonalDiscountPercent,
            u.PersonalDiscountUntil,
            Orders = _db.Orders.Count(o => o.UserId == u.Id && (o.Status == OrderStatus.Ordered || o.Status == OrderStatus.Packed || o.Status == OrderStatus.Delivered)),
            Paid = _db.Orders
                .Where(o => o.UserId == u.Id && (o.Status == OrderStatus.Ordered || o.Status == OrderStatus.Packed || o.Status == OrderStatus.Delivered))
                .Sum(o => (decimal?)o.Total) ?? 0m,
            Refunded = _db.ReturnRequests
                .Where(r => r.Order.UserId == u.Id && r.RefundedAt != null)
                .Sum(r => (decimal?)r.Order.Total) ?? 0m,
            LastOrderAt = _db.Orders
                .Where(o => o.UserId == u.Id && (o.Status == OrderStatus.Ordered || o.Status == OrderStatus.Packed || o.Status == OrderStatus.Delivered))
                .Max(o => (DateTime?)o.CreatedAt)
        });

        rows = filter.Sort switch
        {
            CustomerSort.Name => rows.OrderBy(r => r.FullName).ThenBy(r => r.Email),
            CustomerSort.Orders => rows.OrderByDescending(r => r.Orders).ThenBy(r => r.FullName),
            CustomerSort.Spent => rows.OrderByDescending(r => r.Paid - r.Refunded).ThenBy(r => r.FullName),
            CustomerSort.LastOrder => rows.OrderByDescending(r => r.LastOrderAt).ThenBy(r => r.FullName),
            _ => rows.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
        };

        var pageRows = await rows.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync();
        var messages = await LoadMessagesAsync(pageRows.Select(r => r.Id).ToList());

        var items = pageRows.Select(r => new AdminCustomerRow(
            r.Id,
            r.FullName,
            r.Email ?? string.Empty,
            r.PhoneNumber ?? string.Empty,
            r.EmailConfirmed,
            r.CreatedAt,
            r.Orders,
            r.Paid - r.Refunded,
            r.LastOrderAt,
            r.BlockedAt is not null,
            r.PersonalDiscountPercent,
            r.PersonalDiscountUntil,
            CustomerDiscountRules.IsPersonalActive(r.PersonalDiscountPercent, r.PersonalDiscountUntil, today),
            messages.TryGetValue(r.Id, out var list) ? list : [])).ToList();

        return new PagedResult<AdminCustomerRow>(items, page, PageSize, total);
    }

    public async Task<CustomerActionStatus> BlockAsync(string userId, string byAdminId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();

        var (user, refusal) = await FindCustomerAsync(userId);
        if (user is null)
        {
            return refusal;
        }

        if (user.BlockedAt is not null)
        {
            return CustomerActionStatus.NothingToChange;
        }

        user.BlockedAt = Now();
        // The sign-in cookie is also checked against BlockedAt on every request; the new stamp ends sessions that
        // the cookie check would not reach (for example a "remember me" cookie from another device).
        user.SecurityStamp = Guid.NewGuid().ToString();

        await AuditWriter.AddAsync(_db, byAdminId, Now(), "Customer.Blocked", "Customer", user.Id, user.Email);
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return CustomerActionStatus.Done;
    }

    public async Task<CustomerActionStatus> UnblockAsync(string userId, string byAdminId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();

        var (user, refusal) = await FindCustomerAsync(userId);
        if (user is null)
        {
            return refusal;
        }

        if (user.BlockedAt is null)
        {
            return CustomerActionStatus.NothingToChange;
        }

        user.BlockedAt = null;
        // Wrong passwords entered while blocked must not keep the customer out.
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;

        await AuditWriter.AddAsync(_db, byAdminId, Now(), "Customer.Unblocked", "Customer", user.Id, user.Email);
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return CustomerActionStatus.Done;
    }

    public async Task<CustomerActionStatus> SetDiscountAsync(string userId, int? percent, DateOnly? until, string byAdminId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();

        var (user, refusal) = await FindCustomerAsync(userId);
        if (user is null)
        {
            return refusal;
        }

        if (percent is null or 0)
        {
            // Remove.
            if (user.PersonalDiscountPercent is null)
            {
                return CustomerActionStatus.NothingToChange;
            }

            user.PersonalDiscountPercent = null;
            user.PersonalDiscountUntil = null;

            await AuditWriter.AddAsync(_db, byAdminId, Now(), "Customer.DiscountRemoved", "Customer", user.Id, user.Email);
        }
        else
        {
            if (!CustomerDiscountRules.IsValidPersonalPercent(percent.Value) || (until is { } day && day < ShopClock.Today(_time)))
            {
                return CustomerActionStatus.InvalidDiscount;
            }

            user.PersonalDiscountPercent = percent;
            user.PersonalDiscountUntil = until;

            var details = $"{user.Email}: {percent}%" + (until is null ? string.Empty : $" until {until:yyyy-MM-dd}");
            await AuditWriter.AddAsync(_db, byAdminId, Now(), "Customer.DiscountSet", "Customer", user.Id, details);
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return CustomerActionStatus.Done;
    }

    public async Task<CustomerActionStatus> SendMessageAsync(string userId, string subject, string body, string culture, string byAdminId)
    {
        subject = (subject ?? string.Empty).Trim();
        body = (body ?? string.Empty).Replace("\r\n", "\n").Trim();

        if (subject.Length == 0 || subject.Length > CustomerMessageRules.MaxSubjectLength
            || body.Length == 0 || body.Length > CustomerMessageRules.MaxBodyLength)
        {
            return CustomerActionStatus.InvalidMessage;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();

        // One send at a time per customer, so the daily limit cannot be passed by two admins pressing at once.
        await _db.AcquireAsync("customer-message:" + userId);

        var (user, refusal) = await FindCustomerAsync(userId);
        if (user is null)
        {
            return refusal;
        }

        // The address must be proven to be the customer's own, or a message could go to a stranger.
        if (!user.EmailConfirmed || string.IsNullOrWhiteSpace(user.Email))
        {
            return CustomerActionStatus.EmailNotConfirmed;
        }

        var now = Now();
        var since = now.AddHours(-24);
        if (await _db.CustomerMessages.CountAsync(m => m.UserId == userId && m.CreatedAt >= since) >= CustomerMessageRules.MaxPerCustomerPerDay)
        {
            return CustomerActionStatus.TooManyMessages;
        }

        var adminEmail = await _db.Users.Where(u => u.Id == byAdminId).Select(u => u.Email).FirstOrDefaultAsync() ?? string.Empty;

        var message = new CustomerMessage
        {
            UserId = userId,
            Subject = subject,
            Body = body,
            Culture = SupportedCultures.Contains(culture) ? culture : "hy",
            SentByUserId = byAdminId,
            SentByEmail = adminEmail,
            CreatedAt = now
        };

        _db.CustomerMessages.Add(message);
        await _db.SaveChangesAsync(); // the email row needs the message id

        // Written in the same transaction as the message: it cannot be lost between "saved" and "sent".
        _db.OutboxEmails.Add(new OutboxEmail
        {
            CreatedAt = now,
            NextAttemptAt = now,
            Kind = EmailKind.CustomerMessage,
            OrderNumber = 0,
            CustomerMessageId = message.Id
        });

        await AuditWriter.AddAsync(_db, byAdminId, now, "Customer.MessageSent", "Customer", user.Id, $"{user.Email}: {subject}");
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return CustomerActionStatus.Done;
    }

    // The account, if it is a customer. Otherwise null with the reason.
    private async Task<(ApplicationUser? User, CustomerActionStatus Refusal)> FindCustomerAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
        {
            return (null, CustomerActionStatus.NotFound);
        }

        var isAdmin = await _db.UserRoles.AnyAsync(ur => ur.UserId == userId && _db.Roles.Any(r => r.Id == ur.RoleId && r.Name == Roles.Admin));

        return isAdmin ? (null, CustomerActionStatus.NotACustomer) : (user, CustomerActionStatus.Done);
    }

    private async Task<Dictionary<string, IReadOnlyList<AdminMessageRow>>> LoadMessagesAsync(IReadOnlyCollection<string> userIds)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        var since = Now().AddDays(-30);
        var messages = await _db.CustomerMessages.AsNoTracking()
            .Where(m => userIds.Contains(m.UserId) && m.CreatedAt >= since)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .ToListAsync();

        var shown = messages.GroupBy(m => m.UserId).SelectMany(g => g.Take(MessagesShown)).ToList();
        var ids = shown.Select(m => (int?)m.Id).ToList();
        var statuses = await _db.OutboxEmails.AsNoTracking()
            .Where(e => ids.Contains(e.CustomerMessageId))
            .ToDictionaryAsync(e => e.CustomerMessageId!.Value, e => e.Status);

        return shown
            .GroupBy(m => m.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<AdminMessageRow>)g
                    .Select(m => new AdminMessageRow(m.Id, m.CreatedAt, m.Subject, m.Body, m.SentByEmail, statuses.TryGetValue(m.Id, out var s) ? s : null))
                    .ToList());
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;
}

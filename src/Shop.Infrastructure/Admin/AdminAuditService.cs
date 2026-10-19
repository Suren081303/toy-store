using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Orders;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Admin;

public class AdminAuditService : IAdminAuditService
{
    private const int PageSize = 50;

    private readonly ShopDbContext _db;

    public AdminAuditService(ShopDbContext db)
    {
        _db = db;
    }

    public async Task<PagedResult<AuditRow>> ListAsync(AuditFilter filter)
    {
        var query = _db.AuditLogs.AsNoTracking().AsQueryable();

        var text = filter.Query?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            query = query.Where(a => a.UserEmail.Contains(text) || a.Action.Contains(text) || a.EntityId == text
                || (a.Details != null && a.Details.Contains(text)));
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityType))
        {
            var type = filter.EntityType;
            query = query.Where(a => a.EntityType == type);
        }

        if (filter.From is not null)
        {
            var from = ShopClock.StartOfDayUtc(filter.From.Value);
            query = query.Where(a => a.At >= from);
        }

        if (filter.To is not null)
        {
            var before = ShopClock.StartOfDayUtc(filter.To.Value.AddDays(1));
            query = query.Where(a => a.At < before);
        }

        var total = await query.CountAsync();
        var page = Math.Max(1, filter.Page);

        var rows = await query
            .OrderByDescending(a => a.At).ThenByDescending(a => a.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(a => new AuditRow(a.Id, a.At, a.UserEmail, a.Action, a.EntityType, a.EntityId, a.Details))
            .ToListAsync();

        return new PagedResult<AuditRow>(rows, page, PageSize, total);
    }

    public async Task<IReadOnlyList<string>> EntityTypesAsync()
    {
        return await _db.AuditLogs.AsNoTracking().Select(a => a.EntityType).Distinct().OrderBy(t => t).ToListAsync();
    }
}

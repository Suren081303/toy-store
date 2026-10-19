using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Entities;
using Shop.Core.Notifications;
using Shop.Core.Orders;
using Shop.Infrastructure.Data;
using Shop.Infrastructure.Notifications;

namespace Shop.Infrastructure.Admin;

public class AdminReturnService : IAdminReturnService
{
    private const int PageSize = 20;
    private const int MaxNoteLength = 1000;

    private readonly ShopDbContext _db;
    private readonly IOrderService _orders;
    private readonly TimeProvider _time;

    public AdminReturnService(ShopDbContext db, IOrderService orders, TimeProvider time)
    {
        _db = db;
        _orders = orders;
        _time = time;
    }

    public async Task<PagedResult<AdminReturnRow>> ListAsync(ReturnStatus? status, int page)
    {
        var query = _db.ReturnRequests.AsNoTracking().AsQueryable();
        if (status is not null)
        {
            query = query.Where(r => r.Status == status);
        }

        var total = await query.CountAsync();
        page = Math.Max(1, page);

        var rows = await query
            .OrderBy(r => r.Status == ReturnStatus.Requested ? 0 : r.Status == ReturnStatus.Returned ? 1 : 2) // open ones first
            .ThenByDescending(r => r.CreatedAt)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(r => new AdminReturnRow(r.Id, r.Order.Number, r.Order.CustomerName, r.CreatedAt, r.Status, r.Order.Total, r.Reason))
            .ToListAsync();

        return new PagedResult<AdminReturnRow>(rows, page, PageSize, total);
    }

    public async Task<AdminReturnDetail?> GetAsync(int id)
    {
        var request = await _db.ReturnRequests.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new { r.Id, r.Status, r.Reason, r.AdminNote, r.CreatedAt, r.DecidedAt, r.Order.Number })
            .FirstOrDefaultAsync();
        if (request is null)
        {
            return null;
        }

        var order = await _orders.FindForSystemAsync(request.Number);
        return new AdminReturnDetail(
            request.Id, request.Status, request.Reason, request.AdminNote, request.CreatedAt, request.DecidedAt,
            order!, ReturnStatusRules.AllowedNext(request.Status));
    }

    public async Task<ChangeReturnResult> ChangeStatusAsync(int id, ReturnStatus newStatus, string? note, bool restock, string adminUserId)
    {
        var info = await _db.ReturnRequests.Where(r => r.Id == id).Select(r => new { r.Order.Number }).FirstOrDefaultAsync();
        if (info is null)
        {
            return ChangeReturnResult.NotFound;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _db.AcquireAsync("order:" + info.Number);

        var request = await _db.ReturnRequests
            .Include(r => r.Order).ThenInclude(o => o.Items)
            .FirstAsync(r => r.Id == id);

        if (!ReturnStatusRules.CanChange(request.Status, newStatus))
        {
            return ChangeReturnResult.NotAllowed;
        }

        var order = request.Order;
        var now = _time.GetUtcNow().UtcDateTime;

        request.Status = newStatus;
        request.DecidedAt ??= now;

        var text = note?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            request.AdminNote = text.Length > MaxNoteLength ? text[..MaxNoteLength] : text;
        }

        var eventNote = $"Return {newStatus}";

        // The goods are back: they can be sold again. This happens at most once, because "Returned" can be set only once.
        if (newStatus == ReturnStatus.Returned && restock)
        {
            foreach (var item in order.Items)
            {
                var productId = item.ProductId;
                var quantity = item.Quantity;

                await _db.Products
                    .Where(p => p.Id == productId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, p => p.StockQuantity + quantity));
            }

            eventNote += ", items back in stock";
        }

        // The money went back (sent by hand, outside the shop).
        if (newStatus == ReturnStatus.Refunded)
        {
            request.RefundedAt = now;

            if (order.PaymentStatus == PaymentStatus.Paid)
            {
                order.PaymentStatus = PaymentStatus.Refunded;
            }
        }

        order.UpdatedAt = now;
        order.Events.Add(new OrderEvent { At = now, Status = order.Status, Note = eventNote, ByUserId = adminUserId });

        // The customer gets the answer by email, with the admin's note.
        var emailKind = newStatus switch
        {
            ReturnStatus.Rejected => EmailKind.ReturnRejected,
            ReturnStatus.Returned => EmailKind.ReturnReceived,
            _ => EmailKind.ReturnRefunded
        };
        NotificationWriter.Email(_db, emailKind, order.Number, now, request.Id, text);

        await AuditWriter.AddAsync(_db, adminUserId, now, "Return." + newStatus, "Order", order.Number.ToString(), eventNote);

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return ChangeReturnResult.Changed;
    }
}

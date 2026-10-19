using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Orders;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Admin;

public class AdminOrderService : IAdminOrderService
{
    private const int PageSize = 20;

    private readonly ShopDbContext _db;
    private readonly IOrderService _orders;
    private readonly TimeProvider _time;

    public AdminOrderService(ShopDbContext db, IOrderService orders, TimeProvider time)
    {
        _db = db;
        _orders = orders;
        _time = time;
    }

    public async Task<DashboardCounts> GetDashboardAsync()
    {
        var todayStart = ShopClock.StartOfDayUtc(ShopClock.Today(_time));

        // One query after another (a DbContext runs one query at a time).
        return new DashboardCounts(
            OrdersToday: await _db.Orders.CountAsync(o => o.CreatedAt >= todayStart),
            ToPack: await _db.Orders.CountAsync(o => o.Status == OrderStatus.Ordered),
            AwaitingPayment: await _db.Orders.CountAsync(o => o.Status == OrderStatus.AwaitingPayment),
            OpenReturns: await _db.ReturnRequests.CountAsync(r => r.Status == ReturnStatus.Requested || r.Status == ReturnStatus.Returned),
            RefundsNeeded: await _db.Payments.CountAsync(p => p.NeedsRefund),
            LowStock: await _db.Products.CountAsync(p => !p.IsArchived && p.StockQuantity <= StockLevels.Low),
            FailedEmails: await _db.OutboxEmails.CountAsync(e => e.Status == Shop.Core.Notifications.OutboxStatus.Failed));
    }

    public async Task<PagedResult<AdminOrderRow>> ListAsync(AdminOrderFilter filter)
    {
        var query = _db.Orders.AsNoTracking().AsQueryable();

        var text = filter.Query?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            query = int.TryParse(text, out var number)
                ? query.Where(o => o.Number == number || o.Phone.Contains(text))
                : query.Where(o => o.Email.Contains(text) || o.CustomerName.Contains(text) || o.Phone.Contains(text));
        }

        if (filter.Status is not null)
        {
            query = query.Where(o => o.Status == filter.Status);
        }

        if (filter.Method is not null)
        {
            query = query.Where(o => o.PaymentMethod == filter.Method);
        }

        if (filter.From is not null)
        {
            var from = ShopClock.StartOfDayUtc(filter.From.Value);
            query = query.Where(o => o.CreatedAt >= from);
        }

        if (filter.To is not null)
        {
            var before = ShopClock.StartOfDayUtc(filter.To.Value.AddDays(1));
            query = query.Where(o => o.CreatedAt < before);
        }

        if (filter.RefundNeeded)
        {
            query = query.Where(o => _db.Payments.Any(p => p.OrderId == o.Id && p.NeedsRefund));
        }

        var total = await query.CountAsync();
        var page = Math.Max(1, filter.Page);

        var rows = await query
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(o => new AdminOrderRow(
                o.Number,
                o.CreatedAt,
                o.Status,
                o.PaymentMethod,
                o.PaymentStatus,
                o.CustomerName,
                o.Phone,
                o.Total,
                _db.Payments.Any(p => p.OrderId == o.Id && p.NeedsRefund),
                o.ReturnRequests.Any(r => r.Status == ReturnStatus.Requested || r.Status == ReturnStatus.Returned)))
            .ToListAsync();

        return new PagedResult<AdminOrderRow>(rows, page, PageSize, total);
    }

    public async Task<AdminOrderDetail?> GetAsync(int number)
    {
        var order = await _orders.FindForSystemAsync(number);
        if (order is null)
        {
            return null;
        }

        var events = await _db.OrderEvents.AsNoTracking()
            .Where(e => e.Order.Number == number)
            .OrderBy(e => e.At).ThenBy(e => e.Id)
            .Select(e => new AdminEventRow(
                e.At,
                e.Status,
                e.Note,
                e.ByUserId == null ? null : _db.Users.Where(u => u.Id == e.ByUserId).Select(u => u.Email).FirstOrDefault()))
            .ToListAsync();

        var payments = await _db.Payments.AsNoTracking()
            .Where(p => p.Order.Number == number)
            .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
            .Select(p => new AdminPaymentRow(p.Id, p.Provider, p.CreatedAt, p.Amount, p.Status, p.NeedsRefund))
            .ToListAsync();

        // A card order becomes Ordered only when the bank says it was paid, and a bank transfer by the "received" button,
        // so "Ordered" is not offered by hand while the order is waiting for payment.
        var next = OrderStatusRules.AllowedNext(order.Status)
            .Where(s => !(order.Status == OrderStatus.AwaitingPayment && s == OrderStatus.Ordered))
            .ToList();

        var canMarkReceived = order.PaymentMethod == PaymentMethod.BankTransfer && order.Status == OrderStatus.AwaitingPayment;

        var returnId = await _db.ReturnRequests.AsNoTracking()
            .Where(r => r.Order.Number == number)
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync();

        return new AdminOrderDetail(order, events, payments, next, canMarkReceived, returnId);
    }

    public async Task<RefundDoneResult> MarkRefundDoneAsync(int paymentId, string adminUserId)
    {
        var info = await _db.Payments.Where(p => p.Id == paymentId).Select(p => new { p.Order.Number }).FirstOrDefaultAsync();
        if (info is null)
        {
            return RefundDoneResult.NotFound;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _db.AcquireAsync("order:" + info.Number);

        var payment = await _db.Payments.Include(p => p.Order).FirstAsync(p => p.Id == paymentId);
        if (!payment.NeedsRefund)
        {
            return RefundDoneResult.NotNeeded;
        }

        var order = payment.Order;
        var now = _time.GetUtcNow().UtcDateTime;

        payment.NeedsRefund = false;

        // A cancelled order whose money was kept is now fully refunded. (For an order that is still valid, the first
        // payment stands and only the extra payment was sent back.)
        if (order.Status == OrderStatus.Cancelled && order.PaymentStatus == PaymentStatus.Paid)
        {
            order.PaymentStatus = PaymentStatus.Refunded;
        }

        order.UpdatedAt = now;
        order.Events.Add(new Shop.Core.Entities.OrderEvent
        {
            At = now,
            Status = order.Status,
            Note = $"Refund done for payment {payment.Reference[..8]}",
            ByUserId = adminUserId
        });

        await AuditWriter.AddAsync(_db, adminUserId, now, "Payment.RefundDone", "Order", order.Number.ToString(), $"Amount {payment.Amount:0.##}");

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return RefundDoneResult.Done;
    }
}

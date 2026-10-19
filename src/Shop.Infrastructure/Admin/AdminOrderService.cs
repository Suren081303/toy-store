using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Entities;
using Shop.Core.Notifications;
using Shop.Core.Orders;
using Shop.Core.Payments;
using Shop.Core.Phones;
using Shop.Infrastructure.Data;
using Shop.Infrastructure.Notifications;

namespace Shop.Infrastructure.Admin;

public class AdminOrderService : IAdminOrderService
{
    private const int PageSize = 20;

    private readonly ShopDbContext _db;
    private readonly IOrderService _orders;
    private readonly IStoreSettingsService _settings;
    private readonly TimeProvider _time;

    public AdminOrderService(ShopDbContext db, IOrderService orders, IStoreSettingsService settings, TimeProvider time)
    {
        _db = db;
        _orders = orders;
        _settings = settings;
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

    // The orders that match the filter (without paging).
    private IQueryable<Order> Filtered(AdminOrderFilter filter)
    {
        var query = _db.Orders.AsNoTracking().AsQueryable();

        var text = filter.Query?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            // A number is looked up as an order number and as part of a phone; a phone is found however it is typed
            // (091 123456, +374 91 123456, 91123456), because orders store "+37491123456".
            int? number = int.TryParse(text, out var parsed) ? parsed : null;
            var needle = PhoneNumbers.SearchNeedle(text);

            query = query.Where(o =>
                (number != null && o.Number == number)
                || o.Phone.Contains(text)
                || (needle != null && o.Phone.Contains(needle))
                || (number == null && (o.Email.Contains(text) || o.CustomerName.Contains(text))));
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

        return query;
    }

    public async Task<PagedResult<AdminOrderRow>> ListAsync(AdminOrderFilter filter)
    {
        var query = Filtered(filter);

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
                o.ReturnRequests.Any(r => r.Status == ReturnStatus.Requested || r.Status == ReturnStatus.Returned),
                o.Notes.Count(),
                o.CreatedByUserId != null))
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

        var notes = await _db.OrderNotes.AsNoTracking()
            .Where(n => n.Order.Number == number)
            .OrderBy(n => n.CreatedAt).ThenBy(n => n.Id)
            .Select(n => new AdminNoteRow(n.CreatedAt, n.Text, n.ByEmail))
            .ToListAsync();

        var createdBy = await _db.Orders.AsNoTracking()
            .Where(o => o.Number == number)
            .Select(o => new { o.CreatedByUserId, Email = _db.Users.Where(u => u.Id == o.CreatedByUserId).Select(u => u.Email).FirstOrDefault() })
            .FirstAsync();

        return new AdminOrderDetail(
            order, events, payments, next, canMarkReceived, returnId,
            notes, createdBy.CreatedByUserId is not null, createdBy.Email, OrderEditRules.ScopeOf(order.Status));
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

    public async Task<EditOrderStatus> UpdateAsync(int number, AdminOrderEdit edit, string adminUserId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();

        // The same lock as a status change: an edit and a "packed" at the same moment are handled one after the other.
        await _db.AcquireAsync("order:" + number);

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Number == number);
        if (order is null)
        {
            return EditOrderStatus.NotFound;
        }

        var scope = OrderEditRules.ScopeOf(order.Status);
        if (scope == OrderEditScope.None)
        {
            return EditOrderStatus.NotEditable;
        }

        // ---- What the admin typed, cleaned up ----
        var name = (edit.CustomerName ?? string.Empty).Trim();
        var email = (edit.Email ?? string.Empty).Trim();
        var city = (edit.City ?? string.Empty).Trim();
        var street = (edit.Street ?? string.Empty).Trim();
        var building = (edit.Building ?? string.Empty).Trim();
        var apartment = string.IsNullOrWhiteSpace(edit.Apartment) ? null : edit.Apartment.Trim();
        var comment = string.IsNullOrWhiteSpace(edit.Comment) ? null : edit.Comment.Trim();

        if (name.Length is 0 or > 100
            || !PhoneNumbers.TryNormalize(edit.Phone, out var phone)
            || email.Length > 256
            || (email.Length > 0 && !new EmailAddressAttribute().IsValid(email)))
        {
            return EditOrderStatus.InvalidContact;
        }

        var settings = await _settings.GetAsync();
        var slot = settings.Slots.FirstOrDefault(x => x.Id == edit.DeliverySlotId);

        // A day or time that is already on the order stays valid even when it would not be offered any more (for example today).
        var dayChanged = edit.DeliveryDate != order.DeliveryDate;
        var slotChanged = slot is null || slot.Label != order.DeliverySlot;

        if (!Regions.IsValid(edit.Region)
            || city.Length is 0 or > 100 || street.Length is 0 or > 150 || building.Length is 0 or > 30
            || (apartment?.Length ?? 0) > 30 || (comment?.Length ?? 0) > 500
            || (slot is null && slotChanged)
            || (dayChanged && !DeliveryRules.IsAllowedDate(edit.DeliveryDate, ShopClock.Today(_time), settings.DeliveryFirstDayOffset, settings.DeliveryDaysAhead)))
        {
            return EditOrderStatus.InvalidDelivery;
        }

        // ---- What is different ----
        var contactChanges = new List<string>();
        var deliveryChanges = new List<string>();

        void Note(List<string> list, string field, string? from, string? to)
        {
            if ((from ?? string.Empty) != (to ?? string.Empty))
            {
                list.Add($"{field}: {(string.IsNullOrEmpty(from) ? "-" : from)} -> {(string.IsNullOrEmpty(to) ? "-" : to)}");
            }
        }

        Note(contactChanges, "name", order.CustomerName, name);
        Note(contactChanges, "phone", order.Phone, phone);
        Note(contactChanges, "email", order.Email, email);
        Note(deliveryChanges, "region", order.Region, edit.Region);
        Note(deliveryChanges, "city", order.City, city);
        Note(deliveryChanges, "street", order.Street, street);
        Note(deliveryChanges, "building", order.Building, building);
        Note(deliveryChanges, "apartment", order.Apartment, apartment);
        Note(deliveryChanges, "day", order.DeliveryDate.ToString("yyyy-MM-dd"), edit.DeliveryDate.ToString("yyyy-MM-dd"));
        Note(deliveryChanges, "slot", order.DeliverySlot, slot?.Label ?? order.DeliverySlot);

        var commentChanges = new List<string>();
        Note(commentChanges, "comment", order.Comment, comment);

        var all = contactChanges.Concat(deliveryChanges).Concat(commentChanges).ToList();
        if (all.Count == 0)
        {
            return EditOrderStatus.NoChange;
        }

        // A packed order is ready to go: only the comment may change.
        if (scope == OrderEditScope.CommentOnly && (contactChanges.Count > 0 || deliveryChanges.Count > 0))
        {
            return EditOrderStatus.NotEditable;
        }

        // ---- Apply ----
        var now = _time.GetUtcNow().UtcDateTime;
        order.CustomerName = name;
        order.Phone = phone;
        order.Email = email;
        order.Region = edit.Region;
        order.City = city;
        order.Street = street;
        order.Building = building;
        order.Apartment = apartment;
        order.Comment = comment;
        order.DeliveryDate = edit.DeliveryDate;
        order.DeliverySlot = slot?.Label ?? order.DeliverySlot;
        order.UpdatedAt = now;

        var summary = string.Join("; ", all);
        _db.OrderEvents.Add(new OrderEvent { OrderId = order.Id, At = now, Status = order.Status, Note = "Edited: " + summary, ByUserId = adminUserId });
        await AuditWriter.AddAsync(_db, adminUserId, now, "Order.Edited", "Order", order.Number.ToString(), summary);

        // The customer is told when the day, time or address changed (the email job skips an order without an email address).
        if (deliveryChanges.Count > 0)
        {
            NotificationWriter.Email(_db, EmailKind.OrderUpdated, order.Number, now);
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return EditOrderStatus.Saved;
    }

    public async Task<AddNoteStatus> AddNoteAsync(int number, string text, string adminUserId)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > OrderEditRules.MaxNoteLength)
        {
            return AddNoteStatus.Invalid;
        }

        var order = await _db.Orders.Where(o => o.Number == number).Select(o => new { o.Id }).FirstOrDefaultAsync();
        if (order is null)
        {
            return AddNoteStatus.NotFound;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var email = await _db.Users.Where(u => u.Id == adminUserId).Select(u => u.Email).FirstOrDefaultAsync() ?? string.Empty;

        _db.OrderNotes.Add(new OrderNote { OrderId = order.Id, Text = trimmed, CreatedAt = now, ByUserId = adminUserId, ByEmail = email });
        await AuditWriter.AddAsync(_db, adminUserId, now, "Order.NoteAdded", "Order", number.ToString(), trimmed);
        await _db.SaveChangesAsync();

        return AddNoteStatus.Added;
    }

    public async Task<PackingSlip?> GetSlipAsync(int number)
    {
        var order = await _db.Orders.AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Number == number);

        return order is null ? null : ToSlip(order);
    }

    public async Task<IReadOnlyList<PackingSlip>> GetSlipsAsync(AdminOrderFilter filter, int max)
    {
        var orders = await Filtered(filter)
            .Include(o => o.Items)
            .OrderBy(o => o.DeliveryDate).ThenBy(o => o.DeliverySlot).ThenBy(o => o.Number)
            .Take(max)
            .ToListAsync();

        return orders.Select(ToSlip).ToList();
    }

    private static PackingSlip ToSlip(Order o)
    {
        var lines = o.Items
            .OrderBy(i => i.Id)
            .Select(i => new PackingSlipLine(i.Sku, i.Brand, i.Name, i.Quantity, i.UnitPrice, i.UnitPrice * i.Quantity))
            .ToList();

        // The courier collects cash only for a cash order that is not delivered or cancelled yet.
        var collect = o.PaymentMethod == PaymentMethod.CashOnDelivery
            && o.PaymentStatus != PaymentStatus.Paid
            && o.Status is not (OrderStatus.Delivered or OrderStatus.Cancelled)
            ? o.Total
            : 0m;

        return new PackingSlip(
            o.Number, o.CreatedAt, o.CreatedByUserId is not null, o.Status, o.PaymentMethod, o.PaymentStatus,
            o.DeliveryDate, o.DeliverySlot, o.CustomerName, o.Phone, o.Email,
            o.Region, o.City, o.Street, o.Building, o.Apartment, o.Comment,
            lines, o.Subtotal, o.Discount, o.DeliveryFee, o.Total, collect);
    }
}

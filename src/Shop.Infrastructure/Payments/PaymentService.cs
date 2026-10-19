using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shop.Core.Entities;
using Shop.Core.Orders;
using Shop.Core.Payments;
using Shop.Infrastructure.Data;
using Shop.Infrastructure.Notifications;

namespace Shop.Infrastructure.Payments;

public class PaymentService : IPaymentService
{
    private readonly ShopDbContext _db;
    private readonly IOrderService _orders;
    private readonly PaymentOptions _options;
    private readonly TimeProvider _time;
    private readonly IPaymentProvider? _provider;

    // The provider is optional: without one, card payment is simply not offered.
    public PaymentService(
        ShopDbContext db,
        IOrderService orders,
        IOptions<PaymentOptions> options,
        TimeProvider time,
        IPaymentProvider? provider = null)
    {
        _db = db;
        _orders = orders;
        _options = options.Value;
        _time = time;
        _provider = provider;
    }

    public bool CardPaymentAvailable => _provider is not null;

    public async Task<StartPaymentResult> StartCardPaymentAsync(int orderNumber, string culture, string returnUrl, string callbackUrl)
    {
        if (_provider is null)
        {
            return new StartPaymentResult(StartPaymentStatus.ProviderUnavailable);
        }

        Payment payment;
        await using (var transaction = await _db.Database.BeginTransactionAsync())
        {
            await _db.AcquireAsync("order:" + orderNumber);

            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Number == orderNumber);
            if (order is null)
            {
                return new StartPaymentResult(StartPaymentStatus.OrderNotFound);
            }

            var now = Now();
            if (order.PaymentMethod != PaymentMethod.Card
                || order.Status != OrderStatus.AwaitingPayment
                || PaymentRules.IsExpired(order.PaymentMethod, order.CreatedAt, now, _options))
            {
                return new StartPaymentResult(StartPaymentStatus.NotPayable);
            }

            // A new attempt replaces the open ones (an answer to an old one is still accepted if it says "paid").
            var open = await _db.Payments.Where(p => p.OrderId == order.Id && p.Status == PaymentAttemptStatus.Created).ToListAsync();
            foreach (var old in open)
            {
                old.Status = PaymentAttemptStatus.Cancelled;
            }

            payment = new Payment
            {
                OrderId = order.Id,
                Provider = _provider.Name,
                Reference = Guid.NewGuid().ToString("N"),
                Amount = order.Total,
                Status = PaymentAttemptStatus.Created,
                CreatedAt = now
            };
            _db.Payments.Add(payment);

            order.PaymentStatus = PaymentStatus.Pending;
            order.UpdatedAt = now;

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var session = await _provider.CreateSessionAsync(new ProviderPaymentRequest(
            payment.Reference, payment.Amount, orderNumber, culture, returnUrl, callbackUrl));

        return new StartPaymentResult(StartPaymentStatus.Started, session.RedirectUrl);
    }

    public async Task<CallbackResult> HandleCallbackAsync(IReadOnlyDictionary<string, string> fields)
    {
        var answer = _provider?.ParseCallback(fields);
        if (answer is null)
        {
            return new CallbackResult(CallbackStatus.InvalidSignature);
        }

        var known = await _db.Payments
            .Where(p => p.Reference == answer.Reference)
            .Select(p => new { p.Id, OrderNumber = p.Order.Number })
            .FirstOrDefaultAsync();
        if (known is null)
        {
            return new CallbackResult(CallbackStatus.UnknownReference);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _db.AcquireAsync("order:" + known.OrderNumber);

        // Read again after the lock: another answer may have been handled while we waited.
        var payment = await _db.Payments.Include(p => p.Order).FirstAsync(p => p.Id == known.Id);
        var order = payment.Order;

        if (answer.Amount != payment.Amount)
        {
            return new CallbackResult(CallbackStatus.AmountMismatch);
        }

        var now = Now();

        if (answer.Result == PaymentAttemptStatus.Paid)
        {
            if (payment.Status == PaymentAttemptStatus.Paid)
            {
                return new CallbackResult(CallbackStatus.AlreadyProcessed);
            }

            payment.Status = PaymentAttemptStatus.Paid;
            payment.CompletedAt = now;

            if (order.Status == OrderStatus.AwaitingPayment)
            {
                order.Status = OrderStatus.Ordered;
                order.PaymentStatus = PaymentStatus.Paid;
                order.UpdatedAt = now;
                order.Events.Add(new OrderEvent { At = now, Status = OrderStatus.Ordered, Note = "Paid online" });

                // A card order is ready to be prepared only now (cash and transfer orders were announced when placed).
                NotificationWriter.NewOrder(_db, order.Number, order.Total, now);

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();

                return new CallbackResult(CallbackStatus.Paid, await _orders.FindForSystemAsync(order.Number));
            }

            // The money arrived but the order cannot use it: it was cancelled (time ran out) or an earlier attempt already paid it.
            payment.NeedsRefund = true;
            if (order.Status == OrderStatus.Cancelled)
            {
                order.PaymentStatus = PaymentStatus.Paid;
            }

            order.UpdatedAt = now;
            order.Events.Add(new OrderEvent { At = now, Status = order.Status, Note = "Payment received that cannot be used: refund needed" });
            NotificationWriter.RefundNeeded(_db, order.Number, payment.Amount, now);

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            return new CallbackResult(CallbackStatus.PaidNeedsRefund);
        }

        // Failed or cancelled: only an attempt that is still open changes.
        if (payment.Status != PaymentAttemptStatus.Created)
        {
            return new CallbackResult(CallbackStatus.AlreadyProcessed);
        }

        payment.Status = answer.Result;
        payment.CompletedAt = now;

        if (answer.Result == PaymentAttemptStatus.Failed && order.Status == OrderStatus.AwaitingPayment)
        {
            order.PaymentStatus = PaymentStatus.Failed;
            order.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return new CallbackResult(answer.Result == PaymentAttemptStatus.Failed ? CallbackStatus.Failed : CallbackStatus.Cancelled);
    }

    public async Task<PaymentAttemptStatus?> GetLatestAttemptAsync(int orderNumber)
    {
        return await _db.Payments
            .AsNoTracking()
            .Where(p => p.Order.Number == orderNumber)
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Select(p => (PaymentAttemptStatus?)p.Status)
            .FirstOrDefaultAsync();
    }

    public async Task<PaymentAttemptInfo?> GetAttemptAsync(string reference)
    {
        return await _db.Payments
            .AsNoTracking()
            .Where(p => p.Reference == reference)
            .Select(p => new PaymentAttemptInfo(p.Reference, p.Order.Number, p.Amount, p.Status))
            .FirstOrDefaultAsync();
    }

    public async Task<int> ExpireUnpaidOrdersAsync()
    {
        var now = Now();
        var cardLimit = now.AddMinutes(-_options.CardTimeoutMinutes);
        var transferLimit = now.AddDays(-_options.BankTransferTimeoutDays);

        var numbers = await _db.Orders
            .Where(o => o.Status == OrderStatus.AwaitingPayment
                && ((o.PaymentMethod == PaymentMethod.Card && o.CreatedAt < cardLimit)
                    || (o.PaymentMethod == PaymentMethod.BankTransfer && o.CreatedAt < transferLimit)))
            .Select(o => o.Number)
            .ToListAsync();

        var cancelled = 0;
        foreach (var number in numbers)
        {
            // "Only if still awaiting payment": an order that was paid a moment ago is left alone.
            var result = await _orders.ChangeStatusAsync(
                number, OrderStatus.Cancelled, null, OrderNotes.PaymentTimeout, onlyIfCurrently: OrderStatus.AwaitingPayment);

            if (result == ChangeStatusResult.Changed)
            {
                cancelled++;
            }
        }

        return cancelled;
    }

    public async Task<MarkPaidResult> MarkBankTransferPaidAsync(int orderNumber, string? byUserId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _db.AcquireAsync("order:" + orderNumber);

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Number == orderNumber);
        if (order is null)
        {
            return MarkPaidResult.NotFound;
        }

        if (order.PaymentMethod != PaymentMethod.BankTransfer || order.Status != OrderStatus.AwaitingPayment)
        {
            return MarkPaidResult.NotAllowed;
        }

        var now = Now();
        order.Status = OrderStatus.Ordered;
        order.PaymentStatus = PaymentStatus.Paid;
        order.UpdatedAt = now;
        order.Events.Add(new OrderEvent { At = now, Status = OrderStatus.Ordered, Note = "Bank transfer received", ByUserId = byUserId });

        // The customer is told that the money arrived.
        NotificationWriter.Email(_db, Shop.Core.Notifications.EmailKind.TransferReceived, order.Number, now);

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return MarkPaidResult.Marked;
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;
}

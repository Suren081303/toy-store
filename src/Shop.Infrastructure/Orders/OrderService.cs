using System.Buffers.Text;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Shop.Core.Carts;
using Shop.Core.Entities;
using Shop.Core.Admin;
using Shop.Core.Localization;
using Shop.Core.Notifications;
using Shop.Core.Orders;
using Shop.Infrastructure.Data;
using Shop.Infrastructure.Notifications;

namespace Shop.Infrastructure.Orders;

public class OrderService : IOrderService
{
    private const int MinReturnReasonLength = 5;
    private const int MaxReturnReasonLength = 1000;

    private readonly ShopDbContext _db;
    private readonly ICartService _cart;
    private readonly IStoreSettingsService _settings;
    private readonly TimeProvider _time;

    public OrderService(ShopDbContext db, ICartService cart, IStoreSettingsService settings, TimeProvider time)
    {
        _db = db;
        _cart = cart;
        _settings = settings;
        _time = time;
    }

    public async Task<PlaceOrderResult> PlaceAsync(CartOwner owner, CheckoutInput input, string culture)
    {
        // Everything below happens in one transaction: if anything fails, nothing is saved (and no stock is taken).
        await using var transaction = await _db.Database.BeginTransactionAsync();

        // A second submit of the same checkout waits here until the first one is finished, then finds its order.
        await LockAsync("checkout:" + input.CheckoutId);
        var existing = await LoadSummaryAsync(o => o.CheckoutId == input.CheckoutId);
        if (existing is not null)
        {
            return new PlaceOrderResult(PlaceOrderStatus.AlreadyPlaced, existing);
        }

        // Two orders of the same customer at the same moment are handled one after the other,
        // so the "first 5 orders" discount can never be given six times.
        if (owner.UserId is not null)
        {
            await LockAsync("customer:" + owner.UserId);
        }

        var cart = await _cart.GetAsync(owner, culture);
        if (cart.Lines.Count == 0)
        {
            return new PlaceOrderResult(PlaceOrderStatus.EmptyCart);
        }

        var unavailable = cart.Lines.Where(l => !l.IsAvailable).Select(l => new StockProblem(l.ProductId, l.Name, 0)).ToList();
        if (unavailable.Count > 0)
        {
            return new PlaceOrderResult(PlaceOrderStatus.UnavailableItems, Problems: unavailable);
        }

        var settings = await _settings.GetAsync();
        var slot = settings.Slots.FirstOrDefault(s => s.Id == input.DeliverySlotId);
        var today = ShopClock.Today(_time);

        if (!Regions.IsValid(input.Region)
            || slot is null
            || !DeliveryRules.IsAllowedDate(input.DeliveryDate, today, settings.DeliveryFirstDayOffset, settings.DeliveryDaysAhead))
        {
            return new PlaceOrderResult(PlaceOrderStatus.InvalidDelivery);
        }

        var deliveryFee = DeliveryRules.GetFee(cart.Total, settings.DeliveryFee, settings.FreeDeliveryFrom);
        var total = cart.Total + deliveryFee;
        if (total != input.ExpectedTotal)
        {
            return new PlaceOrderResult(PlaceOrderStatus.PriceChanged, NewTotal: total);
        }

        // Reserve the stock. The condition "StockQuantity >= quantity" and the subtraction are one SQL statement,
        // so two customers can never both take the last item.
        var problems = new List<StockProblem>();
        var runningLow = new List<(int ProductId, int Left)>();
        foreach (var line in cart.Lines)
        {
            var productId = line.ProductId;
            var quantity = line.Quantity;

            var taken = await _db.Products
                .Where(p => p.Id == productId && p.StockQuantity >= quantity)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, p => p.StockQuantity - quantity));

            if (taken == 0)
            {
                var available = await _db.Products.Where(p => p.Id == productId).Select(p => p.StockQuantity).FirstOrDefaultAsync();
                problems.Add(new StockProblem(productId, line.Name, available));
            }
            else
            {
                // The admins are told once, when a product crosses the "low" line on its way down.
                var left = await _db.Products.Where(p => p.Id == productId).Select(p => p.StockQuantity).FirstAsync();
                if (left <= StockLevels.Low && left + quantity > StockLevels.Low)
                {
                    runningLow.Add((productId, left));
                }
            }
        }

        if (problems.Count > 0)
        {
            return new PlaceOrderResult(PlaceOrderStatus.OutOfStock, Problems: problems);
        }

        var ids = cart.Lines.Select(l => l.ProductId).ToList();
        var products = await _db.Products
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Include(p => p.Translations.Where(t => t.Culture == Cultures.Default))
            .ToDictionaryAsync(p => p.Id);

        var now = Now();
        var status = input.PaymentMethod == PaymentMethod.CashOnDelivery ? OrderStatus.Ordered : OrderStatus.AwaitingPayment;

        var order = new Order
        {
            AccessKey = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
            CheckoutId = input.CheckoutId,
            UserId = owner.UserId,
            CustomerName = input.CustomerName,
            Email = input.Email,
            Phone = input.Phone,
            Region = input.Region,
            City = input.City,
            Street = input.Street,
            Building = input.Building,
            Apartment = string.IsNullOrWhiteSpace(input.Apartment) ? null : input.Apartment,
            Comment = string.IsNullOrWhiteSpace(input.Comment) ? null : input.Comment,
            DeliveryDate = input.DeliveryDate,
            DeliverySlot = slot.Label,
            PaymentMethod = input.PaymentMethod,
            PaymentStatus = PaymentStatus.Pending,
            Status = status,
            Subtotal = cart.RegularTotal,
            Discount = cart.Savings,
            DeliveryFee = deliveryFee,
            Total = total,
            CustomerDiscountApplied = cart.CustomerDiscountItemCount > 0,
            Culture = culture,
            CreatedAt = now,
            UpdatedAt = now,
            AcceptedTermsAt = now
        };

        foreach (var line in cart.Lines)
        {
            var product = products[line.ProductId];
            order.Items.Add(new OrderItem
            {
                ProductId = line.ProductId,
                Sku = product.Sku,
                Brand = line.Brand,
                Name = line.Name,
                NameHy = product.Translations.ResolveName(Cultures.Default),
                Quantity = line.Quantity,
                RegularPrice = line.RegularPrice,
                UnitPrice = line.UnitPrice,
                DiscountPercent = line.DiscountPercent,
                CustomerDiscountApplied = line.CustomerDiscountApplied
            });
        }

        order.Events.Add(new OrderEvent { At = now, Status = status, Note = "Order placed" });

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        // The order number exists now. A cash or bank transfer order is ready to be prepared at once: the confirmation email,
        // the shop's notice and the bell are queued in this same transaction. A card order waits for the bank's "paid" answer.
        if (input.PaymentMethod != PaymentMethod.Card)
        {
            NotificationWriter.NewOrder(_db, order.Number, order.Total, now);
        }

        foreach (var (productId, left) in runningLow)
        {
            NotificationWriter.LowStock(_db, productId, products[productId].Translations.ResolveName(Cultures.Default), left, now);
        }

        await _db.SaveChangesAsync();
        await _cart.ClearAsync(owner);

        await transaction.CommitAsync();

        var summary = await LoadSummaryAsync(o => o.Id == order.Id);
        return new PlaceOrderResult(PlaceOrderStatus.Placed, summary);
    }

    public Task<OrderSummary?> FindForUserAsync(string userId, int number)
    {
        return LoadSummaryAsync(o => o.Number == number && o.UserId == userId);
    }

    public async Task<OrderSummary?> FindByKeyAsync(int number, string accessKey)
    {
        var order = await LoadSummaryAsync(o => o.Number == number);
        if (order is null)
        {
            return null;
        }

        // Compared in constant time, so the response time does not reveal how much of a guessed key was right.
        var expected = Encoding.UTF8.GetBytes(order.AccessKey);
        var given = Encoding.UTF8.GetBytes(accessKey ?? string.Empty);

        return expected.Length == given.Length && CryptographicOperations.FixedTimeEquals(expected, given) ? order : null;
    }

    public Task<OrderSummary?> FindByEmailAsync(int number, string email)
    {
        var trimmed = email.Trim();
        return LoadSummaryAsync(o => o.Number == number && o.Email == trimmed);
    }

    public async Task<IReadOnlyList<OrderListItem>> ListForUserAsync(string userId)
    {
        var rows = await _db.Orders
            .AsNoTracking()
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new { o.Number, o.CreatedAt, o.Status, o.Total, ItemCount = o.Items.Sum(i => i.Quantity) })
            .ToListAsync();

        return rows.Select(r => new OrderListItem(r.Number, r.CreatedAt, r.Status, r.Total, r.ItemCount)).ToList();
    }

    public async Task<LastAddress?> GetLastAddressAsync(string userId)
    {
        return await _db.Orders
            .AsNoTracking()
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new LastAddress(o.CustomerName, o.Phone, o.Region, o.City, o.Street, o.Building, o.Apartment))
            .FirstOrDefaultAsync();
    }

    public Task<OrderSummary?> FindForSystemAsync(int number)
    {
        return LoadSummaryAsync(o => o.Number == number);
    }

    public async Task<ChangeStatusResult> ChangeStatusAsync(
        int number, OrderStatus newStatus, string? byUserId, string? note, OrderStatus? onlyIfCurrently = null)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();

        // Two changes of the same order (for example two cancels) are handled one after the other,
        // so the stock can never be put back twice.
        await LockAsync("order:" + number);

        var order = await _db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Number == number);
        if (order is null)
        {
            return ChangeStatusResult.NotFound;
        }

        if (!OrderStatusRules.CanChange(order.Status, newStatus) || (onlyIfCurrently is not null && order.Status != onlyIfCurrently))
        {
            return ChangeStatusResult.NotAllowed;
        }

        var now = Now();
        order.Status = newStatus;
        order.UpdatedAt = now;

        if (newStatus == OrderStatus.Delivered)
        {
            order.DeliveredAt = now;

            // Cash is collected when the order is handed over.
            if (order.PaymentMethod == PaymentMethod.CashOnDelivery)
            {
                order.PaymentStatus = PaymentStatus.Paid;
            }
        }

        // A cancelled order gives its stock back, once.
        if (newStatus == OrderStatus.Cancelled && !order.StockReleased)
        {
            foreach (var item in order.Items)
            {
                var productId = item.ProductId;
                var quantity = item.Quantity;

                await _db.Products
                    .Where(p => p.Id == productId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, p => p.StockQuantity + quantity));
            }

            order.StockReleased = true;
        }

        order.Events.Add(new OrderEvent { At = now, Status = newStatus, Note = note, ByUserId = byUserId });

        // The customer is told about the main steps (queued in this transaction, sent by the email job).
        switch (newStatus)
        {
            case OrderStatus.Packed:
                NotificationWriter.Email(_db, EmailKind.OrderPacked, order.Number, now);
                break;
            case OrderStatus.Delivered:
                NotificationWriter.Email(_db, EmailKind.OrderDelivered, order.Number, now);
                break;
            case OrderStatus.Cancelled:
                NotificationWriter.Email(_db, EmailKind.OrderCancelled, order.Number, now, detail: note == OrderNotes.PaymentTimeout ? "expired" : note);
                break;
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return ChangeStatusResult.Changed;
    }

    public async Task<ReturnRequestResult> RequestReturnAsync(int number, string reason)
    {
        var text = (reason ?? string.Empty).Trim();
        if (text.Length < MinReturnReasonLength || text.Length > MaxReturnReasonLength)
        {
            return ReturnRequestResult.InvalidReason;
        }

        // Same lock as a status change: two requests at the same moment cannot both be created.
        await using var transaction = await _db.Database.BeginTransactionAsync();
        await LockAsync("order:" + number);

        var order = await _db.Orders.Include(o => o.ReturnRequests).FirstOrDefaultAsync(o => o.Number == number);
        if (order is null)
        {
            return ReturnRequestResult.NotFound;
        }

        if (order.Status != OrderStatus.Delivered || order.DeliveredAt is null)
        {
            return ReturnRequestResult.NotDelivered;
        }

        var settings = await _settings.GetAsync();
        if (Now() > order.DeliveredAt.Value.AddDays(settings.ReturnWindowDays))
        {
            return ReturnRequestResult.WindowClosed;
        }

        if (order.ReturnRequests.Any(r => r.Status != ReturnStatus.Rejected))
        {
            return ReturnRequestResult.AlreadyRequested;
        }

        var request = new ReturnRequest { Reason = text, Status = ReturnStatus.Requested, CreatedAt = Now() };
        order.ReturnRequests.Add(request);
        order.UpdatedAt = Now();
        await _db.SaveChangesAsync();

        // The request has its id now: tell the admins (bell and the shop's email) in this same transaction.
        NotificationWriter.ReturnRequested(_db, order.Number, request.Id, Now());
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return ReturnRequestResult.Created;
    }

    // ---- helpers ----

    private Task LockAsync(string resource) => _db.AcquireAsync(resource);

    private async Task<OrderSummary?> LoadSummaryAsync(Expression<Func<Order, bool>> filter)
    {
        var order = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.ReturnRequests)
            .FirstOrDefaultAsync(filter);

        return order is null ? null : await ToSummaryAsync(order);
    }

    private async Task<OrderSummary> ToSummaryAsync(Order order)
    {
        var ids = order.Items.Select(i => i.ProductId).ToList();
        var images = await _db.ProductImages
            .AsNoTracking()
            .Where(i => ids.Contains(i.ProductId))
            .OrderBy(i => i.SortOrder)
            .ToListAsync();
        var firstImage = images.GroupBy(i => i.ProductId).ToDictionary(g => g.Key, g => g.First().ThumbPath ?? g.First().Path);

        var settings = await _settings.GetAsync();

        var lines = order.Items
            .OrderBy(i => i.Id)
            .Select(i => new OrderLine(
                i.ProductId, i.Name, i.Brand, firstImage.GetValueOrDefault(i.ProductId),
                i.Quantity, i.RegularPrice, i.UnitPrice, i.DiscountPercent, i.CustomerDiscountApplied))
            .ToList();

        var latestReturn = order.ReturnRequests.OrderByDescending(r => r.CreatedAt).FirstOrDefault();
        var returnInfo = latestReturn is null
            ? null
            : new ReturnInfo(latestReturn.Status, latestReturn.Reason, latestReturn.CreatedAt, latestReturn.AdminNote);

        var canRequestReturn =
            order.Status == OrderStatus.Delivered
            && order.DeliveredAt is DateTime deliveredAt
            && Now() <= deliveredAt.AddDays(settings.ReturnWindowDays)
            && order.ReturnRequests.All(r => r.Status == ReturnStatus.Rejected);

        return new OrderSummary(
            order.Number, order.AccessKey, order.CreatedAt, order.Status, order.PaymentMethod, order.PaymentStatus,
            order.CustomerName, order.Email, order.Phone,
            order.Region, order.City, order.Street, order.Building, order.Apartment, order.Comment,
            order.DeliveryDate, order.DeliverySlot,
            order.Subtotal, order.Discount, order.DeliveryFee, order.Total, order.CustomerDiscountApplied,
            order.Culture, lines, order.DeliveredAt, returnInfo, canRequestReturn);
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;
}

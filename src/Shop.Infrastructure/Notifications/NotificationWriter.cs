using Shop.Core.Entities;
using Shop.Core.Notifications;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Notifications;

// Adds email-queue rows and bell items to the database context. They are saved by the caller's own SaveChanges, inside the same
// transaction as the order change that caused them: either everything happens or nothing does, and an email is never lost.
internal static class NotificationWriter
{
    public static void Email(ShopDbContext db, EmailKind kind, int orderNumber, DateTime now, int? returnId = null, string? detail = null)
    {
        db.OutboxEmails.Add(new OutboxEmail
        {
            CreatedAt = now,
            Kind = kind,
            OrderNumber = orderNumber,
            ReturnId = returnId,
            Detail = detail is { Length: > 1000 } ? detail[..1000] : detail,
            NextAttemptAt = now
        });
    }

    // An order that is ready to be prepared: the customer's confirmation, the shop's notice, and the bell.
    public static void NewOrder(ShopDbContext db, int orderNumber, decimal total, DateTime now)
    {
        Email(db, EmailKind.OrderConfirmation, orderNumber, now);
        Email(db, EmailKind.ShopNewOrder, orderNumber, now);
        db.AdminNotifications.Add(new AdminNotification { CreatedAt = now, Type = NotificationType.NewOrder, OrderNumber = orderNumber, Amount = total });
    }

    public static void ReturnRequested(ShopDbContext db, int orderNumber, int returnId, DateTime now)
    {
        Email(db, EmailKind.ShopReturnRequested, orderNumber, now, returnId);
        db.AdminNotifications.Add(new AdminNotification { CreatedAt = now, Type = NotificationType.ReturnRequested, OrderNumber = orderNumber, ReturnId = returnId });
    }

    public static void RefundNeeded(ShopDbContext db, int orderNumber, decimal amount, DateTime now)
    {
        Email(db, EmailKind.ShopRefundNeeded, orderNumber, now);
        db.AdminNotifications.Add(new AdminNotification { CreatedAt = now, Type = NotificationType.RefundNeeded, OrderNumber = orderNumber, Amount = amount });
    }

    // Only the bell, no email: low stock is common and not urgent.
    public static void LowStock(ShopDbContext db, int productId, string name, int stock, DateTime now)
    {
        db.AdminNotifications.Add(new AdminNotification { CreatedAt = now, Type = NotificationType.LowStock, ProductId = productId, Name = name, Stock = stock });
    }
}

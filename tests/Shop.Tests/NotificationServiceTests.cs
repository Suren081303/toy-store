using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Entities;
using Shop.Core.Notifications;
using Shop.Core.Orders;
using Shop.Core.Payments;
using Shop.Infrastructure.Notifications;

namespace Shop.Tests;

public class OutboxRulesTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(9, 60)]
    public void RetryDelay_GrowsWithEachFailure_AndStopsAtAnHour(int failedAttempts, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), OutboxRules.RetryDelay(failedAttempts));
    }

    [Fact]
    public void OnlyTheShopKinds_AreForTheShop()
    {
        var forShop = Enum.GetValues<EmailKind>().Where(EmailKinds.IsForShop).ToList();

        Assert.Equal(
            new[] { EmailKind.ShopNewOrder, EmailKind.ShopReturnRequested, EmailKind.ShopRefundNeeded }.Order(),
            forShop.Order());
    }
}

[Collection("Orders")]
public class NotificationServiceTests
{
    private readonly OrderFixture _fx;

    public NotificationServiceTests(OrderFixture fx)
    {
        _fx = fx;
    }

    private async Task<List<OutboxEmail>> EmailsAsync(int orderNumber)
    {
        using var db = _fx.CreateDbContext();
        return await db.OutboxEmails.Where(e => e.OrderNumber == orderNumber).OrderBy(e => e.Id).ToListAsync();
    }

    private async Task<List<AdminNotification>> BellAsync(int orderNumber)
    {
        using var db = _fx.CreateDbContext();
        return await db.AdminNotifications.Where(n => n.OrderNumber == orderNumber).OrderBy(n => n.Id).ToListAsync();
    }

    // Tests that look at the whole bell start from an empty one (each test makes its own data; nothing relies on old rows).
    private async Task ClearBellAsync()
    {
        using var db = _fx.CreateDbContext();
        await db.AdminNotifications.ExecuteDeleteAsync();
    }

    private async Task<OrderSummary> DeliveredOrderAsync()
    {
        var order = await _fx.PlaceOrderAsync(quantity: 2);
        await _fx.CreateOrderService().ChangeStatusAsync(order.Number, OrderStatus.Packed, null, null);
        await _fx.CreateOrderService().ChangeStatusAsync(order.Number, OrderStatus.Delivered, null, null);
        return order;
    }

    private async Task<int> ReturnIdAsync(OrderSummary order)
    {
        Assert.Equal(ReturnRequestResult.Created, await _fx.CreateOrderService().RequestReturnAsync(order.Number, "The wheel is broken"));
        using var db = _fx.CreateDbContext();
        return await db.ReturnRequests.Where(r => r.Order.Number == order.Number).Select(r => r.Id).SingleAsync();
    }

    // ---- New orders ----

    [Theory]
    [InlineData(PaymentMethod.CashOnDelivery)]
    [InlineData(PaymentMethod.BankTransfer)]
    public async Task Placing_ACashOrTransferOrder_QueuesTheConfirmationAndShopNotice_AndRingsTheBell(PaymentMethod method)
    {
        var order = await _fx.PlaceOrderAsync(method: method);

        var emails = await EmailsAsync(order.Number);
        Assert.Equal([EmailKind.OrderConfirmation, EmailKind.ShopNewOrder], emails.Select(e => e.Kind));
        Assert.All(emails, e =>
        {
            Assert.Equal(OutboxStatus.Pending, e.Status);
            Assert.Equal(0, e.Attempts);
            Assert.Equal(OrderFixture.Now, e.NextAttemptAt); // due at once
        });

        var bell = Assert.Single(await BellAsync(order.Number));
        Assert.Equal(NotificationType.NewOrder, bell.Type);
        Assert.Equal(order.Total, bell.Amount);
        Assert.False(bell.IsRead);
    }

    [Fact]
    public async Task Placing_ACardOrder_QueuesNothing_UntilTheBankSaysPaid_AndOnlyOnce()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);
        Assert.Empty(await EmailsAsync(order.Number));
        Assert.Empty(await BellAsync(order.Number));

        var started = await _fx.CreatePaymentService().StartCardPaymentAsync(order.Number, "en", "r", "c");
        var answer = StubPaymentProvider.Answer(started.RedirectUrl!.Split('/').Last(), PaymentAttemptStatus.Paid, order.Total);

        await _fx.CreatePaymentService().HandleCallbackAsync(answer);
        await _fx.CreatePaymentService().HandleCallbackAsync(answer); // the bank sends it twice

        Assert.Equal([EmailKind.OrderConfirmation, EmailKind.ShopNewOrder], (await EmailsAsync(order.Number)).Select(e => e.Kind));
        Assert.Single(await BellAsync(order.Number));
    }

    [Fact]
    public async Task ARefusedOrder_LeavesNoQueueRowsAndNoBellItems()
    {
        var productId = await _fx.CreateProductAsync(stock: 5);
        var owner = await _fx.FillCartAsync(null, (productId, 1));
        var input = await _fx.InputAsync(owner, expectedTotal: 1); // wrong total: refused

        using var before = _fx.CreateDbContext();
        var emailsBefore = await before.OutboxEmails.CountAsync();
        var bellBefore = await before.AdminNotifications.CountAsync();

        Assert.Equal(PlaceOrderStatus.PriceChanged, (await _fx.CreateOrderService().PlaceAsync(owner, input, "en")).Status);

        using var after = _fx.CreateDbContext();
        Assert.Equal(emailsBefore, await after.OutboxEmails.CountAsync());
        Assert.Equal(bellBefore, await after.AdminNotifications.CountAsync());
    }

    [Fact]
    public async Task TheSameCheckoutSentManyTimesAtOnce_QueuesTheEmailsOnce()
    {
        var productId = await _fx.CreateProductAsync(stock: 10);
        var owner = await _fx.FillCartAsync(null, (productId, 1));
        var input = await _fx.InputAsync(owner);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => _fx.CreateOrderService().PlaceAsync(owner, input, "en"))));

        var number = results.Single(r => r.Status == PlaceOrderStatus.Placed).Order!.Number;
        Assert.Equal(2, (await EmailsAsync(number)).Count);
        Assert.Single(await BellAsync(number));
    }

    // ---- Low stock ----

    [Fact]
    public async Task LowStock_RingsTheBellOnce_WhenAProductCrossesTheLine()
    {
        var productId = await _fx.CreateProductAsync(stock: 5);

        async Task<List<AdminNotification>> LowItems()
        {
            using var db = _fx.CreateDbContext();
            return await db.AdminNotifications.Where(n => n.Type == NotificationType.LowStock && n.ProductId == productId).OrderBy(n => n.Id).ToListAsync();
        }

        async Task Buy(int quantity)
        {
            var owner = await _fx.FillCartAsync(null, (productId, quantity));
            Assert.Equal(PlaceOrderStatus.Placed, (await _fx.CreateOrderService().PlaceAsync(owner, await _fx.InputAsync(owner), "en")).Status);
        }

        await Buy(1); // 5 -> 4: still above the line
        Assert.Empty(await LowItems());

        await Buy(1); // 4 -> 3: crosses it
        var item = Assert.Single(await LowItems());
        Assert.Equal(3, item.Stock);
        Assert.StartsWith("Խաղ", item.Name); // the Armenian name

        await Buy(1); // 3 -> 2: already low, no second item
        Assert.Single(await LowItems());
    }

    [Fact]
    public async Task LowStock_IsNotReported_ForAnOrderThatLeavesPlentyOfStock()
    {
        var productId = await _fx.CreateProductAsync(stock: 20);
        var owner = await _fx.FillCartAsync(null, (productId, 2));
        await _fx.CreateOrderService().PlaceAsync(owner, await _fx.InputAsync(owner), "en");

        using var db = _fx.CreateDbContext();
        Assert.False(await db.AdminNotifications.AnyAsync(n => n.Type == NotificationType.LowStock && n.ProductId == productId));
    }

    // ---- Status news for the customer ----

    [Fact]
    public async Task StatusChanges_QueueTheCustomersEmails()
    {
        var order = await _fx.PlaceOrderAsync();
        var orders = _fx.CreateOrderService();

        await orders.ChangeStatusAsync(order.Number, OrderStatus.Packed, null, null);
        await _fx.CreateOrderService().ChangeStatusAsync(order.Number, OrderStatus.Delivered, null, null);

        var kinds = (await EmailsAsync(order.Number)).Select(e => e.Kind).ToList();
        Assert.Equal([EmailKind.OrderConfirmation, EmailKind.ShopNewOrder, EmailKind.OrderPacked, EmailKind.OrderDelivered], kinds);
    }

    [Fact]
    public async Task Cancelling_QueuesAnEmail_WithTheAdminsNote_OrNone()
    {
        var withNote = await _fx.PlaceOrderAsync();
        var withoutNote = await _fx.PlaceOrderAsync();

        await _fx.CreateOrderService().ChangeStatusAsync(withNote.Number, OrderStatus.Cancelled, null, "Out of stock at the supplier");
        await _fx.CreateOrderService().ChangeStatusAsync(withoutNote.Number, OrderStatus.Cancelled, null, null);

        var noted = (await EmailsAsync(withNote.Number)).Single(e => e.Kind == EmailKind.OrderCancelled);
        Assert.Equal("Out of stock at the supplier", noted.Detail);
        Assert.Null((await EmailsAsync(withoutNote.Number)).Single(e => e.Kind == EmailKind.OrderCancelled).Detail);
    }

    [Fact]
    public async Task TheTimeoutCancel_IsMarkedAsExpired_InTheEmail()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);

        await _fx.CreatePaymentService(OrderFixture.Now.AddHours(1)).ExpireUnpaidOrdersAsync();

        var cancelled = Assert.Single(await EmailsAsync(order.Number)); // a card order had no confirmation yet
        Assert.Equal(EmailKind.OrderCancelled, cancelled.Kind);
        Assert.Equal("expired", cancelled.Detail);
    }

    [Fact]
    public async Task ARefusedStatusChange_QueuesNothing()
    {
        var order = await _fx.PlaceOrderAsync();
        var before = (await EmailsAsync(order.Number)).Count;

        Assert.Equal(ChangeStatusResult.NotAllowed, await _fx.CreateOrderService().ChangeStatusAsync(order.Number, OrderStatus.Delivered, null, null));

        Assert.Equal(before, (await EmailsAsync(order.Number)).Count);
    }

    [Fact]
    public async Task ABankTransferReceived_QueuesTheCustomersEmail_Once()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.BankTransfer);

        Assert.Equal(MarkPaidResult.Marked, await _fx.CreatePaymentService().MarkBankTransferPaidAsync(order.Number, null));
        Assert.Equal(MarkPaidResult.NotAllowed, await _fx.CreatePaymentService().MarkBankTransferPaidAsync(order.Number, null));

        Assert.Equal(1, (await EmailsAsync(order.Number)).Count(e => e.Kind == EmailKind.TransferReceived));
    }

    // ---- Returns ----

    [Fact]
    public async Task ARequestedReturn_RingsTheBell_AndQueuesTheShopsEmail()
    {
        var order = await DeliveredOrderAsync();

        var returnId = await ReturnIdAsync(order);

        var bell = (await BellAsync(order.Number)).Single(n => n.Type == NotificationType.ReturnRequested);
        Assert.Equal(returnId, bell.ReturnId);
        var email = (await EmailsAsync(order.Number)).Single(e => e.Kind == EmailKind.ShopReturnRequested);
        Assert.Equal(returnId, email.ReturnId);
    }

    [Theory]
    [InlineData(ReturnStatus.Rejected, EmailKind.ReturnRejected)]
    [InlineData(ReturnStatus.Returned, EmailKind.ReturnReceived)]
    public async Task EachReturnAnswer_QueuesTheCustomersEmail_WithTheNote(ReturnStatus answer, EmailKind expected)
    {
        var adminId = await _fx.CreateUserAsync();
        var order = await DeliveredOrderAsync();
        var returnId = await ReturnIdAsync(order);

        await _fx.CreateAdminServices().Returns.ChangeStatusAsync(returnId, answer, "  A note for you  ", restock: true, adminId);

        var email = (await EmailsAsync(order.Number)).Single(e => e.Kind == expected);
        Assert.Equal("A note for you", email.Detail);
        Assert.Equal(returnId, email.ReturnId);
    }

    [Fact]
    public async Task TheRefundAnswer_QueuesTheCustomersEmail()
    {
        var adminId = await _fx.CreateUserAsync();
        var order = await DeliveredOrderAsync();
        var returnId = await ReturnIdAsync(order);
        await _fx.CreateAdminServices().Returns.ChangeStatusAsync(returnId, ReturnStatus.Returned, null, true, adminId);

        await _fx.CreateAdminServices().Returns.ChangeStatusAsync(returnId, ReturnStatus.Refunded, null, false, adminId);

        Assert.Contains(await EmailsAsync(order.Number), e => e.Kind == EmailKind.ReturnRefunded && e.Detail == null);
    }

    // ---- Money that cannot be used ----

    [Fact]
    public async Task APaymentThatCannotBeUsed_RingsTheBell_AndQueuesTheShopsEmail()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);
        var started = await _fx.CreatePaymentService().StartCardPaymentAsync(order.Number, "en", "r", "c");
        await _fx.CreateOrderService().ChangeStatusAsync(order.Number, OrderStatus.Cancelled, null, "Time ran out");

        await _fx.CreatePaymentService().HandleCallbackAsync(StubPaymentProvider.Answer(started.RedirectUrl!.Split('/').Last(), PaymentAttemptStatus.Paid, order.Total));

        var bell = (await BellAsync(order.Number)).Single(n => n.Type == NotificationType.RefundNeeded);
        Assert.Equal(order.Total, bell.Amount);
        Assert.Contains(await EmailsAsync(order.Number), e => e.Kind == EmailKind.ShopRefundNeeded);
    }

    // ---- The bell ----

    [Fact]
    public async Task TheBell_CountsUnread_ShowsTheLatest_AndReadStateIsShared()
    {
        var adminA = await _fx.CreateUserAsync();
        var adminB = await _fx.CreateUserAsync();
        var service = () => new AdminNotificationService(_fx.CreateDbContext(), new FixedTimeProvider(OrderFixture.Now));
        await ClearBellAsync();

        var first = await _fx.PlaceOrderAsync();
        var second = await _fx.PlaceOrderAsync();

        Assert.Equal(2, await service().UnreadCountAsync());
        var summary = await service().SummaryAsync(5);
        Assert.Equal(2, summary.Unread);
        Assert.Equal([second.Number, first.Number], summary.Latest.Take(2).Select(n => n.OrderNumber!.Value)); // newest first

        var firstItem = summary.Latest.Single(n => n.OrderNumber == first.Number);
        await service().MarkReadAsync(firstItem.Id, adminA);
        await service().MarkReadAsync(firstItem.Id, adminB); // a second admin: the first reader stays

        Assert.Equal(1, await service().UnreadCountAsync());
        using var db = _fx.CreateDbContext();
        var saved = await db.AdminNotifications.SingleAsync(n => n.Id == firstItem.Id);
        Assert.True(saved.IsRead);
        Assert.Equal(adminA, saved.ReadByUserId);

        Assert.Equal(1, await service().MarkAllReadAsync(adminB));
        Assert.Equal(0, await service().UnreadCountAsync());
        Assert.Equal(0, await service().MarkAllReadAsync(adminB));
    }

    [Fact]
    public async Task TheBellList_IsPaged_FiltersUnread_AndFindsOneById()
    {
        var service = () => new AdminNotificationService(_fx.CreateDbContext(), new FixedTimeProvider(OrderFixture.Now));
        await ClearBellAsync();

        using (var db = _fx.CreateDbContext())
        {
            for (var i = 0; i < 65; i++)
            {
                db.AdminNotifications.Add(new AdminNotification
                {
                    CreatedAt = OrderFixture.Now.AddMinutes(i),
                    Type = NotificationType.LowStock,
                    ProductId = 1000 + i,
                    Name = "Product " + i,
                    Stock = 1,
                    IsRead = i % 2 == 0
                });
            }

            await db.SaveChangesAsync();
        }

        var all = await service().ListAsync(unreadOnly: false, page: 1);
        Assert.Equal(30, all.PageSize);
        Assert.Equal(30, all.Items.Count);
        Assert.Equal("Product 64", all.Items[0].Name); // newest first
        Assert.Equal(3, all.TotalPages);

        var unread = await service().ListAsync(unreadOnly: true, page: 1);
        Assert.Equal(32, unread.TotalCount); // the odd ones of 65
        Assert.All(unread.Items, n => Assert.False(n.IsRead));
        Assert.Empty((await service().ListAsync(false, 99)).Items);

        var one = await service().GetAsync(all.Items[3].Id);
        Assert.Equal(all.Items[3].Name, one!.Name);
        Assert.Null(await service().GetAsync(-1));
    }

    // ---- The email queue screen ----

    private async Task<int> AddEmailAsync(OutboxStatus status, int attempts = 0, DateTime? createdAt = null, DateTime? sentAt = null)
    {
        using var db = _fx.CreateDbContext();
        var email = new OutboxEmail
        {
            CreatedAt = createdAt ?? OrderFixture.Now,
            Kind = EmailKind.OrderPacked,
            OrderNumber = 1,
            Status = status,
            Attempts = attempts,
            NextAttemptAt = OrderFixture.Now.AddHours(1),
            SentAt = sentAt,
            LastError = status == OutboxStatus.Failed ? "SmtpException: server down" : null
        };
        db.OutboxEmails.Add(email);
        await db.SaveChangesAsync();
        return email.Id;
    }

    [Fact]
    public async Task EmailList_FiltersByStatus_AndRetryPutsOnlyAFailedEmailBack()
    {
        var adminId = await _fx.CreateUserAsync();
        var failed = await AddEmailAsync(OutboxStatus.Failed, attempts: 5);
        var sent = await AddEmailAsync(OutboxStatus.Sent, attempts: 1, sentAt: OrderFixture.Now);
        var pending = await AddEmailAsync(OutboxStatus.Pending, attempts: 2);
        var service = () => new AdminEmailService(_fx.CreateDbContext(), new FixedTimeProvider(OrderFixture.Now));

        var failedList = await service().ListAsync(OutboxStatus.Failed, 1);
        var row = failedList.Items.Single(e => e.Id == failed);
        Assert.Equal("SmtpException: server down", row.LastError);
        Assert.DoesNotContain(failedList.Items, e => e.Id == sent || e.Id == pending);
        Assert.True((await service().FailedCountAsync()) >= 1);

        Assert.False(await service().RetryAsync(sent, adminId));
        Assert.False(await service().RetryAsync(pending, adminId));
        Assert.False(await service().RetryAsync(-1, adminId));

        Assert.True(await service().RetryAsync(failed, adminId));
        using var db = _fx.CreateDbContext();
        var saved = await db.OutboxEmails.SingleAsync(e => e.Id == failed);
        Assert.Equal(OutboxStatus.Pending, saved.Status);
        Assert.Equal(0, saved.Attempts);
        Assert.Equal(OrderFixture.Now, saved.NextAttemptAt); // due at once
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Email.Retry" && a.EntityId == failed.ToString() && a.UserId == adminId));

        Assert.False(await service().RetryAsync(failed, adminId)); // already back in the queue
    }

    [Fact]
    public async Task EmailList_IsNewestFirst_AndPaged()
    {
        var ids = new List<int>();
        for (var i = 0; i < 35; i++)
        {
            ids.Add(await AddEmailAsync(OutboxStatus.Sent, createdAt: OrderFixture.Now.AddYears(10).AddMinutes(i), sentAt: OrderFixture.Now)); // newer than anything other tests make, even with a faked clock
        }

        var page = await new AdminEmailService(_fx.CreateDbContext(), new FixedTimeProvider(OrderFixture.Now)).ListAsync(null, 1);

        Assert.Equal(30, page.PageSize);
        Assert.Equal(30, page.Items.Count);
        Assert.True(page.Items.Zip(page.Items.Skip(1), (a, b) => a.CreatedAt >= b.CreatedAt).All(x => x));
        Assert.Equal(ids[^1], page.Items[0].Id); // the newest of these is first

        using var db = _fx.CreateDbContext();
        await db.OutboxEmails.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync();
    }

    // ---- Clean-up ----

    [Fact]
    public async Task Cleanup_DeletesOldBellItemsAndOldEmails_AndKeepsTheRest()
    {
        await ClearBellAsync();
        var now = OrderFixture.Now.AddYears(5); // far from every other test's rows
        var day = TimeSpan.FromDays(1);
        int oldBell, freshBell, oldSent, freshSent, oldFailed, freshFailed, pending;

        using (var db = _fx.CreateDbContext())
        {
            var oldN = new AdminNotification { CreatedAt = now - 91 * day, Type = NotificationType.LowStock, Name = "old" };
            var freshN = new AdminNotification { CreatedAt = now - 89 * day, Type = NotificationType.LowStock, Name = "fresh" };
            db.AdminNotifications.AddRange(oldN, freshN);

            var emails = new[]
            {
                new OutboxEmail { Kind = EmailKind.OrderPacked, OrderNumber = 1, Status = OutboxStatus.Sent, CreatedAt = now - 40 * day, SentAt = now - 31 * day },
                new OutboxEmail { Kind = EmailKind.OrderPacked, OrderNumber = 1, Status = OutboxStatus.Sent, CreatedAt = now - 40 * day, SentAt = now - 29 * day },
                new OutboxEmail { Kind = EmailKind.OrderPacked, OrderNumber = 1, Status = OutboxStatus.Failed, CreatedAt = now - 91 * day },
                new OutboxEmail { Kind = EmailKind.OrderPacked, OrderNumber = 1, Status = OutboxStatus.Failed, CreatedAt = now - 60 * day },
                new OutboxEmail { Kind = EmailKind.OrderPacked, OrderNumber = 1, Status = OutboxStatus.Pending, CreatedAt = now - 200 * day }
            };
            db.OutboxEmails.AddRange(emails);
            await db.SaveChangesAsync();

            (oldBell, freshBell) = (oldN.Id, freshN.Id);
            (oldSent, freshSent, oldFailed, freshFailed, pending) = (emails[0].Id, emails[1].Id, emails[2].Id, emails[3].Id, emails[4].Id);
        }

        var (notifications, deletedEmails) = await new NotificationCleanup(_fx.CreateDbContext(), new FixedTimeProvider(now)).CleanAsync();

        Assert.True(notifications >= 1);
        Assert.True(deletedEmails >= 2);
        using var check = _fx.CreateDbContext();
        Assert.False(await check.AdminNotifications.AnyAsync(n => n.Id == oldBell));
        Assert.True(await check.AdminNotifications.AnyAsync(n => n.Id == freshBell));
        Assert.False(await check.OutboxEmails.AnyAsync(e => e.Id == oldSent));       // sent more than 30 days ago
        Assert.True(await check.OutboxEmails.AnyAsync(e => e.Id == freshSent));      // sent 29 days ago
        Assert.False(await check.OutboxEmails.AnyAsync(e => e.Id == oldFailed));     // failed 91 days ago
        Assert.True(await check.OutboxEmails.AnyAsync(e => e.Id == freshFailed));    // failed 60 days ago: an admin may still retry it
        Assert.True(await check.OutboxEmails.AnyAsync(e => e.Id == pending));        // still waiting: never deleted

        // Tidy up: these rows are dated years ahead and must not disturb other tests.
        await check.OutboxEmails.Where(e => e.Id == freshSent || e.Id == freshFailed || e.Id == pending).ExecuteDeleteAsync();
        await check.AdminNotifications.Where(n => n.Id == freshBell).ExecuteDeleteAsync();
    }
}

using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Orders;
using Shop.Core.Payments;

namespace Shop.Tests;

public class ReturnStatusRulesTests
{
    [Theory]
    [InlineData(ReturnStatus.Requested, ReturnStatus.Rejected, true)]
    [InlineData(ReturnStatus.Requested, ReturnStatus.Returned, true)]
    [InlineData(ReturnStatus.Requested, ReturnStatus.Refunded, false)] // the goods must come back first
    [InlineData(ReturnStatus.Returned, ReturnStatus.Refunded, true)]
    [InlineData(ReturnStatus.Returned, ReturnStatus.Rejected, false)]
    [InlineData(ReturnStatus.Rejected, ReturnStatus.Returned, false)]
    [InlineData(ReturnStatus.Refunded, ReturnStatus.Returned, false)]
    public void ReturnStatus_FollowsThePath(ReturnStatus from, ReturnStatus to, bool expected)
    {
        Assert.Equal(expected, ReturnStatusRules.CanChange(from, to));
    }

    [Fact]
    public void StartOfDayUtc_IsFourHoursBeforeMidnightInArmenia()
    {
        var start = ShopClock.StartOfDayUtc(new DateOnly(2026, 6, 2));

        Assert.Equal(new DateTime(2026, 6, 1, 20, 0, 0, DateTimeKind.Utc), start);
    }
}

[Collection("Orders")]
public class AdminServiceTests
{
    private readonly OrderFixture _fx;

    public AdminServiceTests(OrderFixture fx)
    {
        _fx = fx;
    }

    private async Task<OrderSummary> DeliveredOrderAsync(int quantity = 2, int stock = 10)
    {
        var order = await _fx.PlaceOrderAsync(quantity: quantity, stock: stock);
        var orders = _fx.CreateOrderService();
        await orders.ChangeStatusAsync(order.Number, OrderStatus.Packed, null, null);
        await orders.ChangeStatusAsync(order.Number, OrderStatus.Delivered, null, null);
        return order;
    }

    private async Task<int> ReturnIdOfAsync(OrderSummary order, string reason = "The toy is broken")
    {
        Assert.Equal(ReturnRequestResult.Created, await _fx.CreateOrderService().RequestReturnAsync(order.Number, reason));

        using var db = _fx.CreateDbContext();
        return await db.ReturnRequests.Where(r => r.Order.Number == order.Number).Select(r => r.Id).SingleAsync();
    }

    private async Task<int> StockOfAsync(OrderSummary order) => await _fx.GetStockAsync(order.Lines[0].ProductId);

    // ---- Dashboard ----

    [Fact]
    public async Task Dashboard_CountsWhatNeedsAttention()
    {
        var admin = _fx.CreateAdminServices().Orders;
        var before = await admin.GetDashboardAsync();

        await _fx.PlaceOrderAsync();                                        // to pack
        await _fx.PlaceOrderAsync(method: PaymentMethod.BankTransfer);      // awaiting payment
        var delivered = await DeliveredOrderAsync();
        await ReturnIdOfAsync(delivered);                                   // open return
        await _fx.CreateProductAsync(stock: 1);                             // low stock

        var after = await admin.GetDashboardAsync();

        Assert.Equal(before.OrdersToday + 3, after.OrdersToday);
        Assert.Equal(before.ToPack + 1, after.ToPack);
        Assert.Equal(before.AwaitingPayment + 1, after.AwaitingPayment);
        Assert.Equal(before.OpenReturns + 1, after.OpenReturns);
        Assert.True(after.LowStock >= before.LowStock + 1);
    }

    [Fact]
    public async Task Dashboard_OrdersToday_UsesTheArmenianDay_NotTheUtcDay()
    {
        // This order is from 19:30 UTC = 23:30 on June 10 in Armenia.
        var order = await _fx.PlaceOrderAsync();
        using (var db = _fx.CreateDbContext())
        {
            await db.Orders.Where(o => o.Number == order.Number)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedAt, new DateTime(2026, 6, 10, 19, 30, 0, DateTimeKind.Utc)));
        }

        // At 23:45 in Armenia (19:45 UTC) it is still June 10: the order is from today.
        var sameDay = await _fx.CreateAdminServices(new DateTime(2026, 6, 10, 19, 45, 0, DateTimeKind.Utc)).Orders.GetDashboardAsync();
        Assert.Equal(1, sameDay.OrdersToday);

        // At 01:00 on June 11 in Armenia (21:00 UTC, still June 10 in UTC) it is already yesterday's order.
        var nextDay = await _fx.CreateAdminServices(new DateTime(2026, 6, 10, 21, 0, 0, DateTimeKind.Utc)).Orders.GetDashboardAsync();
        Assert.Equal(0, nextDay.OrdersToday);
    }

    // ---- Order list ----

    [Fact]
    public async Task List_FindsOrdersByNumberEmailNameAndPhone()
    {
        var order = await _fx.PlaceOrderAsync();
        var admin = _fx.CreateAdminServices().Orders;

        Assert.Contains(order.Number, (await admin.ListAsync(new AdminOrderFilter { Query = order.Number.ToString() })).Items.Select(i => i.Number));
        Assert.Contains(order.Number, (await admin.ListAsync(new AdminOrderFilter { Query = order.Email[..12] })).Items.Select(i => i.Number));
        Assert.Contains(order.Number, (await admin.ListAsync(new AdminOrderFilter { Query = "Hakobyan" })).Items.Select(i => i.Number));
        Assert.Contains(order.Number, (await admin.ListAsync(new AdminOrderFilter { Query = "91123456" })).Items.Select(i => i.Number));
        Assert.DoesNotContain(order.Number, (await admin.ListAsync(new AdminOrderFilter { Query = "no-such-person-anywhere" })).Items.Select(i => i.Number));
    }

    [Fact]
    public async Task List_FiltersByStatusAndPaymentMethod()
    {
        var cash = await _fx.PlaceOrderAsync(method: PaymentMethod.CashOnDelivery);
        var transfer = await _fx.PlaceOrderAsync(method: PaymentMethod.BankTransfer);
        var admin = _fx.CreateAdminServices().Orders;

        var awaiting = (await admin.ListAsync(new AdminOrderFilter { Status = OrderStatus.AwaitingPayment, Query = transfer.Email })).Items;
        Assert.Equal(transfer.Number, Assert.Single(awaiting).Number);

        var byMethod = (await admin.ListAsync(new AdminOrderFilter { Method = PaymentMethod.CashOnDelivery, Query = cash.Email })).Items;
        Assert.Equal(cash.Number, Assert.Single(byMethod).Number);

        Assert.Empty((await admin.ListAsync(new AdminOrderFilter { Method = PaymentMethod.Card, Query = cash.Email })).Items);
    }

    [Fact]
    public async Task List_FiltersByArmenianDates()
    {
        var order = await _fx.PlaceOrderAsync(); // created at OrderFixture.Now (June 1, 16:00 in Armenia)
        var admin = _fx.CreateAdminServices().Orders;
        var day = new DateOnly(2026, 6, 1);

        Assert.Single((await admin.ListAsync(new AdminOrderFilter { Query = order.Email, From = day, To = day })).Items);
        Assert.Single((await admin.ListAsync(new AdminOrderFilter { Query = order.Email, From = day })).Items);
        Assert.Empty((await admin.ListAsync(new AdminOrderFilter { Query = order.Email, From = day.AddDays(1) })).Items);
        Assert.Empty((await admin.ListAsync(new AdminOrderFilter { Query = order.Email, To = day.AddDays(-1) })).Items);
    }

    [Fact]
    public async Task List_IsPaged_NewestFirst()
    {
        for (var i = 0; i < 3; i++)
        {
            await _fx.PlaceOrderAsync();
        }

        var admin = _fx.CreateAdminServices().Orders;
        var first = await admin.ListAsync(new AdminOrderFilter { Page = 1 });

        Assert.Equal(20, first.PageSize);
        Assert.True(first.TotalCount >= 3);
        Assert.True(first.Items.Count <= 20);
        Assert.True(first.Items.Zip(first.Items.Skip(1), (a, b) => a.CreatedAt >= b.CreatedAt).All(x => x));

        // A page far beyond the end is simply empty.
        Assert.Empty((await admin.ListAsync(new AdminOrderFilter { Page = 9999 })).Items);
    }

    // ---- Order page ----

    [Fact]
    public async Task Details_OffersOnlyTheAllowedNextSteps()
    {
        var admin = _fx.CreateAdminServices().Orders;

        var cash = await _fx.PlaceOrderAsync(method: PaymentMethod.CashOnDelivery);
        var cashDetail = await admin.GetAsync(cash.Number);
        Assert.Equal([OrderStatus.Packed, OrderStatus.Cancelled], cashDetail!.NextStatuses);
        Assert.False(cashDetail.CanMarkTransferReceived);

        // A card order waiting for the bank cannot be set to "Ordered" by hand, only cancelled.
        var card = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);
        Assert.Equal([OrderStatus.Cancelled], (await admin.GetAsync(card.Number))!.NextStatuses);

        // A bank transfer order: cancel, or "transfer received" (not a plain status change).
        var transfer = await _fx.PlaceOrderAsync(method: PaymentMethod.BankTransfer);
        var transferDetail = await admin.GetAsync(transfer.Number);
        Assert.Equal([OrderStatus.Cancelled], transferDetail!.NextStatuses);
        Assert.True(transferDetail.CanMarkTransferReceived);

        // Final orders have nothing left.
        var delivered = await DeliveredOrderAsync();
        var deliveredDetail = await admin.GetAsync(delivered.Number);
        Assert.Empty(deliveredDetail!.NextStatuses);
        Assert.False(deliveredDetail.CanMarkTransferReceived);

        Assert.Null(await admin.GetAsync(1));
    }

    [Fact]
    public async Task Details_ShowsTheHistoryWithWhoDidIt_AndThePayments()
    {
        var adminId = await _fx.CreateUserAsync();
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);
        var payments = _fx.CreatePaymentService();
        var started = await payments.StartCardPaymentAsync(order.Number, "en", "r", "c");
        await _fx.CreateOrderService().ChangeStatusAsync(order.Number, OrderStatus.Cancelled, adminId, "Customer called");

        var detail = await _fx.CreateAdminServices().Orders.GetAsync(order.Number);

        Assert.Equal([OrderStatus.AwaitingPayment, OrderStatus.Cancelled], detail!.Events.Select(e => e.Status));
        var cancel = detail.Events.Last();
        Assert.Equal("Customer called", cancel.Note);
        Assert.NotNull(cancel.ByEmail);
        Assert.Null(detail.Events.First().ByEmail);

        var payment = Assert.Single(detail.Payments);
        Assert.Equal(PaymentAttemptStatus.Created, payment.Status);
        Assert.Equal(order.Total, payment.Amount);
        _ = started;
    }

    // ---- Refunds ----

    private async Task<(OrderSummary Order, int PaymentId)> OrderWithUnusablePaymentAsync()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);
        var started = await _fx.CreatePaymentService().StartCardPaymentAsync(order.Number, "en", "r", "c");
        await _fx.CreateOrderService().ChangeStatusAsync(order.Number, OrderStatus.Cancelled, null, "Time ran out");
        var answer = await _fx.CreatePaymentService().HandleCallbackAsync(
            StubPaymentProvider.Answer(started.RedirectUrl!.Split('/').Last(), PaymentAttemptStatus.Paid, order.Total));
        Assert.Equal(CallbackStatus.PaidNeedsRefund, answer.Status);

        using var db = _fx.CreateDbContext();
        return (order, await db.Payments.Where(p => p.Order.Number == order.Number).Select(p => p.Id).SingleAsync());
    }

    [Fact]
    public async Task RefundDone_ClearsTheFlag_MarksTheCancelledOrderRefunded_AndIsAudited()
    {
        var adminId = await _fx.CreateUserAsync();
        var (order, paymentId) = await OrderWithUnusablePaymentAsync();
        var admin = _fx.CreateAdminServices().Orders;

        Assert.Contains(
            (await admin.ListAsync(new AdminOrderFilter { RefundNeeded = true })).Items,
            i => i.Number == order.Number && i.NeedsRefund);

        Assert.Equal(RefundDoneResult.Done, await admin.MarkRefundDoneAsync(paymentId, adminId));

        var detail = await admin.GetAsync(order.Number);
        Assert.Equal(PaymentStatus.Refunded, detail!.Order.PaymentStatus);
        Assert.False(detail.Payments.Single().NeedsRefund);
        Assert.Contains(detail.Events, e => e.ByEmail is not null && e.Note != null && e.Note.StartsWith("Refund done"));
        Assert.DoesNotContain(order.Number, (await admin.ListAsync(new AdminOrderFilter { RefundNeeded = true })).Items.Select(i => i.Number));

        using var db = _fx.CreateDbContext();
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "Payment.RefundDone" && a.EntityId == order.Number.ToString());
        Assert.Equal(adminId, audit.UserId);
        Assert.False(string.IsNullOrEmpty(audit.UserEmail));
    }

    [Fact]
    public async Task RefundDone_Twice_OrForAnUnknownPayment_ChangesNothing()
    {
        var adminId = await _fx.CreateUserAsync();
        var (_, paymentId) = await OrderWithUnusablePaymentAsync();
        var admin = _fx.CreateAdminServices().Orders;

        Assert.Equal(RefundDoneResult.Done, await admin.MarkRefundDoneAsync(paymentId, adminId));
        Assert.Equal(RefundDoneResult.NotNeeded, await admin.MarkRefundDoneAsync(paymentId, adminId));
        Assert.Equal(RefundDoneResult.NotFound, await admin.MarkRefundDoneAsync(-5, adminId));
    }

    [Fact]
    public async Task RefundDone_ForAnExtraPaymentOnAValidOrder_KeepsTheOrderPaid()
    {
        var adminId = await _fx.CreateUserAsync();
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);
        var first = (await _fx.CreatePaymentService().StartCardPaymentAsync(order.Number, "en", "r", "c")).RedirectUrl!.Split('/').Last();
        var second = (await _fx.CreatePaymentService().StartCardPaymentAsync(order.Number, "en", "r", "c")).RedirectUrl!.Split('/').Last();
        await _fx.CreatePaymentService().HandleCallbackAsync(StubPaymentProvider.Answer(first, PaymentAttemptStatus.Paid, order.Total));
        await _fx.CreatePaymentService().HandleCallbackAsync(StubPaymentProvider.Answer(second, PaymentAttemptStatus.Paid, order.Total));

        using var db = _fx.CreateDbContext();
        var extra = await db.Payments.Where(p => p.Reference == second).Select(p => p.Id).SingleAsync();

        Assert.Equal(RefundDoneResult.Done, await _fx.CreateAdminServices().Orders.MarkRefundDoneAsync(extra, adminId));

        var detail = await _fx.CreateAdminServices().Orders.GetAsync(order.Number);
        Assert.Equal(OrderStatus.Ordered, detail!.Order.Status);
        Assert.Equal(PaymentStatus.Paid, detail.Order.PaymentStatus); // the first payment still stands
    }

    // ---- Returns ----

    [Fact]
    public async Task Returns_ListShowsOpenOnesFirst_AndFiltersByStatus()
    {
        var adminId = await _fx.CreateUserAsync();
        var rejected = await DeliveredOrderAsync();
        var rejectedId = await ReturnIdOfAsync(rejected);
        await _fx.CreateAdminServices().Returns.ChangeStatusAsync(rejectedId, ReturnStatus.Rejected, "No", false, adminId);
        var open = await DeliveredOrderAsync();
        var openId = await ReturnIdOfAsync(open);

        var returns = _fx.CreateAdminServices().Returns;
        var all = (await returns.ListAsync(null, 1)).Items.ToList();

        Assert.True(all.FindIndex(r => r.Id == openId) < all.FindIndex(r => r.Id == rejectedId));
        Assert.Contains(openId, (await returns.ListAsync(ReturnStatus.Requested, 1)).Items.Select(r => r.Id));
        Assert.DoesNotContain(rejectedId, (await returns.ListAsync(ReturnStatus.Requested, 1)).Items.Select(r => r.Id));
    }

    [Fact]
    public async Task Return_Details_ShowReasonOrderAndNextSteps()
    {
        var order = await DeliveredOrderAsync();
        var id = await ReturnIdOfAsync(order, "Wrong colour");

        var detail = await _fx.CreateAdminServices().Returns.GetAsync(id);

        Assert.Equal("Wrong colour", detail!.Reason);
        Assert.Equal(order.Number, detail.Order.Number);
        Assert.Equal([ReturnStatus.Rejected, ReturnStatus.Returned], detail.NextStatuses);
        Assert.Null(await _fx.CreateAdminServices().Returns.GetAsync(-1));
    }

    [Fact]
    public async Task Return_Returned_WithRestock_PutsTheItemsBack_Once()
    {
        var adminId = await _fx.CreateUserAsync();
        var order = await DeliveredOrderAsync(quantity: 3, stock: 10);
        var id = await ReturnIdOfAsync(order);
        Assert.Equal(7, await StockOfAsync(order));

        var returns = _fx.CreateAdminServices().Returns;
        Assert.Equal(ChangeReturnResult.Changed, await returns.ChangeStatusAsync(id, ReturnStatus.Returned, "Received in good shape", restock: true, adminId));
        Assert.Equal(10, await StockOfAsync(order));

        // The customer sees the note and the new status.
        var saved = await _fx.CreateOrderService().FindForSystemAsync(order.Number);
        Assert.Equal(ReturnStatus.Returned, saved!.Return!.Status);
        Assert.Equal("Received in good shape", saved.Return.AdminNote);

        // It cannot be "returned" a second time, so the stock cannot be added twice.
        Assert.Equal(ChangeReturnResult.NotAllowed, await returns.ChangeStatusAsync(id, ReturnStatus.Returned, null, restock: true, adminId));
        Assert.Equal(10, await StockOfAsync(order));
    }

    [Fact]
    public async Task Return_Returned_WithoutRestock_LeavesTheStockAlone()
    {
        var adminId = await _fx.CreateUserAsync();
        var order = await DeliveredOrderAsync(quantity: 2, stock: 10);
        var id = await ReturnIdOfAsync(order);

        await _fx.CreateAdminServices().Returns.ChangeStatusAsync(id, ReturnStatus.Returned, null, restock: false, adminId);

        Assert.Equal(8, await StockOfAsync(order));
    }

    [Fact]
    public async Task Return_Refunded_MarksThePaidOrderRefunded()
    {
        var adminId = await _fx.CreateUserAsync();
        var order = await DeliveredOrderAsync(); // cash orders are paid when delivered
        var id = await ReturnIdOfAsync(order);
        var returns = _fx.CreateAdminServices().Returns;

        // Money cannot go back before the goods came back.
        Assert.Equal(ChangeReturnResult.NotAllowed, await returns.ChangeStatusAsync(id, ReturnStatus.Refunded, null, false, adminId));

        await returns.ChangeStatusAsync(id, ReturnStatus.Returned, null, true, adminId);
        Assert.Equal(ChangeReturnResult.Changed, await returns.ChangeStatusAsync(id, ReturnStatus.Refunded, "Sent to the card", false, adminId));

        var saved = await _fx.CreateOrderService().FindForSystemAsync(order.Number);
        Assert.Equal(PaymentStatus.Refunded, saved!.PaymentStatus);
        Assert.Equal(OrderStatus.Delivered, saved.Status);

        // The analytics count the refund on the day the money went back.
        using var db = _fx.CreateDbContext();
        Assert.Equal(OrderFixture.Now, (await db.ReturnRequests.SingleAsync(r => r.Id == id)).RefundedAt);
    }

    [Fact]
    public async Task Return_Rejected_IsFinal_AndTheCustomerMayAskAgain()
    {
        var adminId = await _fx.CreateUserAsync();
        var order = await DeliveredOrderAsync();
        var id = await ReturnIdOfAsync(order);
        var returns = _fx.CreateAdminServices().Returns;

        Assert.Equal(ChangeReturnResult.Changed, await returns.ChangeStatusAsync(id, ReturnStatus.Rejected, "Outside the rules", false, adminId));
        Assert.Equal(ChangeReturnResult.NotAllowed, await returns.ChangeStatusAsync(id, ReturnStatus.Returned, null, true, adminId));
        Assert.Equal(ChangeReturnResult.NotFound, await returns.ChangeStatusAsync(-1, ReturnStatus.Rejected, null, false, adminId));

        Assert.Equal(ReturnRequestResult.Created, await _fx.CreateOrderService().RequestReturnAsync(order.Number, "A better explanation"));
    }

    [Fact]
    public async Task Return_ChangedTwiceAtTheSameMoment_IsAppliedOnce()
    {
        var adminId = await _fx.CreateUserAsync();
        var order = await DeliveredOrderAsync(quantity: 2, stock: 10);
        var id = await ReturnIdOfAsync(order);

        var results = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => Task.Run(() => _fx.CreateAdminServices().Returns.ChangeStatusAsync(id, ReturnStatus.Returned, null, true, adminId))));

        Assert.Equal(1, results.Count(r => r == ChangeReturnResult.Changed));
        Assert.Equal(10, await StockOfAsync(order)); // 8 + 2, not more
    }

    [Fact]
    public async Task Return_Changes_AreRecordedInTheHistoryAndTheAuditLog()
    {
        var adminId = await _fx.CreateUserAsync();
        var order = await DeliveredOrderAsync();
        var id = await ReturnIdOfAsync(order);

        await _fx.CreateAdminServices().Returns.ChangeStatusAsync(id, ReturnStatus.Returned, null, true, adminId);

        var detail = await _fx.CreateAdminServices().Orders.GetAsync(order.Number);
        Assert.Contains(detail!.Events, e => e.Note != null && e.Note.Contains("Return Returned") && e.ByEmail is not null);

        using var db = _fx.CreateDbContext();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Return.Returned" && a.EntityId == order.Number.ToString() && a.UserId == adminId));
    }

    // ---- Settings ----

    [Fact]
    public async Task Settings_Save_ChangesWhatCustomersSee_AndIsAudited()
    {
        var adminId = await _fx.CreateUserAsync();
        var settings = _fx.CreateAdminServices().Settings;
        var original = (await settings.GetAsync()).Settings;

        try
        {
            await settings.SaveSettingsAsync(new AdminSettingsInput
            {
                DeliveryFee = 2000,
                FreeDeliveryFrom = 30000,
                DeliveryDaysAhead = 10,
                DeliveryFirstDayOffset = 2,
                ReturnWindowDays = 30,
                BankName = "  New Bank ",
                BankAccount = "AM11 1111",
                BankRecipient = "Toys LLC"
            }, adminId);

            // What the shop reads now.
            var shop = await new Shop.Infrastructure.Orders.StoreSettingsService(_fx.CreateDbContext()).GetAsync();
            Assert.Equal(2000, shop.DeliveryFee);
            Assert.Equal(30000, shop.FreeDeliveryFrom);
            Assert.Equal(10, shop.DeliveryDaysAhead);
            Assert.Equal(2, shop.DeliveryFirstDayOffset);
            Assert.Equal(30, shop.ReturnWindowDays);
            Assert.Equal("New Bank", shop.BankName); // trimmed

            using var db = _fx.CreateDbContext();
            var audit = await db.AuditLogs.Where(a => a.Action == "Settings.Saved").OrderByDescending(a => a.Id).FirstAsync();
            Assert.Equal(adminId, audit.UserId);
            Assert.Contains("DeliveryFee", audit.Details);
            Assert.Contains("1500", audit.Details); // the old value is in the record
            Assert.Contains("2000", audit.Details);
        }
        finally
        {
            await settings.SaveSettingsAsync(original, adminId);
        }
    }

    [Fact]
    public async Task Settings_Slots_CanBeAddedChangedAndHidden_ButNotAllHidden()
    {
        var adminId = await _fx.CreateUserAsync();
        var settings = _fx.CreateAdminServices().Settings;
        var originalSlots = (await settings.GetAsync()).Slots;

        try
        {
            Assert.Equal(SaveSlotResult.Saved, await settings.SaveSlotAsync(null, " 20:00–22:00 ", 99, true, adminId));
            var added = (await settings.GetAsync()).Slots.Single(s => s.Label == "20:00–22:00");
            Assert.Equal(99, added.SortOrder);

            Assert.Equal(SaveSlotResult.Saved, await settings.SaveSlotAsync(added.Id, "20:00–23:00", 98, false, adminId));
            var changed = (await settings.GetAsync()).Slots.Single(s => s.Id == added.Id);
            Assert.Equal("20:00–23:00", changed.Label);
            Assert.False(changed.IsActive);

            // Hidden slots are not offered at checkout.
            var shop = await new Shop.Infrastructure.Orders.StoreSettingsService(_fx.CreateDbContext()).GetAsync();
            Assert.DoesNotContain(shop.Slots, s => s.Id == added.Id);

            Assert.Equal(SaveSlotResult.NotFound, await settings.SaveSlotAsync(-1, "x", 1, true, adminId));

            // Hide all but one: the last one cannot be hidden.
            var active = (await settings.GetAsync()).Slots.Where(s => s.IsActive).ToList();
            foreach (var slot in active.Skip(1))
            {
                Assert.Equal(SaveSlotResult.Saved, await settings.SaveSlotAsync(slot.Id, slot.Label, slot.SortOrder, false, adminId));
            }

            var last = active[0];
            Assert.Equal(SaveSlotResult.LastActiveSlot, await settings.SaveSlotAsync(last.Id, last.Label, last.SortOrder, false, adminId));
            Assert.True((await settings.GetAsync()).Slots.Single(s => s.Id == last.Id).IsActive);

            using var db = _fx.CreateDbContext();
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Slot.Added" && a.EntityId == added.Id.ToString()));
        }
        finally
        {
            // Put every slot back as it was, and hide the one this test added.
            foreach (var slot in originalSlots)
            {
                await settings.SaveSlotAsync(slot.Id, slot.Label, slot.SortOrder, slot.IsActive, adminId);
            }

            var extra = (await settings.GetAsync()).Slots.Where(s => s.Label.StartsWith("20:00") && s.IsActive).ToList();
            foreach (var slot in extra)
            {
                await settings.SaveSlotAsync(slot.Id, slot.Label, slot.SortOrder, false, adminId);
            }
        }
    }
}

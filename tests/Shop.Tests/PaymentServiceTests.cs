using Microsoft.EntityFrameworkCore;
using Shop.Core.Orders;
using Shop.Core.Payments;

namespace Shop.Tests;

[Collection("Orders")]
public class PaymentServiceTests
{
    private readonly OrderFixture _fx;

    public PaymentServiceTests(OrderFixture fx)
    {
        _fx = fx;
    }

    private static string ReferenceOf(StartPaymentResult started) => started.RedirectUrl!.Split('/').Last();

    // A card order waiting for payment, with its payment started. Returns the order and the bank's reference.
    private async Task<(OrderSummary Order, string Reference)> CardOrderWithPaymentAsync(int quantity = 1, int stock = 10)
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card, quantity: quantity, stock: stock);
        var started = await _fx.CreatePaymentService().StartCardPaymentAsync(order.Number, "en", "https://shop.test/return", "https://shop.test/callback");

        Assert.Equal(StartPaymentStatus.Started, started.Status);
        return (order, ReferenceOf(started));
    }

    private async Task<OrderSummary> ReloadAsync(OrderSummary order) => (await _fx.CreateOrderService().FindForSystemAsync(order.Number))!;

    private async Task<int> StockOfAsync(OrderSummary order) => await _fx.GetStockAsync(order.Lines[0].ProductId);

    // ---- Starting a payment ----

    [Fact]
    public async Task Start_ForACardOrder_CreatesAnAttempt_ForTheOrderTotal()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);

        var started = await _fx.CreatePaymentService().StartCardPaymentAsync(order.Number, "en", "https://shop.test/return", "https://shop.test/callback");

        Assert.Equal(StartPaymentStatus.Started, started.Status);
        Assert.StartsWith("https://bank.test/pay/", started.RedirectUrl);

        var attempt = await _fx.CreatePaymentService().GetAttemptAsync(ReferenceOf(started));
        Assert.Equal(order.Number, attempt!.OrderNumber);
        Assert.Equal(order.Total, attempt.Amount);
        Assert.Equal(PaymentAttemptStatus.Created, attempt.Status);
    }

    [Fact]
    public async Task Start_ForACashOrder_OrAnUnknownOrder_IsRefused()
    {
        var cash = await _fx.PlaceOrderAsync(method: PaymentMethod.CashOnDelivery);
        var transfer = await _fx.PlaceOrderAsync(method: PaymentMethod.BankTransfer);
        var service = _fx.CreatePaymentService();

        Assert.Equal(StartPaymentStatus.NotPayable, (await service.StartCardPaymentAsync(cash.Number, "en", "r", "c")).Status);
        Assert.Equal(StartPaymentStatus.NotPayable, (await service.StartCardPaymentAsync(transfer.Number, "en", "r", "c")).Status);
        Assert.Equal(StartPaymentStatus.OrderNotFound, (await service.StartCardPaymentAsync(1, "en", "r", "c")).Status);
    }

    [Fact]
    public async Task Start_WithoutACardProvider_IsUnavailable()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);
        var service = _fx.CreatePaymentService(withCardProvider: false);

        Assert.False(service.CardPaymentAvailable);
        Assert.Equal(StartPaymentStatus.ProviderUnavailable, (await service.StartCardPaymentAsync(order.Number, "en", "r", "c")).Status);
    }

    [Fact]
    public async Task Start_AfterTheDeadline_OrForACancelledOrder_IsRefused()
    {
        var late = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);
        Assert.Equal(
            StartPaymentStatus.NotPayable,
            (await _fx.CreatePaymentService(OrderFixture.Now.AddMinutes(31)).StartCardPaymentAsync(late.Number, "en", "r", "c")).Status);

        var cancelled = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);
        await _fx.CreateOrderService().ChangeStatusAsync(cancelled.Number, OrderStatus.Cancelled, null, null);
        Assert.Equal(
            StartPaymentStatus.NotPayable,
            (await _fx.CreatePaymentService().StartCardPaymentAsync(cancelled.Number, "en", "r", "c")).Status);
    }

    [Fact]
    public async Task Start_Again_ReplacesTheOpenAttempt()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);
        var service = _fx.CreatePaymentService();

        var first = ReferenceOf(await service.StartCardPaymentAsync(order.Number, "en", "r", "c"));
        var second = ReferenceOf(await service.StartCardPaymentAsync(order.Number, "en", "r", "c"));

        Assert.NotEqual(first, second);
        Assert.Equal(PaymentAttemptStatus.Cancelled, (await service.GetAttemptAsync(first))!.Status);
        Assert.Equal(PaymentAttemptStatus.Created, (await service.GetAttemptAsync(second))!.Status);
        Assert.Equal(PaymentAttemptStatus.Created, await service.GetLatestAttemptAsync(order.Number));
    }

    // ---- The bank's answer: paid ----

    [Fact]
    public async Task Paid_MovesTheOrderToOrdered_AndHandsItBackOnce()
    {
        var (order, reference) = await CardOrderWithPaymentAsync();
        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);

        var answer = await _fx.CreatePaymentService().HandleCallbackAsync(StubPaymentProvider.Answer(reference, PaymentAttemptStatus.Paid, order.Total));

        Assert.Equal(CallbackStatus.Paid, answer.Status);
        Assert.Equal(order.Number, answer.Order!.Number);

        var saved = await ReloadAsync(order);
        Assert.Equal(OrderStatus.Ordered, saved.Status);
        Assert.Equal(PaymentStatus.Paid, saved.PaymentStatus);

        using var db = _fx.CreateDbContext();
        var events = await db.OrderEvents.Where(e => e.Order.Number == order.Number).OrderBy(e => e.Id).Select(e => e.Status).ToListAsync();
        Assert.Equal([OrderStatus.AwaitingPayment, OrderStatus.Ordered], events);
    }

    [Fact]
    public async Task Paid_TheSameAnswerAgain_ChangesNothing()
    {
        var (order, reference) = await CardOrderWithPaymentAsync();
        var fields = StubPaymentProvider.Answer(reference, PaymentAttemptStatus.Paid, order.Total);

        var first = await _fx.CreatePaymentService().HandleCallbackAsync(fields);
        var second = await _fx.CreatePaymentService().HandleCallbackAsync(fields);

        Assert.Equal(CallbackStatus.Paid, first.Status);
        Assert.Equal(CallbackStatus.AlreadyProcessed, second.Status);
        Assert.Null(second.Order); // so the emails are not sent twice
    }

    [Fact]
    public async Task Paid_TheSameAnswerManyTimesAtOnce_IsHandledOnce()
    {
        var (order, reference) = await CardOrderWithPaymentAsync();
        var fields = StubPaymentProvider.Answer(reference, PaymentAttemptStatus.Paid, order.Total);

        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => _fx.CreatePaymentService().HandleCallbackAsync(fields))));

        Assert.Equal(1, results.Count(r => r.Status == CallbackStatus.Paid));
        Assert.Equal(4, results.Count(r => r.Status == CallbackStatus.AlreadyProcessed));
        Assert.Equal(OrderStatus.Ordered, (await ReloadAsync(order)).Status);
    }

    // ---- The bank's answer: not believed ----

    [Fact]
    public async Task AnAnswerWithABadSignature_ChangesNothing()
    {
        var (order, reference) = await CardOrderWithPaymentAsync();

        var answer = await _fx.CreatePaymentService().HandleCallbackAsync(
            StubPaymentProvider.Answer(reference, PaymentAttemptStatus.Paid, order.Total, signature: "forged"));

        Assert.Equal(CallbackStatus.InvalidSignature, answer.Status);
        Assert.Equal(OrderStatus.AwaitingPayment, (await ReloadAsync(order)).Status);
    }

    [Fact]
    public async Task AnAnswerWithTheWrongAmount_ChangesNothing()
    {
        var (order, reference) = await CardOrderWithPaymentAsync();

        var answer = await _fx.CreatePaymentService().HandleCallbackAsync(
            StubPaymentProvider.Answer(reference, PaymentAttemptStatus.Paid, order.Total - 1));

        Assert.Equal(CallbackStatus.AmountMismatch, answer.Status);
        Assert.Equal(OrderStatus.AwaitingPayment, (await ReloadAsync(order)).Status);
        Assert.Equal(PaymentAttemptStatus.Created, await _fx.CreatePaymentService().GetLatestAttemptAsync(order.Number));
    }

    [Fact]
    public async Task AnAnswerForAnUnknownPayment_IsRefused()
    {
        var answer = await _fx.CreatePaymentService().HandleCallbackAsync(
            StubPaymentProvider.Answer("no-such-reference", PaymentAttemptStatus.Paid, 100));

        Assert.Equal(CallbackStatus.UnknownReference, answer.Status);
    }

    [Fact]
    public async Task WithoutAProvider_NoAnswerIsAccepted()
    {
        var (order, reference) = await CardOrderWithPaymentAsync();

        var answer = await _fx.CreatePaymentService(withCardProvider: false).HandleCallbackAsync(
            StubPaymentProvider.Answer(reference, PaymentAttemptStatus.Paid, order.Total));

        Assert.Equal(CallbackStatus.InvalidSignature, answer.Status);
    }

    // ---- The bank's answer: failed and cancelled ----

    [Fact]
    public async Task Failed_KeepsTheOrderWaiting_AndAllowsANewAttemptThatCanSucceed()
    {
        var (order, reference) = await CardOrderWithPaymentAsync();

        var failed = await _fx.CreatePaymentService().HandleCallbackAsync(StubPaymentProvider.Answer(reference, PaymentAttemptStatus.Failed, order.Total));

        Assert.Equal(CallbackStatus.Failed, failed.Status);
        var waiting = await ReloadAsync(order);
        Assert.Equal(OrderStatus.AwaitingPayment, waiting.Status);
        Assert.Equal(PaymentStatus.Failed, waiting.PaymentStatus);
        Assert.Equal(PaymentAttemptStatus.Failed, await _fx.CreatePaymentService().GetLatestAttemptAsync(order.Number));

        var retry = await _fx.CreatePaymentService().StartCardPaymentAsync(order.Number, "en", "r", "c");
        Assert.Equal(StartPaymentStatus.Started, retry.Status);
        Assert.Equal(PaymentStatus.Pending, (await ReloadAsync(order)).PaymentStatus);

        var paid = await _fx.CreatePaymentService().HandleCallbackAsync(StubPaymentProvider.Answer(ReferenceOf(retry), PaymentAttemptStatus.Paid, order.Total));
        Assert.Equal(CallbackStatus.Paid, paid.Status);
        Assert.Equal(PaymentStatus.Paid, (await ReloadAsync(order)).PaymentStatus);
    }

    [Fact]
    public async Task Cancelled_ByTheCustomer_KeepsTheOrderWaiting()
    {
        var (order, reference) = await CardOrderWithPaymentAsync();

        var answer = await _fx.CreatePaymentService().HandleCallbackAsync(StubPaymentProvider.Answer(reference, PaymentAttemptStatus.Cancelled, order.Total));

        Assert.Equal(CallbackStatus.Cancelled, answer.Status);
        var saved = await ReloadAsync(order);
        Assert.Equal(OrderStatus.AwaitingPayment, saved.Status);
        Assert.Equal(PaymentStatus.Pending, saved.PaymentStatus);
    }

    [Fact]
    public async Task AFailedAnswerAfterPaid_IsIgnored()
    {
        var (order, reference) = await CardOrderWithPaymentAsync();
        await _fx.CreatePaymentService().HandleCallbackAsync(StubPaymentProvider.Answer(reference, PaymentAttemptStatus.Paid, order.Total));

        var late = await _fx.CreatePaymentService().HandleCallbackAsync(StubPaymentProvider.Answer(reference, PaymentAttemptStatus.Failed, order.Total));

        Assert.Equal(CallbackStatus.AlreadyProcessed, late.Status);
        var saved = await ReloadAsync(order);
        Assert.Equal(OrderStatus.Ordered, saved.Status);
        Assert.Equal(PaymentStatus.Paid, saved.PaymentStatus);
    }

    // ---- Money that arrives when the order cannot use it ----

    [Fact]
    public async Task PaidAfterTheOrderWasCancelled_IsKept_AndFlaggedForARefund()
    {
        var (order, reference) = await CardOrderWithPaymentAsync(quantity: 2, stock: 10);
        await _fx.CreateOrderService().ChangeStatusAsync(order.Number, OrderStatus.Cancelled, null, "Time ran out");
        Assert.Equal(10, await StockOfAsync(order)); // the stock went back

        var answer = await _fx.CreatePaymentService().HandleCallbackAsync(StubPaymentProvider.Answer(reference, PaymentAttemptStatus.Paid, order.Total));

        Assert.Equal(CallbackStatus.PaidNeedsRefund, answer.Status);
        Assert.Null(answer.Order);

        var saved = await ReloadAsync(order);
        Assert.Equal(OrderStatus.Cancelled, saved.Status);   // not brought back to life
        Assert.Equal(PaymentStatus.Paid, saved.PaymentStatus); // the money is there
        Assert.Equal(10, await StockOfAsync(order));         // the stock is not taken again

        using var db = _fx.CreateDbContext();
        Assert.True(await db.Payments.Where(p => p.Reference == reference).Select(p => p.NeedsRefund).SingleAsync());
    }

    [Fact]
    public async Task APaymentForAnOrderThatIsAlreadyPaid_IsFlaggedForARefund()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);
        var service = _fx.CreatePaymentService();
        var first = ReferenceOf(await service.StartCardPaymentAsync(order.Number, "en", "r", "c"));
        var second = ReferenceOf(await service.StartCardPaymentAsync(order.Number, "en", "r", "c")); // the customer pressed "pay" twice

        var a = await service.HandleCallbackAsync(StubPaymentProvider.Answer(first, PaymentAttemptStatus.Paid, order.Total));
        var b = await service.HandleCallbackAsync(StubPaymentProvider.Answer(second, PaymentAttemptStatus.Paid, order.Total));

        Assert.Equal(CallbackStatus.Paid, a.Status);
        Assert.Equal(CallbackStatus.PaidNeedsRefund, b.Status);
        Assert.Equal(OrderStatus.Ordered, (await ReloadAsync(order)).Status);

        using var db = _fx.CreateDbContext();
        Assert.False(await db.Payments.Where(p => p.Reference == first).Select(p => p.NeedsRefund).SingleAsync());
        Assert.True(await db.Payments.Where(p => p.Reference == second).Select(p => p.NeedsRefund).SingleAsync());
    }

    // ---- Expiry ----

    [Fact]
    public async Task Expire_CancelsACardOrderAfterThirtyMinutes_AndPutsTheStockBack()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card, quantity: 3, stock: 10);
        Assert.Equal(7, await StockOfAsync(order));

        await _fx.CreatePaymentService(OrderFixture.Now.AddMinutes(31)).ExpireUnpaidOrdersAsync();

        Assert.Equal(OrderStatus.Cancelled, (await ReloadAsync(order)).Status);
        Assert.Equal(10, await StockOfAsync(order));
    }

    [Fact]
    public async Task Expire_LeavesACardOrderAloneBeforeTheDeadline()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);

        await _fx.CreatePaymentService(OrderFixture.Now.AddMinutes(29)).ExpireUnpaidOrdersAsync();

        Assert.Equal(OrderStatus.AwaitingPayment, (await ReloadAsync(order)).Status);
    }

    [Fact]
    public async Task Expire_GivesABankTransferThreeDays()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.BankTransfer, quantity: 2, stock: 10);

        await _fx.CreatePaymentService(OrderFixture.Now.AddDays(2)).ExpireUnpaidOrdersAsync();
        Assert.Equal(OrderStatus.AwaitingPayment, (await ReloadAsync(order)).Status);

        await _fx.CreatePaymentService(OrderFixture.Now.AddDays(4)).ExpireUnpaidOrdersAsync();
        Assert.Equal(OrderStatus.Cancelled, (await ReloadAsync(order)).Status);
        Assert.Equal(10, await StockOfAsync(order));
    }

    [Fact]
    public async Task Expire_NeverTouchesCashOrders_OrOrdersThatWerePaid()
    {
        var cash = await _fx.PlaceOrderAsync(method: PaymentMethod.CashOnDelivery);
        var (paid, reference) = await CardOrderWithPaymentAsync();
        await _fx.CreatePaymentService().HandleCallbackAsync(StubPaymentProvider.Answer(reference, PaymentAttemptStatus.Paid, paid.Total));

        await _fx.CreatePaymentService(OrderFixture.Now.AddDays(30)).ExpireUnpaidOrdersAsync();

        Assert.Equal(OrderStatus.Ordered, (await ReloadAsync(cash)).Status);
        Assert.Equal(OrderStatus.Ordered, (await ReloadAsync(paid)).Status);
    }

    [Fact]
    public async Task Expire_TwiceInARow_PutsTheStockBackOnce()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card, quantity: 3, stock: 10);
        var later = OrderFixture.Now.AddHours(1);

        var results = await Task.WhenAll(
            Task.Run(() => _fx.CreatePaymentService(later).ExpireUnpaidOrdersAsync()),
            Task.Run(() => _fx.CreatePaymentService(later).ExpireUnpaidOrdersAsync()));

        Assert.Equal(10, await StockOfAsync(order));
        Assert.True(results.Sum() >= 1);
    }

    [Fact]
    public async Task Expire_AtTheSameMomentAsAPaidAnswer_NeverBothSellsAndReleasesTheStock()
    {
        for (var i = 0; i < 4; i++)
        {
            var (order, reference) = await CardOrderWithPaymentAsync(quantity: 2, stock: 10);
            var fields = StubPaymentProvider.Answer(reference, PaymentAttemptStatus.Paid, order.Total);
            var later = OrderFixture.Now.AddHours(1);

            await Task.WhenAll(
                Task.Run(() => _fx.CreatePaymentService().HandleCallbackAsync(fields)),
                Task.Run(() => _fx.CreatePaymentService(later).ExpireUnpaidOrdersAsync()));

            var saved = await ReloadAsync(order);
            using var db = _fx.CreateDbContext();
            var needsRefund = await db.Payments.Where(p => p.Reference == reference).Select(p => p.NeedsRefund).SingleAsync();

            if (saved.Status == OrderStatus.Ordered)
            {
                // The payment won: the order is kept, the stock stays taken.
                Assert.Equal(8, await StockOfAsync(order));
                Assert.False(needsRefund);
            }
            else
            {
                // The expiry won: the stock is back, and the late payment is flagged for a refund.
                Assert.Equal(OrderStatus.Cancelled, saved.Status);
                Assert.Equal(10, await StockOfAsync(order));
                Assert.True(needsRefund);
            }
        }
    }

    // ---- Bank transfer ----

    [Fact]
    public async Task MarkBankTransferPaid_MovesTheOrderToOrdered_Once()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.BankTransfer);
        var admin = await _fx.CreateUserAsync();
        var service = _fx.CreatePaymentService();

        Assert.Equal(MarkPaidResult.Marked, await service.MarkBankTransferPaidAsync(order.Number, admin));

        var saved = await ReloadAsync(order);
        Assert.Equal(OrderStatus.Ordered, saved.Status);
        Assert.Equal(PaymentStatus.Paid, saved.PaymentStatus);

        using var db = _fx.CreateDbContext();
        Assert.Contains(await db.OrderEvents.Where(e => e.Order.Number == order.Number).ToListAsync(), e => e.ByUserId == admin);

        Assert.Equal(MarkPaidResult.NotAllowed, await service.MarkBankTransferPaidAsync(order.Number, admin));
    }

    [Fact]
    public async Task MarkBankTransferPaid_IsNotAllowed_ForOtherMethods_AndCancelledOrders()
    {
        var cash = await _fx.PlaceOrderAsync(method: PaymentMethod.CashOnDelivery);
        var card = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);
        var expired = await _fx.PlaceOrderAsync(method: PaymentMethod.BankTransfer);
        await _fx.CreatePaymentService(OrderFixture.Now.AddDays(4)).ExpireUnpaidOrdersAsync();
        var service = _fx.CreatePaymentService();

        Assert.Equal(MarkPaidResult.NotAllowed, await service.MarkBankTransferPaidAsync(cash.Number, null));
        Assert.Equal(MarkPaidResult.NotAllowed, await service.MarkBankTransferPaidAsync(card.Number, null));
        Assert.Equal(MarkPaidResult.NotAllowed, await service.MarkBankTransferPaidAsync(expired.Number, null));
        Assert.Equal(MarkPaidResult.NotFound, await service.MarkBankTransferPaidAsync(1, null));
    }

    // ---- Reading ----

    [Fact]
    public async Task GetLatestAttempt_IsNull_WithoutAttempts()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);

        Assert.Null(await _fx.CreatePaymentService().GetLatestAttemptAsync(order.Number));
        Assert.Null(await _fx.CreatePaymentService().GetAttemptAsync("missing"));
    }
}

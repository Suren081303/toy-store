using Microsoft.EntityFrameworkCore;
using Shop.Core.Carts;
using Shop.Core.Orders;

namespace Shop.Tests;

[Collection("Orders")]
public class OrderServiceTests
{
    private readonly OrderFixture _fx;

    public OrderServiceTests(OrderFixture fx)
    {
        _fx = fx;
    }

    // ---- Placing an order ----

    [Fact]
    public async Task Place_AGuestOrder_SavesASnapshot_TakesTheStock_AndEmptiesTheCart()
    {
        var productId = await _fx.CreateProductAsync(stock: 10, price: 10000);
        var owner = await _fx.FillCartAsync(null, (productId, 2));
        var input = await _fx.InputAsync(owner);

        var result = await _fx.CreateOrderService().PlaceAsync(owner, input, "en");

        Assert.Equal(PlaceOrderStatus.Placed, result.Status);
        var order = result.Order!;
        Assert.True(order.Number >= 10001);
        Assert.False(string.IsNullOrEmpty(order.AccessKey));
        Assert.Equal(OrderStatus.Ordered, order.Status);
        Assert.Equal(PaymentStatus.Pending, order.PaymentStatus);
        Assert.Equal(20000, order.Subtotal);
        Assert.Equal(0, order.Discount);
        Assert.Equal(0, order.DeliveryFee); // 20000 reaches the free-delivery threshold
        Assert.Equal(20000, order.Total);
        Assert.False(order.CustomerDiscountApplied);
        Assert.Equal("Anna Hakobyan", order.CustomerName);
        Assert.Equal("yerevan", order.Region);
        Assert.Equal(OrderFixture.FirstDeliveryDate, order.DeliveryDate);

        var line = Assert.Single(order.Lines);
        Assert.Equal(productId, line.ProductId);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(10000, line.UnitPrice);

        Assert.Equal(8, await _fx.GetStockAsync(productId));
        Assert.Equal(0, await _fx.CreateServices().Cart.CountAsync(owner));
    }

    [Fact]
    public async Task Place_BelowTheFreeDeliveryThreshold_AddsTheDeliveryFee()
    {
        var order = await _fx.PlaceOrderAsync(quantity: 1);

        Assert.Equal(10000, order.Subtotal);
        Assert.Equal(1500, order.DeliveryFee);
        Assert.Equal(11500, order.Total);
    }

    [Fact]
    public async Task Place_WithCard_StartsAwaitingPayment()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.Card);

        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
        Assert.Equal(PaymentStatus.Pending, order.PaymentStatus);
    }

    [Fact]
    public async Task Place_AnEmptyCart_IsRefused()
    {
        var owner = new CartOwner(null, Guid.NewGuid());
        var input = await _fx.InputAsync(owner, expectedTotal: 1500);

        var result = await _fx.CreateOrderService().PlaceAsync(owner, input, "en");

        Assert.Equal(PlaceOrderStatus.EmptyCart, result.Status);
    }

    [Fact]
    public async Task Place_WhenAProductWasArchived_ReportsUnavailableItems_AndKeepsTheStock()
    {
        var productId = await _fx.CreateProductAsync(stock: 5);
        var owner = await _fx.FillCartAsync(null, (productId, 1));
        var input = await _fx.InputAsync(owner);
        await _fx.ArchiveAsync(productId);

        var result = await _fx.CreateOrderService().PlaceAsync(owner, input, "en");

        Assert.Equal(PlaceOrderStatus.UnavailableItems, result.Status);
        Assert.Equal(productId, Assert.Single(result.Problems!).ProductId);
        Assert.Equal(5, await _fx.GetStockAsync(productId));
    }

    [Fact]
    public async Task Place_WhenThePriceChangedSinceTheCustomerSawIt_ReturnsTheNewTotal_AndDoesNotOrder()
    {
        var productId = await _fx.CreateProductAsync(stock: 5, price: 10000);
        var owner = await _fx.FillCartAsync(null, (productId, 1));
        var input = await _fx.InputAsync(owner, expectedTotal: 9999);

        var result = await _fx.CreateOrderService().PlaceAsync(owner, input, "en");

        Assert.Equal(PlaceOrderStatus.PriceChanged, result.Status);
        Assert.Equal(11500, result.NewTotal);
        Assert.Equal(5, await _fx.GetStockAsync(productId));
        Assert.Equal(1, await _fx.CreateServices().Cart.CountAsync(owner));
    }

    [Fact]
    public async Task Place_WhenTheStockDroppedAfterTheCartWasFilled_ShowsTheNewTotal_AndTakesNothing()
    {
        var productId = await _fx.CreateProductAsync(stock: 5);
        var owner = await _fx.FillCartAsync(null, (productId, 3));
        var input = await _fx.InputAsync(owner);
        await _fx.SetStockAsync(productId, 2); // somebody else bought it

        var result = await _fx.CreateOrderService().PlaceAsync(owner, input, "en");

        // The cart shrinks to what is left, so the total the customer saw is no longer right.
        Assert.Equal(PlaceOrderStatus.PriceChanged, result.Status);
        Assert.Equal(20000, result.NewTotal);
        Assert.Equal(2, await _fx.GetStockAsync(productId));
    }

    [Fact]
    public async Task Place_WhenOneOfTwoProductsIsShort_NothingIsTaken()
    {
        var plenty = await _fx.CreateProductAsync(stock: 10);
        var scarce = await _fx.CreateProductAsync(stock: 5);
        var owner = await _fx.FillCartAsync(null, (plenty, 2), (scarce, 3));
        var input = await _fx.InputAsync(owner);
        await _fx.SetStockAsync(scarce, 1);

        var result = await _fx.CreateOrderService().PlaceAsync(owner, input, "en");

        Assert.Equal(PlaceOrderStatus.PriceChanged, result.Status);
        Assert.Equal(10, await _fx.GetStockAsync(plenty));
        Assert.Equal(1, await _fx.GetStockAsync(scarce));
    }

    [Theory]
    [InlineData("2026-06-01")] // today
    [InlineData("2026-06-16")] // after the last day (14 days from June 2)
    public async Task Place_WithADeliveryDateOutsideTheAllowedDays_IsRefused(string date)
    {
        var productId = await _fx.CreateProductAsync();
        var owner = await _fx.FillCartAsync(null, (productId, 1));
        var input = await _fx.InputAsync(owner);
        input.DeliveryDate = DateOnly.Parse(date);

        var result = await _fx.CreateOrderService().PlaceAsync(owner, input, "en");

        Assert.Equal(PlaceOrderStatus.InvalidDelivery, result.Status);
    }

    [Fact]
    public async Task Place_WithAnUnknownRegionOrSlot_IsRefused()
    {
        var productId = await _fx.CreateProductAsync();
        var owner = await _fx.FillCartAsync(null, (productId, 1));

        var badRegion = await _fx.InputAsync(owner);
        badRegion.Region = "atlantis";
        Assert.Equal(PlaceOrderStatus.InvalidDelivery, (await _fx.CreateOrderService().PlaceAsync(owner, badRegion, "en")).Status);

        var badSlot = await _fx.InputAsync(owner);
        badSlot.DeliverySlotId = -1;
        Assert.Equal(PlaceOrderStatus.InvalidDelivery, (await _fx.CreateOrderService().PlaceAsync(owner, badSlot, "en")).Status);

        Assert.Equal(1, await _fx.CreateServices().Cart.CountAsync(owner));
    }

    [Fact]
    public async Task Place_LaterPriceChanges_DoNotChangeTheSavedOrder()
    {
        var productId = await _fx.CreateProductAsync(stock: 10, price: 10000);
        var owner = await _fx.FillCartAsync(null, (productId, 1));
        var order = (await _fx.CreateOrderService().PlaceAsync(owner, await _fx.InputAsync(owner), "en")).Order!;

        using (var db = _fx.CreateDbContext())
        {
            await db.Products.Where(p => p.Id == productId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 99999m));
        }

        var again = await _fx.CreateOrderService().FindByKeyAsync(order.Number, order.AccessKey);
        Assert.Equal(10000, Assert.Single(again!.Lines).UnitPrice);
        Assert.Equal(11500, again.Total);
    }

    // ---- The same checkout sent twice ----

    [Fact]
    public async Task Place_TheSameCheckoutTwice_GivesTheSameOrder_AndTakesTheStockOnce()
    {
        var productId = await _fx.CreateProductAsync(stock: 10);
        var owner = await _fx.FillCartAsync(null, (productId, 2));
        var input = await _fx.InputAsync(owner);

        var first = await _fx.CreateOrderService().PlaceAsync(owner, input, "en");
        var second = await _fx.CreateOrderService().PlaceAsync(owner, input, "en");

        Assert.Equal(PlaceOrderStatus.Placed, first.Status);
        Assert.Equal(PlaceOrderStatus.AlreadyPlaced, second.Status);
        Assert.Equal(first.Order!.Number, second.Order!.Number);
        Assert.Equal(8, await _fx.GetStockAsync(productId));
    }

    [Fact]
    public async Task Place_TheSameCheckoutAtTheSameMoment_CreatesOneOrder()
    {
        var productId = await _fx.CreateProductAsync(stock: 10);
        var owner = await _fx.FillCartAsync(null, (productId, 2));
        var input = await _fx.InputAsync(owner);

        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => _fx.CreateOrderService().PlaceAsync(owner, input, "en"))));

        Assert.Equal(1, results.Count(r => r.Status == PlaceOrderStatus.Placed));
        Assert.Equal(4, results.Count(r => r.Status == PlaceOrderStatus.AlreadyPlaced));
        Assert.Single(results.Select(r => r.Order!.Number).Distinct());
        Assert.Equal(8, await _fx.GetStockAsync(productId));
    }

    // ---- The last item ----

    [Fact]
    public async Task Place_TwoCustomersForTheLastItem_OnlyOneGetsIt()
    {
        var productId = await _fx.CreateProductAsync(stock: 1);
        var ownerA = await _fx.FillCartAsync(null, (productId, 1));
        var ownerB = await _fx.FillCartAsync(null, (productId, 1));
        var inputA = await _fx.InputAsync(ownerA);
        var inputB = await _fx.InputAsync(ownerB);

        var results = await Task.WhenAll(
            Task.Run(() => _fx.CreateOrderService().PlaceAsync(ownerA, inputA, "en")),
            Task.Run(() => _fx.CreateOrderService().PlaceAsync(ownerB, inputB, "en")));

        Assert.Equal(1, results.Count(r => r.Status == PlaceOrderStatus.Placed));
        Assert.Equal(1, results.Count(r => r.Status == PlaceOrderStatus.OutOfStock));
        Assert.Equal(0, await _fx.GetStockAsync(productId));
    }

    [Fact]
    public async Task Place_ManyCustomersForALimitedStock_NeverSellsMoreThanThereIs()
    {
        var productId = await _fx.CreateProductAsync(stock: 3);
        var carts = new List<(CartOwner Owner, CheckoutInput Input)>();
        for (var i = 0; i < 8; i++)
        {
            var owner = await _fx.FillCartAsync(null, (productId, 1));
            carts.Add((owner, await _fx.InputAsync(owner)));
        }

        var results = await Task.WhenAll(carts.Select(c =>
            Task.Run(() => _fx.CreateOrderService().PlaceAsync(c.Owner, c.Input, "en"))));

        // The others are refused in different ways, depending on when they read the cart (out of stock, unavailable, total changed).
        Assert.Equal(3, results.Count(r => r.Status == PlaceOrderStatus.Placed));
        Assert.Equal(5, results.Count(r => r.Status != PlaceOrderStatus.Placed && r.Order is null));
        Assert.Equal(0, await _fx.GetStockAsync(productId));
    }

    // ---- The 10% customer price on the first 5 orders ----

    [Fact]
    public async Task Place_ForASignedInCustomer_AppliesTheCustomerPrice()
    {
        var userId = await _fx.CreateUserAsync();

        var order = await _fx.PlaceOrderAsync(userId, quantity: 2);

        Assert.True(order.CustomerDiscountApplied);
        Assert.Equal(20000, order.Subtotal);
        Assert.Equal(2000, order.Discount);
        Assert.Equal(18000, order.Total - order.DeliveryFee);
        Assert.True(Assert.Single(order.Lines).CustomerDiscountApplied);
    }

    [Fact]
    public async Task Place_TheSixthOrder_HasNoCustomerPrice()
    {
        var userId = await _fx.CreateUserAsync();
        for (var i = 0; i < CustomerDiscountRules.MaxDiscountedOrders; i++)
        {
            Assert.True((await _fx.PlaceOrderAsync(userId)).CustomerDiscountApplied);
        }

        var sixth = await _fx.PlaceOrderAsync(userId);

        Assert.False(sixth.CustomerDiscountApplied);
        Assert.Equal(0, sixth.Discount);
    }

    [Fact]
    public async Task Place_ACancelledOrder_GivesTheDiscountBack()
    {
        var userId = await _fx.CreateUserAsync();
        var orders = new List<OrderSummary>();
        for (var i = 0; i < CustomerDiscountRules.MaxDiscountedOrders; i++)
        {
            orders.Add(await _fx.PlaceOrderAsync(userId));
        }

        await _fx.CreateOrderService().ChangeStatusAsync(orders[0].Number, OrderStatus.Cancelled, null, null);

        Assert.True((await _fx.PlaceOrderAsync(userId)).CustomerDiscountApplied);
    }

    [Fact]
    public async Task Place_TwoDifferentCheckoutsOfTheSameCartAtOnce_CreateOneOrder()
    {
        var userId = await _fx.CreateUserAsync();
        var productId = await _fx.CreateProductAsync(stock: 10);
        var owner = await _fx.FillCartAsync(userId, (productId, 1));
        var inputs = new[] { await _fx.InputAsync(owner), await _fx.InputAsync(owner) };

        var results = await Task.WhenAll(inputs.Select(input =>
            Task.Run(() => _fx.CreateOrderService().PlaceAsync(owner, input, "en"))));

        Assert.Equal(1, results.Count(r => r.Status == PlaceOrderStatus.Placed));
        Assert.Equal(1, results.Count(r => r.Status == PlaceOrderStatus.EmptyCart));
        Assert.Single(await _fx.CreateOrderService().ListForUserAsync(userId));
        Assert.Equal(9, await _fx.GetStockAsync(productId));
    }

    // ---- Opening orders ----

    [Fact]
    public async Task Find_ByTheSecretKey_WorksOnlyWithTheRightKey()
    {
        var order = await _fx.PlaceOrderAsync();
        var service = _fx.CreateOrderService();

        Assert.NotNull(await service.FindByKeyAsync(order.Number, order.AccessKey));
        Assert.Null(await service.FindByKeyAsync(order.Number, order.AccessKey + "x"));
        Assert.Null(await service.FindByKeyAsync(order.Number, order.AccessKey[..^1]));
        Assert.Null(await service.FindByKeyAsync(order.Number, string.Empty));
        Assert.Null(await service.FindByKeyAsync(order.Number + 100000, order.AccessKey));
    }

    [Fact]
    public async Task Find_ByEmail_NeedsTheNumberAndTheSameEmail()
    {
        var order = await _fx.PlaceOrderAsync();
        var service = _fx.CreateOrderService();

        Assert.NotNull(await service.FindByEmailAsync(order.Number, order.Email));
        Assert.NotNull(await service.FindByEmailAsync(order.Number, "  " + order.Email + " "));
        Assert.Null(await service.FindByEmailAsync(order.Number, "someone-else@example.com"));
    }

    [Fact]
    public async Task Find_ForUser_ShowsOnlyTheirOwnOrders()
    {
        var owner = await _fx.CreateUserAsync();
        var other = await _fx.CreateUserAsync();
        var order = await _fx.PlaceOrderAsync(owner);
        var service = _fx.CreateOrderService();

        Assert.NotNull(await service.FindForUserAsync(owner, order.Number));
        Assert.Null(await service.FindForUserAsync(other, order.Number));
    }

    [Fact]
    public async Task List_ForUser_IsNewestFirst_WithItemCounts()
    {
        var userId = await _fx.CreateUserAsync();
        var first = await _fx.PlaceOrderAsync(userId, quantity: 1);
        var second = await _fx.PlaceOrderAsync(userId, quantity: 3);

        var list = await _fx.CreateOrderService().ListForUserAsync(userId);

        Assert.Equal(2, list.Count);
        Assert.Contains(list, i => i.Number == first.Number && i.ItemCount == 1);
        Assert.Contains(list, i => i.Number == second.Number && i.ItemCount == 3);
        Assert.True(list[0].CreatedAt >= list[1].CreatedAt);
    }

    [Fact]
    public async Task LastAddress_IsTheAddressOfTheLatestOrder_OrNullWithoutOrders()
    {
        var userId = await _fx.CreateUserAsync();
        Assert.Null(await _fx.CreateOrderService().GetLastAddressAsync(userId));

        await _fx.PlaceOrderAsync(userId);

        var address = await _fx.CreateOrderService().GetLastAddressAsync(userId);
        Assert.Equal("Anna Hakobyan", address!.CustomerName);
        Assert.Equal("yerevan", address.Region);
        Assert.Equal("Abovyan", address.Street);
    }

    [Fact]
    public async Task OrderNumbers_AreUnique_AndGrow()
    {
        var a = await _fx.PlaceOrderAsync();
        var b = await _fx.PlaceOrderAsync();

        Assert.True(b.Number > a.Number);
    }

    // ---- Changing the status ----

    [Fact]
    public async Task ChangeStatus_FollowsThePath_AndWritesTheHistory()
    {
        var order = await _fx.PlaceOrderAsync();
        var service = _fx.CreateOrderService();

        Assert.Equal(ChangeStatusResult.Changed, await service.ChangeStatusAsync(order.Number, OrderStatus.Packed, null, null));
        Assert.Equal(ChangeStatusResult.Changed, await service.ChangeStatusAsync(order.Number, OrderStatus.Delivered, null, "Handed over"));

        using var db = _fx.CreateDbContext();
        var saved = await db.Orders.Include(o => o.Events).FirstAsync(o => o.Number == order.Number);
        Assert.Equal(OrderStatus.Delivered, saved.Status);
        Assert.NotNull(saved.DeliveredAt);
        Assert.Equal(
            [OrderStatus.Ordered, OrderStatus.Packed, OrderStatus.Delivered],
            saved.Events.OrderBy(e => e.Id).Select(e => e.Status));
    }

    [Fact]
    public async Task ChangeStatus_SkippingAStep_OrChangingAFinalOrder_IsNotAllowed()
    {
        var order = await _fx.PlaceOrderAsync();
        var service = _fx.CreateOrderService();

        Assert.Equal(ChangeStatusResult.NotAllowed, await service.ChangeStatusAsync(order.Number, OrderStatus.Delivered, null, null));

        await service.ChangeStatusAsync(order.Number, OrderStatus.Cancelled, null, null);
        Assert.Equal(ChangeStatusResult.NotAllowed, await service.ChangeStatusAsync(order.Number, OrderStatus.Ordered, null, null));
    }

    [Fact]
    public async Task ChangeStatus_OfAMissingOrder_IsNotFound()
    {
        Assert.Equal(ChangeStatusResult.NotFound, await _fx.CreateOrderService().ChangeStatusAsync(1, OrderStatus.Packed, null, null));
    }

    [Fact]
    public async Task Deliver_ACashOrder_MarksItPaid()
    {
        var order = await _fx.PlaceOrderAsync(method: PaymentMethod.CashOnDelivery);
        var service = _fx.CreateOrderService();

        await service.ChangeStatusAsync(order.Number, OrderStatus.Packed, null, null);
        await service.ChangeStatusAsync(order.Number, OrderStatus.Delivered, null, null);

        var saved = await service.FindByKeyAsync(order.Number, order.AccessKey);
        Assert.Equal(PaymentStatus.Paid, saved!.PaymentStatus);
    }

    [Fact]
    public async Task Cancel_PutsTheStockBack()
    {
        var productId = await _fx.CreateProductAsync(stock: 10);
        var owner = await _fx.FillCartAsync(null, (productId, 4));
        var order = (await _fx.CreateOrderService().PlaceAsync(owner, await _fx.InputAsync(owner), "en")).Order!;
        Assert.Equal(6, await _fx.GetStockAsync(productId));

        await _fx.CreateOrderService().ChangeStatusAsync(order.Number, OrderStatus.Cancelled, null, "Customer asked");

        Assert.Equal(10, await _fx.GetStockAsync(productId));
    }

    [Fact]
    public async Task Cancel_TwoAtTheSameMoment_PutsTheStockBackOnce()
    {
        var productId = await _fx.CreateProductAsync(stock: 10);
        var owner = await _fx.FillCartAsync(null, (productId, 4));
        var order = (await _fx.CreateOrderService().PlaceAsync(owner, await _fx.InputAsync(owner), "en")).Order!;

        var results = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => Task.Run(() => _fx.CreateOrderService().ChangeStatusAsync(order.Number, OrderStatus.Cancelled, null, null))));

        Assert.Equal(1, results.Count(r => r == ChangeStatusResult.Changed));
        Assert.Equal(2, results.Count(r => r == ChangeStatusResult.NotAllowed));
        Assert.Equal(10, await _fx.GetStockAsync(productId));
    }

    // ---- Returns ----

    private async Task<OrderSummary> DeliveredOrderAsync()
    {
        var order = await _fx.PlaceOrderAsync();
        var service = _fx.CreateOrderService();
        await service.ChangeStatusAsync(order.Number, OrderStatus.Packed, null, null);
        await service.ChangeStatusAsync(order.Number, OrderStatus.Delivered, null, null);
        return order;
    }

    [Fact]
    public async Task Return_ForADeliveredOrder_IsCreated_AndShownOnTheOrder()
    {
        var order = await DeliveredOrderAsync();
        var service = _fx.CreateOrderService();

        Assert.True((await service.FindByKeyAsync(order.Number, order.AccessKey))!.CanRequestReturn);

        var result = await service.RequestReturnAsync(order.Number, "  The toy is broken  ");

        Assert.Equal(ReturnRequestResult.Created, result);
        var saved = await service.FindByKeyAsync(order.Number, order.AccessKey);
        Assert.Equal(ReturnStatus.Requested, saved!.Return!.Status);
        Assert.Equal("The toy is broken", saved.Return.Reason);
        Assert.False(saved.CanRequestReturn);
    }

    [Fact]
    public async Task Return_Twice_IsRefused()
    {
        var order = await DeliveredOrderAsync();
        var service = _fx.CreateOrderService();
        await service.RequestReturnAsync(order.Number, "The toy is broken");

        Assert.Equal(ReturnRequestResult.AlreadyRequested, await service.RequestReturnAsync(order.Number, "Another reason"));
    }

    [Fact]
    public async Task Return_TwoAtTheSameMoment_CreatesOneRequest()
    {
        var order = await DeliveredOrderAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => Task.Run(() => _fx.CreateOrderService().RequestReturnAsync(order.Number, "The toy is broken"))));

        Assert.Equal(1, results.Count(r => r == ReturnRequestResult.Created));
        Assert.Equal(2, results.Count(r => r == ReturnRequestResult.AlreadyRequested));
    }

    [Fact]
    public async Task Return_BeforeDelivery_IsRefused()
    {
        var order = await _fx.PlaceOrderAsync();

        Assert.Equal(ReturnRequestResult.NotDelivered, await _fx.CreateOrderService().RequestReturnAsync(order.Number, "Changed my mind"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public async Task Return_WithATooShortReason_IsRefused(string reason)
    {
        var order = await DeliveredOrderAsync();

        Assert.Equal(ReturnRequestResult.InvalidReason, await _fx.CreateOrderService().RequestReturnAsync(order.Number, reason));
    }

    [Fact]
    public async Task Return_OfAMissingOrder_IsNotFound()
    {
        Assert.Equal(ReturnRequestResult.NotFound, await _fx.CreateOrderService().RequestReturnAsync(1, "Some reason here"));
    }

    [Fact]
    public async Task Return_AfterTheReturnWindow_IsRefused()
    {
        var order = await DeliveredOrderAsync(); // delivered at OrderFixture.Now; the window is 14 days
        var later = OrderFixture.Now.AddDays(15);
        var service = _fx.CreateOrderService(later);

        Assert.False((await service.FindByKeyAsync(order.Number, order.AccessKey))!.CanRequestReturn);
        Assert.Equal(ReturnRequestResult.WindowClosed, await service.RequestReturnAsync(order.Number, "Too late maybe"));
    }

    [Fact]
    public async Task Return_OnTheLastDayOfTheWindow_IsStillAccepted()
    {
        var order = await DeliveredOrderAsync();
        var service = _fx.CreateOrderService(OrderFixture.Now.AddDays(14));

        Assert.Equal(ReturnRequestResult.Created, await service.RequestReturnAsync(order.Number, "Just in time"));
    }

    [Fact]
    public async Task Return_AfterARejectedRequest_CanBeFiledAgain()
    {
        var order = await DeliveredOrderAsync();
        var service = _fx.CreateOrderService();
        await service.RequestReturnAsync(order.Number, "First reason");

        using (var db = _fx.CreateDbContext())
        {
            await db.ReturnRequests.Where(r => r.Order.Number == order.Number)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReturnStatus.Rejected));
        }

        Assert.Equal(ReturnRequestResult.Created, await _fx.CreateOrderService().RequestReturnAsync(order.Number, "Second reason"));
    }
}

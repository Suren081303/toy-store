using Microsoft.EntityFrameworkCore;
using Shop.Core.Carts;
using Shop.Core.Entities;

namespace Shop.Tests;

[Collection("Cart")]
public class CartServiceTests
{
    private static readonly CartOwner NoCartYet = new(null, null);

    private readonly CartFixture _fx;

    public CartServiceTests(CartFixture fx)
    {
        _fx = fx;
    }

    private static CartOwner Guest(Guid cartId) => new(null, cartId);

    private static CartOwner Customer(string userId) => new(userId, null);

    // ---- Adding ----

    [Fact]
    public async Task Add_ToAGuestWithoutACart_CreatesTheCart_AndReturnsItsId()
    {
        var productId = await _fx.CreateProductAsync();

        var result = await _fx.CreateService().AddAsync(NoCartYet, productId, 1);

        Assert.Equal(CartChangeStatus.Added, result.Status);
        Assert.Equal(1, result.Quantity);
        Assert.Equal(1, result.Count);
        Assert.NotNull(result.NewGuestCartId);

        var cart = await _fx.CreateService().GetAsync(Guest(result.NewGuestCartId.Value), "en");
        Assert.Equal(productId, Assert.Single(cart.Lines).ProductId);
    }

    [Fact]
    public async Task Add_TheSameProductAgain_IncreasesTheQuantity()
    {
        var productId = await _fx.CreateProductAsync(stock: 10);
        var first = await _fx.CreateService().AddAsync(NoCartYet, productId, 2);
        var owner = Guest(first.NewGuestCartId!.Value);

        var second = await _fx.CreateService().AddAsync(owner, productId, 3);

        Assert.Equal(CartChangeStatus.Added, second.Status);
        Assert.Equal(5, second.Quantity);
        Assert.Null(second.NewGuestCartId);
        Assert.Equal(5, await _fx.CreateService().CountAsync(owner));
    }

    [Fact]
    public async Task Add_MoreThanTheStock_HoldsBackTheQuantity()
    {
        var productId = await _fx.CreateProductAsync(stock: 2);
        var owner = Guest((await _fx.CreateService().AddAsync(NoCartYet, productId, 2)).NewGuestCartId!.Value);

        var result = await _fx.CreateService().AddAsync(owner, productId, 1);

        Assert.Equal(CartChangeStatus.LimitReached, result.Status);
        Assert.Equal(2, result.Quantity);
        Assert.Equal(2, result.Limit);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Add_MoreThanThePerProductLimit_HoldsBackTheQuantity()
    {
        var productId = await _fx.CreateProductAsync(stock: 8);

        var result = await _fx.CreateService(maxQuantity: 3).AddAsync(NoCartYet, productId, 5);

        Assert.Equal(CartChangeStatus.LimitReached, result.Status);
        Assert.Equal(3, result.Quantity);
        Assert.Equal(3, result.Limit);
    }

    [Fact]
    public async Task Add_ProductsThatCannotBeBought_IsRefused_AndCreatesNoCart()
    {
        var outOfStock = await _fx.CreateProductAsync(stock: 0);
        var archived = await _fx.CreateProductAsync(archived: true);
        var hiddenCategory = await _fx.CreateProductAsync(inactiveCategory: true);

        foreach (var productId in new[] { outOfStock, archived, hiddenCategory, int.MaxValue })
        {
            var result = await _fx.CreateService().AddAsync(NoCartYet, productId, 1);

            Assert.Equal(CartChangeStatus.NotAvailable, result.Status);
            Assert.Null(result.NewGuestCartId);
        }
    }

    [Fact]
    public async Task Add_ForASignedInCustomer_UsesOneCartPerUser()
    {
        var userId = await _fx.CreateUserAsync();
        var first = await _fx.CreateProductAsync();
        var second = await _fx.CreateProductAsync();

        var one = await _fx.CreateService().AddAsync(Customer(userId), first, 1);
        var two = await _fx.CreateService().AddAsync(Customer(userId), second, 1);

        Assert.Null(one.NewGuestCartId);
        Assert.Null(two.NewGuestCartId);
        Assert.Equal(2, two.Count);

        using var db = _fx.CreateDbContext();
        Assert.Equal(1, await db.Carts.CountAsync(c => c.UserId == userId));
    }

    [Fact]
    public async Task Add_WhenTheCartHasTooManyLines_IsRefused()
    {
        var owner = NoCartYet;
        var service = _fx.CreateService(maxLines: 2);
        var first = await service.AddAsync(owner, await _fx.CreateProductAsync(), 1);
        owner = Guest(first.NewGuestCartId!.Value);
        await _fx.CreateService(maxLines: 2).AddAsync(owner, await _fx.CreateProductAsync(), 1);

        var third = await _fx.CreateService(maxLines: 2).AddAsync(owner, await _fx.CreateProductAsync(), 1);

        Assert.Equal(CartChangeStatus.TooManyLines, third.Status);
        Assert.Equal(2, await _fx.CreateService().CountAsync(owner));
    }

    // ---- Changing and removing ----

    [Fact]
    public async Task SetQuantity_ChangesIt_AndHoldsItBackAtTheStock()
    {
        var productId = await _fx.CreateProductAsync(stock: 4);
        var owner = Guest((await _fx.CreateService().AddAsync(NoCartYet, productId, 1)).NewGuestCartId!.Value);

        var changed = await _fx.CreateService().SetQuantityAsync(owner, productId, 3);
        Assert.Equal(CartChangeStatus.Added, changed.Status);
        Assert.Equal(3, changed.Quantity);

        var tooMany = await _fx.CreateService().SetQuantityAsync(owner, productId, 9);
        Assert.Equal(CartChangeStatus.LimitReached, tooMany.Status);
        Assert.Equal(4, tooMany.Quantity);
    }

    [Fact]
    public async Task SetQuantity_ToZero_RemovesTheLine()
    {
        var productId = await _fx.CreateProductAsync();
        var owner = Guest((await _fx.CreateService().AddAsync(NoCartYet, productId, 2)).NewGuestCartId!.Value);

        var result = await _fx.CreateService().SetQuantityAsync(owner, productId, 0);

        Assert.Equal(0, result.Count);
        Assert.Empty((await _fx.CreateService().GetAsync(owner, "en")).Lines);
    }

    [Fact]
    public async Task SetQuantity_ForAProductNotInTheCart_ReportsNotInCart()
    {
        var inCart = await _fx.CreateProductAsync();
        var other = await _fx.CreateProductAsync();
        var owner = Guest((await _fx.CreateService().AddAsync(NoCartYet, inCart, 1)).NewGuestCartId!.Value);

        var result = await _fx.CreateService().SetQuantityAsync(owner, other, 2);

        Assert.Equal(CartChangeStatus.NotInCart, result.Status);
    }

    [Fact]
    public async Task AProductThatCanNoLongerBeBought_CanOnlyBeRemoved()
    {
        var productId = await _fx.CreateProductAsync(stock: 5);
        var owner = Guest((await _fx.CreateService().AddAsync(NoCartYet, productId, 2)).NewGuestCartId!.Value);
        await _fx.SetProductAsync(productId, archived: true);

        var change = await _fx.CreateService().SetQuantityAsync(owner, productId, 4);
        Assert.Equal(CartChangeStatus.NotAvailable, change.Status);

        await _fx.CreateService().RemoveAsync(owner, productId);
        Assert.Empty((await _fx.CreateService().GetAsync(owner, "en")).Lines);
    }

    // ---- Viewing ----

    [Fact]
    public async Task Get_AGuest_SeesProductPrices_ASignedInCustomer_GetsTheCustomerPriceWhenLower()
    {
        var plain = await _fx.CreateProductAsync(price: 10000, discountPercent: 0);
        var discounted = await _fx.CreateProductAsync(price: 10000, discountPercent: 15);
        var userId = await _fx.CreateUserAsync();

        var guestOwner = Guest((await _fx.CreateService().AddAsync(NoCartYet, plain, 2)).NewGuestCartId!.Value);
        await _fx.CreateService().AddAsync(guestOwner, discounted, 1);
        var guestCart = await _fx.CreateService().GetAsync(guestOwner, "en");

        Assert.False(guestCart.CustomerDiscountActive);
        Assert.Equal(10000m, guestCart.Lines.Single(l => l.ProductId == plain).UnitPrice);
        Assert.Equal(8500m, guestCart.Lines.Single(l => l.ProductId == discounted).UnitPrice);
        Assert.Equal(28500m, guestCart.Total);

        await _fx.CreateService().AddAsync(Customer(userId), plain, 2);
        await _fx.CreateService().AddAsync(Customer(userId), discounted, 1);
        var customerCart = await _fx.CreateService().GetAsync(Customer(userId), "en");

        Assert.True(customerCart.CustomerDiscountActive);
        var plainLine = customerCart.Lines.Single(l => l.ProductId == plain);
        Assert.Equal(9000m, plainLine.UnitPrice);
        Assert.True(plainLine.CustomerDiscountApplied);
        var discountedLine = customerCart.Lines.Single(l => l.ProductId == discounted);
        Assert.Equal(8500m, discountedLine.UnitPrice);
        Assert.False(discountedLine.CustomerDiscountApplied);
        Assert.Equal(26500m, customerCart.Total);
        Assert.Equal(30000m, customerCart.RegularTotal);
        Assert.Equal(3500m, customerCart.Savings);
        Assert.Equal(2, customerCart.CustomerDiscountItemCount);
    }

    [Fact]
    public async Task Get_WhenTheStockDroppedBelowTheQuantity_ReducesItOnce_AndReportsIt()
    {
        var productId = await _fx.CreateProductAsync(stock: 6);
        var owner = Guest((await _fx.CreateService().AddAsync(NoCartYet, productId, 5)).NewGuestCartId!.Value);
        await _fx.SetProductAsync(productId, stock: 2);

        var first = await _fx.CreateService().GetAsync(owner, "en");

        var notice = Assert.Single(first.Notices);
        Assert.Equal(CartNoticeKind.QuantityReduced, notice.Kind);
        Assert.Equal(2, notice.Quantity);
        Assert.Equal(2, Assert.Single(first.Lines).Quantity);

        var second = await _fx.CreateService().GetAsync(owner, "en");
        Assert.Empty(second.Notices);
        Assert.Equal(2, Assert.Single(second.Lines).Quantity);
    }

    [Fact]
    public async Task Get_FlagsProductsThatCannotBeBought_AndLeavesThemOutOfTheTotals()
    {
        var fine = await _fx.CreateProductAsync(price: 5000, stock: 5);
        var soldOut = await _fx.CreateProductAsync(price: 7000, stock: 5);
        var archived = await _fx.CreateProductAsync(price: 8000, stock: 5);

        var owner = Guest((await _fx.CreateService().AddAsync(NoCartYet, fine, 1)).NewGuestCartId!.Value);
        await _fx.CreateService().AddAsync(owner, soldOut, 2);
        await _fx.CreateService().AddAsync(owner, archived, 1);
        await _fx.SetProductAsync(soldOut, stock: 0);
        await _fx.SetProductAsync(archived, archived: true);

        var cart = await _fx.CreateService().GetAsync(owner, "en");

        Assert.Equal(CartLineStatus.Available, cart.Lines.Single(l => l.ProductId == fine).Status);
        Assert.Equal(CartLineStatus.OutOfStock, cart.Lines.Single(l => l.ProductId == soldOut).Status);
        Assert.Equal(CartLineStatus.NotForSale, cart.Lines.Single(l => l.ProductId == archived).Status);
        Assert.True(cart.HasUnavailableLines);
        Assert.Equal(5000m, cart.Total);
        Assert.Equal(1, cart.ItemCount);
        Assert.Equal(1, await _fx.CreateService().CountAsync(owner));
        // They stay in the cart (not silently removed), with their quantity.
        Assert.Equal(2, cart.Lines.Single(l => l.ProductId == soldOut).Quantity);
    }

    [Fact]
    public async Task Get_UsesTheRequestedLanguage_WithArmenianFallback()
    {
        var productId = await _fx.CreateProductAsync();
        var owner = Guest((await _fx.CreateService().AddAsync(NoCartYet, productId, 1)).NewGuestCartId!.Value);

        var english = (await _fx.CreateService().GetAsync(owner, "en")).Lines.Single().Name;
        var russian = (await _fx.CreateService().GetAsync(owner, "ru")).Lines.Single().Name;

        Assert.StartsWith("Toy ", english);
        Assert.StartsWith("Խաղ ", russian);
    }

    [Fact]
    public async Task ACartIdInACookie_CanNeverOpenACustomersCart()
    {
        var userId = await _fx.CreateUserAsync();
        await _fx.CreateService().AddAsync(Customer(userId), await _fx.CreateProductAsync(), 1);

        using var db = _fx.CreateDbContext();
        var customerCartId = (await db.Carts.FirstAsync(c => c.UserId == userId)).Id;

        var asGuest = Guest(customerCartId);
        Assert.Empty((await _fx.CreateService().GetAsync(asGuest, "en")).Lines);
        Assert.Equal(0, await _fx.CreateService().CountAsync(asGuest));
        Assert.NotNull((await _fx.CreateService().AddAsync(asGuest, await _fx.CreateProductAsync(), 1)).NewGuestCartId);
    }

    [Fact]
    public async Task Get_ForAnOwnerWithoutACart_IsEmpty()
    {
        var cart = await _fx.CreateService().GetAsync(Guest(Guid.NewGuid()), "en");

        Assert.Empty(cart.Lines);
        Assert.Equal(0, await _fx.CreateService().CountAsync(NoCartYet));
    }

    // ---- Merging when a guest signs in ----

    [Fact]
    public async Task Merge_AddsTheQuantitiesTogether_CapsThemAtTheStock_AndDeletesTheGuestCart()
    {
        var userId = await _fx.CreateUserAsync();
        var shared = await _fx.CreateProductAsync(stock: 4);
        var onlyGuest = await _fx.CreateProductAsync(stock: 10);
        var onlyUser = await _fx.CreateProductAsync(stock: 10);
        var gone = await _fx.CreateProductAsync(stock: 10);

        await _fx.CreateService().AddAsync(Customer(userId), shared, 3);
        await _fx.CreateService().AddAsync(Customer(userId), onlyUser, 1);

        var guestId = (await _fx.CreateService().AddAsync(NoCartYet, shared, 2)).NewGuestCartId!.Value;
        await _fx.CreateService().AddAsync(Guest(guestId), onlyGuest, 2);
        await _fx.CreateService().AddAsync(Guest(guestId), gone, 1);
        await _fx.SetProductAsync(gone, archived: true);

        await _fx.CreateService().MergeGuestCartAsync(guestId, userId);

        var cart = await _fx.CreateService().GetAsync(Customer(userId), "en");
        Assert.Equal(4, cart.Lines.Single(l => l.ProductId == shared).Quantity);      // 3 + 2, capped by stock 4
        Assert.Equal(2, cart.Lines.Single(l => l.ProductId == onlyGuest).Quantity);
        Assert.Equal(1, cart.Lines.Single(l => l.ProductId == onlyUser).Quantity);
        Assert.DoesNotContain(cart.Lines, l => l.ProductId == gone);                  // not for sale: dropped

        using var db = _fx.CreateDbContext();
        Assert.False(await db.Carts.AnyAsync(c => c.Id == guestId));
        Assert.Equal(0, await db.CartItems.CountAsync(i => i.CartId == guestId));
    }

    [Fact]
    public async Task Merge_IntoACustomerWithoutACart_CreatesOne()
    {
        var userId = await _fx.CreateUserAsync();
        var productId = await _fx.CreateProductAsync();
        var guestId = (await _fx.CreateService().AddAsync(NoCartYet, productId, 2)).NewGuestCartId!.Value;

        await _fx.CreateService().MergeGuestCartAsync(guestId, userId);

        var cart = await _fx.CreateService().GetAsync(Customer(userId), "en");
        Assert.Equal(2, Assert.Single(cart.Lines).Quantity);
    }

    [Fact]
    public async Task Merge_WithAnUnknownOrANonGuestCartId_ChangesNothing()
    {
        var userId = await _fx.CreateUserAsync();
        var otherUserId = await _fx.CreateUserAsync();
        await _fx.CreateService().AddAsync(Customer(otherUserId), await _fx.CreateProductAsync(), 1);

        using var db = _fx.CreateDbContext();
        var othersCartId = (await db.Carts.FirstAsync(c => c.UserId == otherUserId)).Id;

        await _fx.CreateService().MergeGuestCartAsync(Guid.NewGuid(), userId);
        await _fx.CreateService().MergeGuestCartAsync(othersCartId, userId);

        Assert.Empty((await _fx.CreateService().GetAsync(Customer(userId), "en")).Lines);
        Assert.Equal(1, await _fx.CreateService().CountAsync(Customer(otherUserId)));
    }

    // ---- Clean-up ----

    [Fact]
    public async Task DeleteOldGuestCarts_RemovesOnlyOldGuestCarts()
    {
        var userId = await _fx.CreateUserAsync();
        var productId = await _fx.CreateProductAsync();

        var oldGuest = (await _fx.CreateService().AddAsync(NoCartYet, productId, 1)).NewGuestCartId!.Value;
        var freshGuest = (await _fx.CreateService().AddAsync(NoCartYet, productId, 1)).NewGuestCartId!.Value;
        await _fx.CreateService().AddAsync(Customer(userId), productId, 1);

        using (var db = _fx.CreateDbContext())
        {
            // Make the first guest cart and the customer's cart look old.
            await db.Carts.Where(c => c.Id == oldGuest || c.UserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, CartFixture.Now.AddDays(-60)));
        }

        var deleted = await _fx.CreateService().DeleteOldGuestCartsAsync(CartFixture.Now.AddDays(-30));

        Assert.True(deleted >= 1);
        using var check = _fx.CreateDbContext();
        Assert.False(await check.Carts.AnyAsync(c => c.Id == oldGuest));
        Assert.True(await check.Carts.AnyAsync(c => c.Id == freshGuest));
        Assert.True(await check.Carts.AnyAsync(c => c.UserId == userId));
        Assert.Equal(0, await check.CartItems.CountAsync(i => i.CartId == oldGuest));
    }
}

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Carts;
using Shop.Core.Entities;
using Shop.Core.Notifications;
using Shop.Core.Orders;
using Shop.Core.Payments;

namespace Shop.Tests;

// The customer list, block, personal discount (and what it does to prices and orders), and messages, on the real database.
[Collection("Orders")]
public class AdminCustomerServiceTests
{
    private readonly OrderFixture _fx;

    public AdminCustomerServiceTests(OrderFixture fx)
    {
        _fx = fx;
    }

    private static string Token() => Guid.NewGuid().ToString("N")[..10];

    // A customer whose name, email and phone all hold the token, so a search for the token finds only this test's customers.
    private async Task<(string Id, string Email, string Name)> NewCustomerAsync(string token, string suffix = "", bool confirmed = true, string phone = "091 123456", DateTime? createdAt = null)
    {
        var id = await _fx.CreateUserAsync();
        using var db = _fx.CreateDbContext();
        var user = await db.Users.SingleAsync(u => u.Id == id);
        user.FullName = $"Cust {token} {suffix}".Trim();
        user.Email = $"{token}-{Guid.NewGuid():N}@example.com";
        user.EmailConfirmed = confirmed;
        user.PhoneNumber = phone;
        user.CreatedAt = createdAt ?? OrderFixture.Now;
        await db.SaveChangesAsync();
        return (id, user.Email, user.FullName);
    }

    private async Task<string> NewAdminAsync()
    {
        var id = await _fx.CreateUserAsync();
        using var db = _fx.CreateDbContext();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == "Admin");
        if (role is null)
        {
            role = new IdentityRole("Admin") { NormalizedName = "ADMIN" };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        db.UserRoles.Add(new IdentityUserRole<string> { UserId = id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<AdminCustomerRow> RowAsync(string token, string id, AdminCustomerFilter? filter = null)
    {
        filter ??= new AdminCustomerFilter();
        filter.Query = token;
        var result = await _fx.CreateCustomerService().ListAsync(filter);
        return result.Items.Single(r => r.Id == id);
    }

    private async Task<ApplicationUser> UserAsync(string id)
    {
        using var db = _fx.CreateDbContext();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
    }

    private async Task<List<AuditLog>> AuditAsync(string action, string entityId)
    {
        using var db = _fx.CreateDbContext();
        return await db.AuditLogs.AsNoTracking().Where(a => a.Action == action && a.EntityId == entityId).ToListAsync();
    }

    private async Task SetOrderTimeAsync(int number, DateTime createdAt)
    {
        using var db = _fx.CreateDbContext();
        await db.Orders.Where(o => o.Number == number).ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedAt, createdAt));
    }

    // ---- The list ----

    [Fact]
    public async Task List_ShowsCustomers_NotAdmins_AndFindsByNameEmailAndPhone()
    {
        var token = Token();
        var (customerId, email, name) = await NewCustomerAsync(token, "Anna", phone: "093 777" + new Random().Next(100, 999));
        var adminId = await NewAdminAsync();
        using (var db = _fx.CreateDbContext())
        {
            await db.Users.Where(u => u.Id == adminId).ExecuteUpdateAsync(s => s.SetProperty(u => u.FullName, $"Cust {token} Admin"));
        }

        var service = _fx.CreateCustomerService();
        var byToken = await service.ListAsync(new AdminCustomerFilter { Query = token });
        Assert.Contains(byToken.Items, r => r.Id == customerId);
        Assert.DoesNotContain(byToken.Items, r => r.Id == adminId); // an admin is not a customer

        var row = byToken.Items.Single(r => r.Id == customerId);
        Assert.Equal(name, row.FullName);
        Assert.Equal(email, row.Email);

        Assert.Contains((await service.ListAsync(new AdminCustomerFilter { Query = email })).Items, r => r.Id == customerId);
        Assert.Contains((await service.ListAsync(new AdminCustomerFilter { Query = row.Phone })).Items, r => r.Id == customerId);
        Assert.Empty((await service.ListAsync(new AdminCustomerFilter { Query = "no-such-" + token })).Items);
    }

    [Fact]
    public async Task List_FiltersActiveAndBlocked_AndPagesByTwentyFive()
    {
        var token = Token();
        var many = new List<string>();
        for (var i = 0; i < 27; i++)
        {
            many.Add((await NewCustomerAsync(token, i.ToString("D2"))).Id);
        }

        var admin = await NewAdminAsync();
        await _fx.CreateCustomerService().BlockAsync(many[0], admin);

        var service = _fx.CreateCustomerService();
        var all = await service.ListAsync(new AdminCustomerFilter { Query = token, Sort = CustomerSort.Name });
        Assert.Equal(27, all.TotalCount);
        Assert.Equal(25, all.PageSize);
        Assert.Equal(2, all.TotalPages);
        Assert.Equal(25, all.Items.Count);
        Assert.Equal(2, (await service.ListAsync(new AdminCustomerFilter { Query = token, Sort = CustomerSort.Name, Page = 2 })).Items.Count);

        var blocked = await service.ListAsync(new AdminCustomerFilter { Query = token, Status = CustomerStatusFilter.Blocked });
        Assert.Equal(many[0], Assert.Single(blocked.Items).Id);
        Assert.True(blocked.Items[0].IsBlocked);
        Assert.Equal(26, (await service.ListAsync(new AdminCustomerFilter { Query = token, Status = CustomerStatusFilter.Active })).TotalCount);
    }

    [Fact]
    public async Task List_CountsConfirmedOrders_SubtractsRefunds_AndShowsTheLastConfirmedOrder()
    {
        var token = Token();
        var (id, _, _) = await NewCustomerAsync(token);
        var adminId = await NewAdminAsync();

        var delivered = await _fx.PlaceOrderAsync(id);                               // will be delivered, then refunded
        var ordered = await _fx.PlaceOrderAsync(id);                                 // stays "Ordered"
        var cancelled = await _fx.PlaceOrderAsync(id);                               // cancelled: does not count
        var waiting = await _fx.PlaceOrderAsync(id, PaymentMethod.BankTransfer);     // waiting for payment: does not count

        var orders = _fx.CreateOrderService();
        await orders.ChangeStatusAsync(delivered.Number, OrderStatus.Packed, null, null);
        await orders.ChangeStatusAsync(delivered.Number, OrderStatus.Delivered, null, null);
        await orders.ChangeStatusAsync(cancelled.Number, OrderStatus.Cancelled, null, null);

        await SetOrderTimeAsync(delivered.Number, OrderFixture.Now.AddDays(-3));
        await SetOrderTimeAsync(ordered.Number, OrderFixture.Now.AddDays(-1));
        await SetOrderTimeAsync(cancelled.Number, OrderFixture.Now.AddDays(-0.5));
        await SetOrderTimeAsync(waiting.Number, OrderFixture.Now.AddDays(5));

        var row = await RowAsync(token, id);
        Assert.Equal(2, row.Orders);
        Assert.Equal(delivered.Total + ordered.Total, row.TotalSpent);
        Assert.Equal(OrderFixture.Now.AddDays(-1), row.LastOrderAt);

        // Money sent back for a return is taken off.
        Assert.Equal(ReturnRequestResult.Created, await orders.RequestReturnAsync(delivered.Number, "Broken"));
        int returnId;
        using (var db = _fx.CreateDbContext())
        {
            returnId = await db.ReturnRequests.Where(r => r.Order.Number == delivered.Number).Select(r => r.Id).SingleAsync();
        }

        var returns = _fx.CreateAdminServices().Returns;
        Assert.Equal(ChangeReturnResult.Changed, await returns.ChangeStatusAsync(returnId, ReturnStatus.Returned, null, false, adminId));
        Assert.Equal(delivered.Total + ordered.Total, (await RowAsync(token, id)).TotalSpent); // goods back, money not yet
        Assert.Equal(ChangeReturnResult.Changed, await returns.ChangeStatusAsync(returnId, ReturnStatus.Refunded, null, false, adminId));

        var after = await RowAsync(token, id);
        Assert.Equal(ordered.Total, after.TotalSpent);
        Assert.Equal(2, after.Orders); // the order still counts, only the money is taken off
    }

    [Fact]
    public async Task List_ACustomerWithoutOrders_HasZeroes_AndNoLastOrder()
    {
        var token = Token();
        var (id, _, _) = await NewCustomerAsync(token);

        var row = await RowAsync(token, id);

        Assert.Equal(0, row.Orders);
        Assert.Equal(0m, row.TotalSpent);
        Assert.Null(row.LastOrderAt);
        Assert.False(row.IsBlocked);
        Assert.Null(row.PersonalDiscountPercent);
        Assert.Empty(row.Messages);
    }

    [Fact]
    public async Task List_Sorts_ByNewest_Name_Orders_Spent_AndLastOrder()
    {
        var token = Token();
        var a = await NewCustomerAsync(token, "C-third", createdAt: OrderFixture.Now.AddDays(-10));  // 2 orders, the oldest account
        var b = await NewCustomerAsync(token, "A-first", createdAt: OrderFixture.Now.AddDays(-5));   // 1 order
        var c = await NewCustomerAsync(token, "B-second", createdAt: OrderFixture.Now.AddDays(-1));  // none

        var oa1 = await _fx.PlaceOrderAsync(a.Id);
        var oa2 = await _fx.PlaceOrderAsync(a.Id);
        var ob = await _fx.PlaceOrderAsync(b.Id);
        await SetOrderTimeAsync(oa1.Number, OrderFixture.Now.AddDays(-4));
        await SetOrderTimeAsync(oa2.Number, OrderFixture.Now.AddDays(-3));
        await SetOrderTimeAsync(ob.Number, OrderFixture.Now.AddDays(-2));

        async Task<string[]> Ids(CustomerSort sort) =>
            (await _fx.CreateCustomerService().ListAsync(new AdminCustomerFilter { Query = token, Sort = sort })).Items.Select(r => r.Id).ToArray();

        Assert.Equal([c.Id, b.Id, a.Id], await Ids(CustomerSort.Newest));
        Assert.Equal([b.Id, c.Id, a.Id], await Ids(CustomerSort.Name));
        Assert.Equal([a.Id, b.Id, c.Id], await Ids(CustomerSort.Orders));
        Assert.Equal([a.Id, b.Id, c.Id], await Ids(CustomerSort.Spent));
        Assert.Equal([b.Id, a.Id, c.Id], await Ids(CustomerSort.LastOrder)); // b ordered 2 days ago, a 3 days ago, c never
    }

    // ---- Block ----

    [Fact]
    public async Task Block_SetsTheFlag_ChangesTheSecurityStamp_IsAudited_AndUnblockClearsIt()
    {
        var token = Token();
        var (id, email, _) = await NewCustomerAsync(token);
        var admin = await NewAdminAsync();
        var service = _fx.CreateCustomerService();
        var stampBefore = (await UserAsync(id)).SecurityStamp;

        Assert.Equal(CustomerActionStatus.Done, await service.BlockAsync(id, admin));

        var blocked = await UserAsync(id);
        Assert.NotNull(blocked.BlockedAt);
        Assert.NotEqual(stampBefore, blocked.SecurityStamp);
        Assert.True((await RowAsync(token, id)).IsBlocked);
        var line = Assert.Single(await AuditAsync("Customer.Blocked", id));
        Assert.Equal(admin, line.UserId);
        Assert.Equal("Customer", line.EntityType);
        Assert.Equal(email, line.Details);

        Assert.Equal(CustomerActionStatus.NothingToChange, await service.BlockAsync(id, admin)); // already blocked

        // Wrong passwords entered while blocked must not keep the customer out afterwards.
        using (var db = _fx.CreateDbContext())
        {
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.AccessFailedCount, 4).SetProperty(u => u.LockoutEnd, DateTimeOffset.UtcNow.AddHours(1)));
        }

        // A request of its own, like on the site (this service object still remembers the account as it was before the change above).
        Assert.Equal(CustomerActionStatus.Done, await _fx.CreateCustomerService().UnblockAsync(id, admin));
        var active = await UserAsync(id);
        Assert.Null(active.BlockedAt);
        Assert.Equal(0, active.AccessFailedCount);
        Assert.Null(active.LockoutEnd);
        Assert.Single(await AuditAsync("Customer.Unblocked", id));
        Assert.Equal(CustomerActionStatus.NothingToChange, await _fx.CreateCustomerService().UnblockAsync(id, admin)); // not blocked
    }

    [Fact]
    public async Task Block_RefusesAdminsAndUnknownAccounts_AndChangesNothing()
    {
        var admin = await NewAdminAsync();
        var otherAdmin = await NewAdminAsync();
        var service = _fx.CreateCustomerService();

        Assert.Equal(CustomerActionStatus.NotACustomer, await service.BlockAsync(otherAdmin, admin)); // admins are managed on the Admins page
        Assert.Equal(CustomerActionStatus.NotACustomer, await service.UnblockAsync(otherAdmin, admin));
        Assert.Equal(CustomerActionStatus.NotACustomer, await service.SetDiscountAsync(otherAdmin, 10, null, admin));
        Assert.Equal(CustomerActionStatus.NotFound, await service.BlockAsync("no-such-id", admin));
        Assert.Null((await UserAsync(otherAdmin)).BlockedAt);
        Assert.Empty(await AuditAsync("Customer.Blocked", otherAdmin));
    }

    // ---- Personal discount: the rules for setting it ----

    [Fact]
    public async Task SetDiscount_AcceptsOneToFifty_AndAnEndDayFromToday_AndRefusesTheRest()
    {
        var token = Token();
        var (id, email, _) = await NewCustomerAsync(token);
        var admin = await NewAdminAsync();
        var service = _fx.CreateCustomerService(); // "today" is June 1, 2026 in Armenia

        foreach (var bad in new int?[] { -5, 51, 100 })
        {
            Assert.Equal(CustomerActionStatus.InvalidDiscount, await service.SetDiscountAsync(id, bad, null, admin));
        }

        Assert.Equal(CustomerActionStatus.InvalidDiscount, await service.SetDiscountAsync(id, 15, new DateOnly(2026, 5, 31), admin)); // yesterday
        Assert.Null((await UserAsync(id)).PersonalDiscountPercent);

        Assert.Equal(CustomerActionStatus.Done, await service.SetDiscountAsync(id, 1, null, admin));
        Assert.Equal(CustomerActionStatus.Done, await service.SetDiscountAsync(id, 50, new DateOnly(2026, 6, 1), admin)); // today is allowed

        var user = await UserAsync(id);
        Assert.Equal(50, user.PersonalDiscountPercent);
        Assert.Equal(new DateOnly(2026, 6, 1), user.PersonalDiscountUntil);

        var row = await RowAsync(token, id);
        Assert.Equal(50, row.PersonalDiscountPercent);
        Assert.True(row.PersonalDiscountActive);

        var lines = await AuditAsync("Customer.DiscountSet", id);
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.Details == $"{email}: 50% until 2026-06-01");
        Assert.Contains(lines, l => l.Details == $"{email}: 1%");
    }

    [Fact]
    public async Task SetDiscount_RemovesIt_WithZeroOrNothing_AndSaysSoWhenThereIsNone()
    {
        var token = Token();
        var (id, _, _) = await NewCustomerAsync(token);
        var admin = await NewAdminAsync();
        var service = _fx.CreateCustomerService();

        Assert.Equal(CustomerActionStatus.NothingToChange, await service.SetDiscountAsync(id, 0, null, admin));
        await service.SetDiscountAsync(id, 20, new DateOnly(2026, 12, 31), admin);

        Assert.Equal(CustomerActionStatus.Done, await service.SetDiscountAsync(id, null, null, admin));

        var user = await UserAsync(id);
        Assert.Null(user.PersonalDiscountPercent);
        Assert.Null(user.PersonalDiscountUntil);
        Assert.Single(await AuditAsync("Customer.DiscountRemoved", id));
    }

    [Fact]
    public async Task APersonalDiscount_ThatHasRunOut_IsShownAsEnded()
    {
        var token = Token();
        var (id, _, _) = await NewCustomerAsync(token);
        using (var db = _fx.CreateDbContext())
        {
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.PersonalDiscountPercent, 15).SetProperty(u => u.PersonalDiscountUntil, new DateOnly(2026, 5, 31)));
        }

        var row = await RowAsync(token, id);

        Assert.Equal(15, row.PersonalDiscountPercent);
        Assert.False(row.PersonalDiscountActive);
    }

    [Fact]
    public async Task TheDatabase_RefusesAPersonalPercentOutsideOneToFifty()
    {
        var (id, _, _) = await NewCustomerAsync(Token());
        using var db = _fx.CreateDbContext();

        await Assert.ThrowsAnyAsync<Exception>(() => db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.PersonalDiscountPercent, 60)));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.PersonalDiscountPercent, 0)));
    }

    // ---- Personal discount: what it does to prices and orders ----

    private async Task<CartView> CartOfAsync(string? userId, int productId, int quantity = 1)
    {
        var owner = await _fx.FillCartAsync(userId, (productId, quantity));
        return await _fx.CreateServices().Cart.GetAsync(owner, "en");
    }

    [Fact]
    public async Task APersonalDiscount_GivesThatPercent_InsteadOfTheStandardTen_WithNoOrderLimit()
    {
        var (id, _, _) = await NewCustomerAsync(Token());
        var admin = await NewAdminAsync();
        await _fx.CreateCustomerService().SetDiscountAsync(id, 15, null, admin);
        var productId = await _fx.CreateProductAsync(price: 10000);

        var cart = await CartOfAsync(id, productId);

        Assert.True(cart.CustomerDiscountActive);
        Assert.True(cart.CustomerDiscountPersonal);
        Assert.Equal(15, cart.CustomerDiscountPercent);
        Assert.Equal(0, cart.CustomerDiscountOrdersLeft); // no order limit
        Assert.Equal(8500m, cart.Lines[0].UnitPrice);
        Assert.True(cart.Lines[0].CustomerDiscountApplied);
        Assert.Equal(1, cart.CustomerDiscountItemCount);
    }

    [Fact]
    public async Task APersonalDiscount_StillWorksAfterTheFifthOrder_WhereTheStandardOneStops()
    {
        var plain = await NewCustomerAsync(Token());
        var vip = await NewCustomerAsync(Token());
        var admin = await NewAdminAsync();
        for (var i = 0; i < 5; i++)
        {
            await _fx.PlaceOrderAsync(plain.Id);
            await _fx.PlaceOrderAsync(vip.Id);
        }

        await _fx.CreateCustomerService().SetDiscountAsync(vip.Id, 20, null, admin);
        var productId = await _fx.CreateProductAsync(price: 10000);

        var plainCart = await CartOfAsync(plain.Id, productId);
        var vipCart = await CartOfAsync(vip.Id, productId);

        Assert.False(plainCart.CustomerDiscountActive);
        Assert.Equal(10000m, plainCart.Lines[0].UnitPrice);
        Assert.True(vipCart.CustomerDiscountActive);
        Assert.Equal(8000m, vipCart.Lines[0].UnitPrice);
    }

    [Theory]
    [InlineData(10000, 20, 15, 8000, false)]   // the product's own 20% is cheaper than 15%: it wins, the customer percent is not "applied"
    [InlineData(10000, 20, 30, 7000, true)]    // 30% is cheaper: it wins
    [InlineData(10000, 15, 15, 8500, false)]   // equal: counts as the product discount
    [InlineData(10000, 0, 5, 9500, true)]      // a low personal percent still replaces the standard 10%
    public async Task APersonalDiscount_AndAProductDiscount_TheLowerPriceWins(decimal price, int productPercent, int personalPercent, decimal expectedUnit, bool applied)
    {
        var (id, _, _) = await NewCustomerAsync(Token());
        await _fx.CreateCustomerService().SetDiscountAsync(id, personalPercent, null, await NewAdminAsync());
        var productId = await _fx.CreateProductAsync(price: price, discountPercent: productPercent);

        var line = (await CartOfAsync(id, productId)).Lines[0];

        Assert.Equal(expectedUnit, line.UnitPrice);
        Assert.Equal(applied, line.CustomerDiscountApplied);
    }

    [Fact]
    public async Task APersonalDiscount_CountsUntilItsLastDay_ThenTheStandardRuleReturns()
    {
        var (id, _, _) = await NewCustomerAsync(Token());
        var productId = await _fx.CreateProductAsync(price: 10000);
        using (var db = _fx.CreateDbContext())
        {
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.PersonalDiscountPercent, 25).SetProperty(u => u.PersonalDiscountUntil, new DateOnly(2026, 6, 1)));
        }

        var lastDay = await CartOfAsync(id, productId); // "today" is June 1: the last day still counts
        Assert.True(lastDay.CustomerDiscountPersonal);
        Assert.Equal(7500m, lastDay.Lines[0].UnitPrice);

        // The next day it is over: the standard 10% on the first 5 orders is back.
        var owner = new CartOwner(id, null);
        var nextDay = await _fx.CreateServices(OrderFixture.Now.AddDays(1)).Cart.GetAsync(owner, "en");
        Assert.False(nextDay.CustomerDiscountPersonal);
        Assert.True(nextDay.CustomerDiscountActive);
        Assert.Equal(10, nextDay.CustomerDiscountPercent);
        Assert.Equal(5, nextDay.CustomerDiscountOrdersLeft);
        Assert.Equal(9000m, nextDay.Lines[0].UnitPrice);
    }

    [Fact]
    public async Task AGuest_NeverGetsAPersonalOrStandardDiscount()
    {
        var productId = await _fx.CreateProductAsync(price: 10000);

        var cart = await CartOfAsync(null, productId);

        Assert.False(cart.CustomerDiscountActive);
        Assert.Equal(10000m, cart.Lines[0].UnitPrice);
    }

    [Fact]
    public async Task AnOrder_KeepsThePercentThatGaveItsPrice_ForTheOrderAndEachItem()
    {
        var personal = await NewCustomerAsync(Token());
        var standard = await NewCustomerAsync(Token());
        await _fx.CreateCustomerService().SetDiscountAsync(personal.Id, 15, null, await NewAdminAsync());

        var personalOrder = await _fx.PlaceOrderAsync(personal.Id);
        var standardOrder = await _fx.PlaceOrderAsync(standard.Id);
        var guestOrder = await _fx.PlaceOrderAsync();

        using var db = _fx.CreateDbContext();
        async Task<(int OrderPercent, int ItemPercent, decimal UnitPrice)> Of(int number)
        {
            var order = await db.Orders.Include(o => o.Items).SingleAsync(o => o.Number == number);
            return (order.CustomerDiscountPercent, order.Items.Single().CustomerDiscountPercent, order.Items.Single().UnitPrice);
        }

        Assert.Equal((15, 15, 8500m), await Of(personalOrder.Number));
        Assert.Equal((10, 10, 9000m), await Of(standardOrder.Number));
        Assert.Equal((0, 0, 10000m), await Of(guestOrder.Number));

        // Taking the personal discount away later does not change the order that already got it.
        await _fx.CreateCustomerService().SetDiscountAsync(personal.Id, 0, null, await NewAdminAsync());
        using var db2 = _fx.CreateDbContext();
        Assert.Equal(15, (await db2.Orders.SingleAsync(o => o.Number == personalOrder.Number)).CustomerDiscountPercent);
    }

    // ---- Messages ----

    [Fact]
    public async Task Send_SavesTheMessage_QueuesTheEmail_AndIsAudited()
    {
        var token = Token();
        var (id, email, _) = await NewCustomerAsync(token);
        var admin = await NewAdminAsync();

        var status = await _fx.CreateCustomerService().SendMessageAsync(id, "  Your order  ", "Hello!\r\nWe have a gift for you.  ", "ru", admin);

        Assert.Equal(CustomerActionStatus.Done, status);
        using var db = _fx.CreateDbContext();
        var message = await db.CustomerMessages.SingleAsync(m => m.UserId == id);
        Assert.Equal("Your order", message.Subject);
        Assert.Equal("Hello!\nWe have a gift for you.", message.Body); // trimmed, new lines normalised
        Assert.Equal("ru", message.Culture);
        Assert.Equal(admin, message.SentByUserId);
        Assert.False(string.IsNullOrEmpty(message.SentByEmail));

        var queued = await db.OutboxEmails.SingleAsync(e => e.CustomerMessageId == message.Id);
        Assert.Equal(EmailKind.CustomerMessage, queued.Kind);
        Assert.Equal(OutboxStatus.Pending, queued.Status);
        Assert.Equal(0, queued.OrderNumber);

        var line = Assert.Single(await AuditAsync("Customer.MessageSent", id));
        Assert.Equal($"{email}: Your order", line.Details);

        var row = await RowAsync(token, id);
        var shown = Assert.Single(row.Messages);
        Assert.Equal("Your order", shown.Subject);
        Assert.Equal(OutboxStatus.Pending, shown.EmailStatus);
    }

    [Theory]
    [InlineData("", "Text")]
    [InlineData("   ", "Text")]
    [InlineData("Subject", "")]
    [InlineData("Subject", "   ")]
    public async Task Send_RefusesAnEmptySubjectOrText_AndSavesNothing(string subject, string body)
    {
        var (id, _, _) = await NewCustomerAsync(Token());

        Assert.Equal(CustomerActionStatus.InvalidMessage, await _fx.CreateCustomerService().SendMessageAsync(id, subject, body, "en", await NewAdminAsync()));

        using var db = _fx.CreateDbContext();
        Assert.False(await db.CustomerMessages.AnyAsync(m => m.UserId == id));
    }

    [Fact]
    public async Task Send_AllowsTheLongestSubjectAndText_AndRefusesOneCharacterMore()
    {
        var (id, _, _) = await NewCustomerAsync(Token());
        var admin = await NewAdminAsync();
        var service = _fx.CreateCustomerService();

        Assert.Equal(CustomerActionStatus.InvalidMessage, await service.SendMessageAsync(id, new string('s', 151), "Text", "en", admin));
        Assert.Equal(CustomerActionStatus.InvalidMessage, await service.SendMessageAsync(id, "Subject", new string('b', 2001), "en", admin));
        Assert.Equal(CustomerActionStatus.Done, await service.SendMessageAsync(id, new string('s', 150), new string('b', 2000), "en", admin));
    }

    [Fact]
    public async Task Send_AtMostFivePerCustomerIn24Hours_ThenAgainWhenTheOldOnesAreOlder()
    {
        var (id, _, _) = await NewCustomerAsync(Token());
        var (otherId, _, _) = await NewCustomerAsync(Token());
        var admin = await NewAdminAsync();
        var service = _fx.CreateCustomerService();

        for (var i = 1; i <= 5; i++)
        {
            Assert.Equal(CustomerActionStatus.Done, await service.SendMessageAsync(id, "Message " + i, "Text", "en", admin));
        }

        Assert.Equal(CustomerActionStatus.TooManyMessages, await service.SendMessageAsync(id, "Message 6", "Text", "en", admin));
        Assert.Equal(CustomerActionStatus.Done, await service.SendMessageAsync(otherId, "Another customer", "Text", "en", admin)); // the limit is per customer

        // 24 hours and a minute later the first five are outside the window.
        Assert.Equal(CustomerActionStatus.Done, await _fx.CreateCustomerService(OrderFixture.Now.AddHours(24).AddMinutes(1)).SendMessageAsync(id, "Message 7", "Text", "en", admin));

        using var db = _fx.CreateDbContext();
        Assert.Equal(6, await db.CustomerMessages.CountAsync(m => m.UserId == id));
        Assert.Equal(6, await db.OutboxEmails.CountAsync(e => db.CustomerMessages.Any(m => m.Id == e.CustomerMessageId && m.UserId == id)));
    }

    [Fact]
    public async Task Send_EightAtTheSameMoment_NeverPassesTheLimit()
    {
        var (id, _, _) = await NewCustomerAsync(Token());
        var admin = await NewAdminAsync();

        var results = await Task.WhenAll(Enumerable.Range(1, 8).Select(i =>
            Task.Run(() => _fx.CreateCustomerService().SendMessageAsync(id, "Parallel " + i, "Text", "en", admin))));

        Assert.Equal(5, results.Count(r => r == CustomerActionStatus.Done));
        Assert.Equal(3, results.Count(r => r == CustomerActionStatus.TooManyMessages));
        using var db = _fx.CreateDbContext();
        Assert.Equal(5, await db.CustomerMessages.CountAsync(m => m.UserId == id));
    }

    [Fact]
    public async Task Send_RefusesAnUnconfirmedEmail_Admins_AndUnknownAccounts()
    {
        var (unconfirmedId, _, _) = await NewCustomerAsync(Token(), confirmed: false);
        var adminId = await NewAdminAsync();
        var service = _fx.CreateCustomerService();

        Assert.Equal(CustomerActionStatus.EmailNotConfirmed, await service.SendMessageAsync(unconfirmedId, "Hi", "Text", "en", adminId));
        Assert.Equal(CustomerActionStatus.NotACustomer, await service.SendMessageAsync(adminId, "Hi", "Text", "en", adminId));
        Assert.Equal(CustomerActionStatus.NotFound, await service.SendMessageAsync("no-such-id", "Hi", "Text", "en", adminId));

        using var db = _fx.CreateDbContext();
        Assert.False(await db.CustomerMessages.AnyAsync(m => m.UserId == unconfirmedId || m.UserId == adminId));
    }

    [Fact]
    public async Task Send_AnUnknownLanguage_FallsBackToArmenian()
    {
        var (id, _, _) = await NewCustomerAsync(Token());

        await _fx.CreateCustomerService().SendMessageAsync(id, "Hi", "Text", "xx", await NewAdminAsync());

        using var db = _fx.CreateDbContext();
        Assert.Equal("hy", (await db.CustomerMessages.SingleAsync(m => m.UserId == id)).Culture);
    }
}

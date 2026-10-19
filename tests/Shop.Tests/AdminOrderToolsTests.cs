using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Carts;
using Shop.Core.Entities;
using Shop.Core.Notifications;
using Shop.Core.Orders;
using Shop.Core.Phones;
using Shop.Infrastructure.Admin;
using Shop.Infrastructure.Orders;

namespace Shop.Tests;

public class PhoneSearchNeedleTests
{
    [Theory]
    [InlineData("091 123456", "91123456")]
    [InlineData("091123456", "91123456")]
    [InlineData("+374 91 123456", "91123456")]
    [InlineData("+37491123456", "91123456")]
    [InlineData("0037491123456", "91123456")]
    [InlineData("91123456", "91123456")]
    [InlineData("(091) 123-456", "91123456")]
    [InlineData("091 12", "9112")]            // part of a number
    [InlineData("+7 912 345 6789", "79123456789")]
    [InlineData("10006", "10006")]            // a plain number is searched as digits too
    public void ATypedPhone_BecomesTheDigitsThatAreStoredInsideTheNumber(string typed, string expected)
    {
        Assert.Equal(expected, PhoneNumbers.SearchNeedle(typed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Anna")]                      // letters: not a phone
    [InlineData("anna@example.com")]
    [InlineData("12")]                        // too short to search
    [InlineData("091")]
    [InlineData("+374")]
    public void NotAPhone_OrTooShort_GivesNoNeedle(string? typed)
    {
        Assert.Null(PhoneNumbers.SearchNeedle(typed));
    }
}

// Notes, editing, packing slips and the phone search of the admin order list, on the real database.
[Collection("Orders")]
public class AdminOrderToolsTests
{
    private readonly OrderFixture _fx;

    public AdminOrderToolsTests(OrderFixture fx)
    {
        _fx = fx;
    }

    private async Task<string> NewAdminAsync()
    {
        var id = await _fx.CreateUserAsync();
        using var db = _fx.CreateDbContext();
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Email, $"admin-{Guid.NewGuid():N}@example.com"));
        return id;
    }

    private static string UniquePhone() => "+374" + new Random().Next(10, 99) + new Random().Next(100000, 999999);

    private async Task SetPhoneAsync(int number, string phone)
    {
        using var db = _fx.CreateDbContext();
        await db.Orders.Where(o => o.Number == number).ExecuteUpdateAsync(s => s.SetProperty(o => o.Phone, phone));
    }

    private async Task<(int Number, string Phone)> OrderWithUniquePhoneAsync(PaymentMethod method = PaymentMethod.CashOnDelivery)
    {
        var order = await _fx.PlaceOrderAsync(method: method);
        var phone = UniquePhone();
        await SetPhoneAsync(order.Number, phone);
        return (order.Number, phone);
    }

    private async Task<AdminOrderEdit> EditOfAsync(int number)
    {
        var order = (await _fx.CreateOrderService().FindForSystemAsync(number))!;
        var settings = await new StoreSettingsService(_fx.CreateDbContext()).GetAsync();

        return new AdminOrderEdit
        {
            CustomerName = order.CustomerName,
            Email = order.Email,
            Phone = order.Phone,
            Region = order.Region,
            City = order.City,
            Street = order.Street,
            Building = order.Building,
            Apartment = order.Apartment,
            Comment = order.Comment,
            DeliveryDate = order.DeliveryDate,
            DeliverySlotId = settings.Slots.First(s => s.Label == order.DeliverySlot).Id
        };
    }

    private async Task<(IReadOnlyList<SlotOption> Slots, IReadOnlyList<DateOnly> Days)> OptionsAsync()
    {
        var settings = await new StoreSettingsService(_fx.CreateDbContext()).GetAsync();
        return (settings.Slots, DeliveryRules.GetDeliveryDates(ShopClock.Today(new FixedTimeProvider(OrderFixture.Now)), settings.DeliveryFirstDayOffset, settings.DeliveryDaysAhead));
    }

    private async Task<List<OutboxEmail>> EmailsOfAsync(int number)
    {
        using var db = _fx.CreateDbContext();
        return await db.OutboxEmails.AsNoTracking().Where(e => e.OrderNumber == number).ToListAsync();
    }

    // ---- Search by phone ----

    [Theory]
    [InlineData("091 123456")]
    [InlineData("+374 91 123456")]
    [InlineData("91123456")]
    [InlineData("0037491123456")]
    [InlineData("091 12")]
    public async Task TheOrderList_FindsAPhone_HoweverItIsTyped(string typed)
    {
        // A phone only this test has: +374 91 123456 is the fixture's own, so use 91 + a random 6 digits and type the same in each way.
        var (number, phone) = await OrderWithUniquePhoneAsync();
        var digits = phone[4..]; // the 8 digits after +374
        var variants = typed.Replace("91123456", digits).Replace("091 123456", "0" + digits).Replace("+374 91 123456", "+374 " + digits)
            .Replace("0037491123456", "00374" + digits).Replace("091 12", "0" + digits[..4]);

        var found = await _fx.CreateAdminServices().Orders.ListAsync(new AdminOrderFilter { Query = variants });

        Assert.Contains(found.Items, r => r.Number == number);
    }

    [Fact]
    public async Task TheOrderList_StillFindsByNumber_Name_AndEmail_AndDoesNotFindOtherPhones()
    {
        var (number, phone) = await OrderWithUniquePhoneAsync();
        var (otherNumber, _) = await OrderWithUniquePhoneAsync();
        var service = _fx.CreateAdminServices().Orders;

        var byNumber = await service.ListAsync(new AdminOrderFilter { Query = number.ToString() });
        Assert.Contains(byNumber.Items, r => r.Number == number);
        Assert.DoesNotContain(byNumber.Items, r => r.Number == otherNumber);

        var byPhone = await service.ListAsync(new AdminOrderFilter { Query = phone });
        Assert.Contains(byPhone.Items, r => r.Number == number);
        Assert.DoesNotContain(byPhone.Items, r => r.Number == otherNumber);

        Assert.NotEmpty((await service.ListAsync(new AdminOrderFilter { Query = "Anna" })).Items);               // the fixture's customer name
        Assert.Empty((await service.ListAsync(new AdminOrderFilter { Query = "no-such-person-anywhere" })).Items);
        Assert.NotNull(await service.ListAsync(new AdminOrderFilter { Query = "12" }));                            // too short to be a phone: no failure
    }

    // ---- Notes ----

    [Fact]
    public async Task AddNote_SavesItWithTheAuthor_ShowsItOnlyToAdmins_CountsItInTheList_AndIsAudited()
    {
        var (number, _) = await OrderWithUniquePhoneAsync();
        var admin = await NewAdminAsync();
        var service = _fx.CreateAdminServices().Orders;

        Assert.Equal(AddNoteStatus.Added, await service.AddNoteAsync(number, "  Call before delivery  ", admin));
        Assert.Equal(AddNoteStatus.Added, await _fx.CreateAdminServices().Orders.AddNoteAsync(number, "Second note", admin));

        var detail = (await _fx.CreateAdminServices().Orders.GetAsync(number))!;
        Assert.Equal(["Call before delivery", "Second note"], detail.Notes!.Select(n => n.Text));
        Assert.All(detail.Notes!, n => Assert.StartsWith("admin-", n.ByEmail));

        var row = (await _fx.CreateAdminServices().Orders.ListAsync(new AdminOrderFilter { Query = number.ToString() })).Items.Single(r => r.Number == number);
        Assert.Equal(2, row.NoteCount);

        using var db = _fx.CreateDbContext();
        Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.Action == "Order.NoteAdded" && a.EntityId == number.ToString() && a.UserId == admin));

        // Nothing the customer can see carries the note text.
        var summary = (await _fx.CreateOrderService().FindForSystemAsync(number))!;
        Assert.DoesNotContain("Call before delivery", System.Text.Json.JsonSerializer.Serialize(summary));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddNote_RefusesAnEmptyNote_AndOneOverTheLimit_AndAnUnknownOrder(string text)
    {
        var (number, _) = await OrderWithUniquePhoneAsync();
        var admin = await NewAdminAsync();
        var service = _fx.CreateAdminServices().Orders;

        Assert.Equal(AddNoteStatus.Invalid, await service.AddNoteAsync(number, text, admin));
        Assert.Equal(AddNoteStatus.Invalid, await service.AddNoteAsync(number, new string('n', 1001), admin));
        Assert.Equal(AddNoteStatus.Added, await service.AddNoteAsync(number, new string('n', 1000), admin));
        Assert.Equal(AddNoteStatus.NotFound, await service.AddNoteAsync(99999999, "x", admin));
    }

    [Fact]
    public async Task ANote_IsNeverInTheOrdersCsvExport()
    {
        var (number, _) = await OrderWithUniquePhoneAsync();
        var marker = "SECRET-NOTE-" + Guid.NewGuid().ToString("N");
        await _fx.CreateAdminServices().Orders.AddNoteAsync(number, marker, await NewAdminAsync());

        var csv = await new AdminAnalyticsService(_fx.CreateDbContext(), new FixedTimeProvider(OrderFixture.Now)).ExportOrdersCsvAsync(new AnalyticsPeriod(new DateOnly(2026, 5, 1), new DateOnly(2026, 6, 30)), await NewAdminAsync());
        var text = System.Text.Encoding.UTF8.GetString(csv);

        Assert.DoesNotContain(marker, text);
        Assert.Contains("Source", text.Split('\n')[0]);
    }

    // ---- Edit ----

    [Fact]
    public async Task Edit_ChangesContactAddressDayTimeAndComment_WritesHistoryAndAudit_AndQueuesOneUpdateEmail()
    {
        var order = await _fx.PlaceOrderAsync();
        var admin = await NewAdminAsync();
        var (slots, days) = await OptionsAsync();
        var edit = await EditOfAsync(order.Number);
        var newSlot = slots.First(s => s.Label != order.DeliverySlot);
        var newDay = days.Last();

        edit.CustomerName = "  New  Name ";
        edit.Phone = "093 555777";
        edit.Email = "changed@example.com";
        edit.Region = "kotayk";
        edit.City = "Abovyan";
        edit.Street = "Main";
        edit.Building = "7";
        edit.Apartment = "";
        edit.Comment = "Leave with the neighbour";
        edit.DeliveryDate = newDay;
        edit.DeliverySlotId = newSlot.Id;

        Assert.Equal(EditOrderStatus.Saved, await _fx.CreateAdminServices().Orders.UpdateAsync(order.Number, edit, admin));

        var saved = (await _fx.CreateOrderService().FindForSystemAsync(order.Number))!;
        Assert.Equal("New  Name".Replace("  ", " "), string.Join(' ', saved.CustomerName.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal("+37493555777", saved.Phone);
        Assert.Equal("changed@example.com", saved.Email);
        Assert.Equal("kotayk", saved.Region);
        Assert.Equal("Abovyan", saved.City);
        Assert.Equal("Main", saved.Street);
        Assert.Equal("7", saved.Building);
        Assert.Null(saved.Apartment);
        Assert.Equal("Leave with the neighbour", saved.Comment);
        Assert.Equal(newDay, saved.DeliveryDate);
        Assert.Equal(newSlot.Label, saved.DeliverySlot);
        Assert.Equal(order.Total, saved.Total); // items and prices are not touched
        Assert.Equal(order.Status, saved.Status);

        using var db = _fx.CreateDbContext();
        var ev = await db.OrderEvents.Where(e => e.Order.Number == order.Number && e.Note!.StartsWith("Edited:")).SingleAsync();
        Assert.Equal(admin, ev.ByUserId);
        Assert.Equal(order.Status, ev.Status);
        Assert.Contains("phone", ev.Note);
        Assert.Contains("slot", ev.Note);

        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "Order.Edited" && a.EntityId == order.Number.ToString());
        Assert.Equal(admin, audit.UserId);
        Assert.Contains("kotayk", audit.Details);

        var updates = (await EmailsOfAsync(order.Number)).Where(e => e.Kind == EmailKind.OrderUpdated).ToList();
        Assert.Single(updates);
        Assert.Equal(OutboxStatus.Pending, updates[0].Status);
    }

    [Fact]
    public async Task Edit_OnlyTheCommentOrContact_DoesNotEmailTheCustomer_AndNothingChangedSaysSo()
    {
        var order = await _fx.PlaceOrderAsync();
        var admin = await NewAdminAsync();
        var service = _fx.CreateAdminServices().Orders;

        Assert.Equal(EditOrderStatus.NoChange, await service.UpdateAsync(order.Number, await EditOfAsync(order.Number), admin));

        var edit = await EditOfAsync(order.Number);
        edit.Comment = "Only a comment";
        edit.CustomerName = "Another Name";
        Assert.Equal(EditOrderStatus.Saved, await _fx.CreateAdminServices().Orders.UpdateAsync(order.Number, edit, admin));

        Assert.DoesNotContain(await EmailsOfAsync(order.Number), e => e.Kind == EmailKind.OrderUpdated);
        using var db = _fx.CreateDbContext();
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == "Order.Edited" && a.EntityId == order.Number.ToString())); // the "no change" wrote nothing
    }

    [Fact]
    public async Task Edit_RefusesWrongContact_WrongDelivery_AndChangesNothing()
    {
        var order = await _fx.PlaceOrderAsync();
        var admin = await NewAdminAsync();
        var (slots, days) = await OptionsAsync();
        var service = _fx.CreateAdminServices().Orders;

        async Task<EditOrderStatus> Try(Action<AdminOrderEdit> change)
        {
            var edit = await EditOfAsync(order.Number);
            change(edit);
            return await _fx.CreateAdminServices().Orders.UpdateAsync(order.Number, edit, admin);
        }

        Assert.Equal(EditOrderStatus.InvalidContact, await Try(e => e.CustomerName = "  "));
        Assert.Equal(EditOrderStatus.InvalidContact, await Try(e => e.CustomerName = new string('n', 101)));
        Assert.Equal(EditOrderStatus.InvalidContact, await Try(e => e.Phone = "12"));
        Assert.Equal(EditOrderStatus.InvalidContact, await Try(e => e.Email = "not-an-email"));

        Assert.Equal(EditOrderStatus.InvalidDelivery, await Try(e => e.Region = "mars"));
        Assert.Equal(EditOrderStatus.InvalidDelivery, await Try(e => e.City = ""));
        Assert.Equal(EditOrderStatus.InvalidDelivery, await Try(e => e.DeliverySlotId = 987654));
        Assert.Equal(EditOrderStatus.InvalidDelivery, await Try(e => e.DeliveryDate = ShopClock.Today(new FixedTimeProvider(OrderFixture.Now)).AddDays(-3))); // the past
        Assert.Equal(EditOrderStatus.InvalidDelivery, await Try(e => e.DeliveryDate = days.Last().AddDays(30)));                                               // too far
        Assert.Equal(EditOrderStatus.InvalidDelivery, await Try(e => e.Comment = new string('c', 501)));

        var after = (await _fx.CreateOrderService().FindForSystemAsync(order.Number))!;
        Assert.Equal(order.CustomerName, after.CustomerName);
        Assert.Equal(order.DeliveryDate, after.DeliveryDate);
        Assert.NotNull(slots);
        Assert.NotNull(service);
    }

    [Fact]
    public async Task Edit_ADayThatIsAlreadyOnTheOrder_StaysValid_EvenWhenItWouldNotBeOfferedAnyMore()
    {
        var order = await _fx.PlaceOrderAsync();
        using (var db = _fx.CreateDbContext())
        {
            await db.Orders.Where(o => o.Number == order.Number).ExecuteUpdateAsync(s => s.SetProperty(o => o.DeliveryDate, new DateOnly(2026, 5, 30)));
        }

        var edit = await EditOfAsync(order.Number);
        edit.Comment = "Late, but the day stays";

        Assert.Equal(EditOrderStatus.Saved, await _fx.CreateAdminServices().Orders.UpdateAsync(order.Number, edit, await NewAdminAsync()));
        Assert.Equal(new DateOnly(2026, 5, 30), (await _fx.CreateOrderService().FindForSystemAsync(order.Number))!.DeliveryDate);
    }

    [Fact]
    public async Task Edit_FollowsTheStatus_FullWhileOrdered_OrAwaitingPayment_CommentOnlyWhenPacked_NothingAfter()
    {
        var admin = await NewAdminAsync();
        var orders = _fx.CreateOrderService();

        // Awaiting payment (bank transfer): full edit.
        var transfer = await _fx.PlaceOrderAsync(method: PaymentMethod.BankTransfer);
        var transferEdit = await EditOfAsync(transfer.Number);
        transferEdit.City = "Gyumri";
        Assert.Equal(EditOrderStatus.Saved, await _fx.CreateAdminServices().Orders.UpdateAsync(transfer.Number, transferEdit, admin));
        Assert.Equal(OrderEditScope.Full, (await _fx.CreateAdminServices().Orders.GetAsync(transfer.Number))!.EditScope);

        // Packed: the comment only.
        var packed = await _fx.PlaceOrderAsync();
        await orders.ChangeStatusAsync(packed.Number, OrderStatus.Packed, null, null);
        Assert.Equal(OrderEditScope.CommentOnly, (await _fx.CreateAdminServices().Orders.GetAsync(packed.Number))!.EditScope);

        var comment = await EditOfAsync(packed.Number);
        comment.Comment = "Ring the bell twice";
        Assert.Equal(EditOrderStatus.Saved, await _fx.CreateAdminServices().Orders.UpdateAsync(packed.Number, comment, admin));
        Assert.DoesNotContain(await EmailsOfAsync(packed.Number), e => e.Kind == EmailKind.OrderUpdated);

        foreach (var forged in new Action<AdminOrderEdit>[] { e => e.Street = "Other", e => e.CustomerName = "Other", e => e.Phone = "094 111222", e => e.Email = "other@example.com" })
        {
            var edit = await EditOfAsync(packed.Number);
            forged(edit);
            Assert.Equal(EditOrderStatus.NotEditable, await _fx.CreateAdminServices().Orders.UpdateAsync(packed.Number, edit, admin));
        }

        // Delivered and cancelled: nothing at all.
        var delivered = await _fx.PlaceOrderAsync();
        await orders.ChangeStatusAsync(delivered.Number, OrderStatus.Packed, null, null);
        await orders.ChangeStatusAsync(delivered.Number, OrderStatus.Delivered, null, null);
        var cancelled = await _fx.PlaceOrderAsync();
        await orders.ChangeStatusAsync(cancelled.Number, OrderStatus.Cancelled, null, null);

        foreach (var number in new[] { delivered.Number, cancelled.Number })
        {
            var edit = await EditOfAsync(number);
            edit.Comment = "Too late";
            Assert.Equal(EditOrderStatus.NotEditable, await _fx.CreateAdminServices().Orders.UpdateAsync(number, edit, admin));
            Assert.Equal(OrderEditScope.None, (await _fx.CreateAdminServices().Orders.GetAsync(number))!.EditScope);
        }

        Assert.Equal(EditOrderStatus.NotFound, await _fx.CreateAdminServices().Orders.UpdateAsync(99999999, await EditOfAsync(transfer.Number), admin));
    }

    // ---- Packing slips ----

    [Fact]
    public async Task Slip_HasTheItemsWithPrices_TheTotals_TheAddress_AndTheCashToCollect()
    {
        var order = await _fx.PlaceOrderAsync(quantity: 2);
        var slip = (await _fx.CreateAdminServices().Orders.GetSlipAsync(order.Number))!;

        Assert.Equal(order.Number, slip.Number);
        var line = Assert.Single(slip.Lines);
        Assert.False(string.IsNullOrWhiteSpace(line.Sku));
        Assert.Equal(2, line.Quantity);
        Assert.Equal(order.Lines[0].UnitPrice, line.UnitPrice);
        Assert.Equal(order.Lines[0].UnitPrice * 2, line.LineTotal);
        Assert.Equal(order.Subtotal, slip.Subtotal);
        Assert.Equal(order.Discount, slip.Discount);
        Assert.Equal(order.DeliveryFee, slip.DeliveryFee);
        Assert.Equal(order.Total, slip.Total);
        Assert.Equal(order.DeliveryDate, slip.DeliveryDate);
        Assert.Equal(order.DeliverySlot, slip.DeliverySlot);
        Assert.Equal(order.Phone, slip.Phone);
        Assert.Equal(order.Street, slip.Street);
        Assert.False(slip.IsPhoneOrder);

        // Cash order, not delivered: the courier collects the total.
        Assert.Equal(order.Total, slip.AmountToCollect);
        Assert.Null(await _fx.CreateAdminServices().Orders.GetSlipAsync(99999999));
    }

    [Fact]
    public async Task Slip_CashToCollect_IsZeroForATransfer_ADeliveredOrder_AndACancelledOne()
    {
        var orders = _fx.CreateOrderService();
        var transfer = await _fx.PlaceOrderAsync(method: PaymentMethod.BankTransfer);
        var delivered = await _fx.PlaceOrderAsync();
        await orders.ChangeStatusAsync(delivered.Number, OrderStatus.Packed, null, null);
        await orders.ChangeStatusAsync(delivered.Number, OrderStatus.Delivered, null, null);
        var cancelled = await _fx.PlaceOrderAsync();
        await orders.ChangeStatusAsync(cancelled.Number, OrderStatus.Cancelled, null, null);

        foreach (var number in new[] { transfer.Number, delivered.Number, cancelled.Number })
        {
            Assert.Equal(0m, (await _fx.CreateAdminServices().Orders.GetSlipAsync(number))!.AmountToCollect);
        }
    }

    [Fact]
    public async Task Slips_AreInDeliveryOrder_FilteredLikeTheList_AndCapped()
    {
        var phone = UniquePhone();
        var a = await _fx.PlaceOrderAsync();
        var b = await _fx.PlaceOrderAsync();
        var c = await _fx.PlaceOrderAsync();
        var (_, days) = await OptionsAsync();

        using (var db = _fx.CreateDbContext())
        {
            // c first (earliest day), then a and b on the same day, ordered by number.
            await db.Orders.Where(o => o.Number == c.Number).ExecuteUpdateAsync(s => s.SetProperty(o => o.Phone, phone).SetProperty(o => o.DeliveryDate, days.First()).SetProperty(o => o.DeliverySlot, "10:00-12:00"));
            await db.Orders.Where(o => o.Number == a.Number || o.Number == b.Number).ExecuteUpdateAsync(s => s.SetProperty(o => o.Phone, phone).SetProperty(o => o.DeliveryDate, days.Last()).SetProperty(o => o.DeliverySlot, "10:00-12:00"));
        }

        var slips = await _fx.CreateAdminServices().Orders.GetSlipsAsync(new AdminOrderFilter { Query = phone }, 10);
        Assert.Equal([c.Number, a.Number, b.Number], slips.Select(s => s.Number));

        Assert.Equal(2, (await _fx.CreateAdminServices().Orders.GetSlipsAsync(new AdminOrderFilter { Query = phone }, 2)).Count);
        Assert.Empty(await _fx.CreateAdminServices().Orders.GetSlipsAsync(new AdminOrderFilter { Query = phone, Status = OrderStatus.Packed }, 10));
    }

    // ---- A phone order is marked in the list and on the detail ----

    [Fact]
    public async Task APhoneOrder_IsMarked_WithWhoMadeIt()
    {
        var admin = await NewAdminAsync();
        var order = await _fx.PlaceOrderAsync();
        using (var db = _fx.CreateDbContext())
        {
            await db.Orders.Where(o => o.Number == order.Number).ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedByUserId, admin));
        }

        var row = (await _fx.CreateAdminServices().Orders.ListAsync(new AdminOrderFilter { Query = order.Number.ToString() })).Items.Single(r => r.Number == order.Number);
        var detail = (await _fx.CreateAdminServices().Orders.GetAsync(order.Number))!;
        var slip = (await _fx.CreateAdminServices().Orders.GetSlipAsync(order.Number))!;

        Assert.True(row.IsPhoneOrder);
        Assert.True(detail.IsPhoneOrder);
        Assert.StartsWith("admin-", detail.CreatedByEmail);
        Assert.True(slip.IsPhoneOrder);

        var website = await _fx.PlaceOrderAsync();
        Assert.False((await _fx.CreateAdminServices().Orders.GetAsync(website.Number))!.IsPhoneOrder);
    }
}

// An admin places an order for a customer by phone: prices, stock, notifications and emails.
[Collection("Orders")]
public class PlaceForAdminTests
{
    private readonly OrderFixture _fx;

    public PlaceForAdminTests(OrderFixture fx)
    {
        _fx = fx;
    }

    private async Task<string> NewAdminAsync() => await _fx.CreateUserAsync();

    // The input the phone-order page would send, with the total the admin saw (the staff cart priced for the customer).
    private async Task<CheckoutInput> InputForAsync(string adminId, string? customerId, PaymentMethod method = PaymentMethod.CashOnDelivery, string email = "", decimal? expected = null)
    {
        var cart = await _fx.CreateServices().Cart.GetForCustomerAsync(new CartOwner(adminId, null), "en", customerId);
        var settings = await new StoreSettingsService(_fx.CreateDbContext()).GetAsync();
        var fee = DeliveryRules.GetFee(cart.Total, settings.DeliveryFee, settings.FreeDeliveryFrom);

        return new CheckoutInput
        {
            CheckoutId = Guid.NewGuid(),
            CustomerName = "Phone Customer",
            Email = email,
            Phone = "+37491765432",
            Region = "yerevan",
            City = "Yerevan",
            Street = "Abovyan",
            Building = "1",
            DeliveryDate = OrderFixture.FirstDeliveryDate,
            DeliverySlotId = _fx.SlotId,
            PaymentMethod = method,
            ExpectedTotal = expected ?? cart.Total + fee
        };
    }

    private async Task FillStaffCartAsync(string adminId, int productId, int quantity = 1)
    {
        var result = await _fx.CreateServices().Cart.AddAsync(new CartOwner(adminId, null), productId, quantity);
        Assert.Equal(CartChangeStatus.Added, result.Status);
    }

    [Fact]
    public async Task ForARegisteredCustomer_UsesTheirDiscount_BelongsToThem_AndRecordsTheAdmin_AndEmptiesTheStaffCart()
    {
        var admin = await NewAdminAsync();
        var customer = await _fx.CreateUserAsync();
        var productId = await _fx.CreateProductAsync(price: 10000);
        await FillStaffCartAsync(admin, productId, 2);

        var result = await _fx.CreateOrderService().PlaceForAdminAsync(admin, customer, await InputForAsync(admin, customer, email: "phone@example.com"), "ru");

        Assert.Equal(PlaceOrderStatus.Placed, result.Status);
        var order = result.Order!;
        Assert.Equal(9000m, order.Lines[0].UnitPrice);      // the customer's standard 10%, not the admin's (an admin has none)
        Assert.True(order.CustomerDiscountApplied);
        Assert.Equal("ru", order.Culture);

        using var db = _fx.CreateDbContext();
        var stored = await db.Orders.SingleAsync(o => o.Number == order.Number);
        Assert.Equal(customer, stored.UserId);
        Assert.Equal(admin, stored.CreatedByUserId);
        Assert.Equal(10, stored.CustomerDiscountPercent);
        Assert.Equal(OrderStatus.Ordered, stored.Status);

        // The order is in the customer's own list; the staff cart is empty.
        Assert.Contains(await _fx.CreateOrderService().ListForUserAsync(customer), o => o.Number == order.Number);
        Assert.Equal(0, await _fx.CreateServices().Cart.CountAsync(new CartOwner(admin, null)));
    }

    [Fact]
    public async Task ForACustomerWithAPersonalDiscount_ThatDiscountIsUsed()
    {
        var admin = await NewAdminAsync();
        var customer = await _fx.CreateUserAsync();
        await _fx.CreateCustomerService().SetDiscountAsync(customer, 25, null, admin);
        var productId = await _fx.CreateProductAsync(price: 10000);
        await FillStaffCartAsync(admin, productId);

        var result = await _fx.CreateOrderService().PlaceForAdminAsync(admin, customer, await InputForAsync(admin, customer), "en");

        Assert.Equal(7500m, result.Order!.Lines[0].UnitPrice);
        using var db = _fx.CreateDbContext();
        Assert.Equal(25, (await db.Orders.SingleAsync(o => o.Number == result.Order.Number)).CustomerDiscountPercent);
    }

    [Fact]
    public async Task ForACustomerWithoutAnAccount_HasNoDiscount_NoUser_AndMayHaveNoEmail()
    {
        var admin = await NewAdminAsync();
        var productId = await _fx.CreateProductAsync(price: 10000);
        await FillStaffCartAsync(admin, productId);

        var result = await _fx.CreateOrderService().PlaceForAdminAsync(admin, null, await InputForAsync(admin, null), "hy");

        Assert.Equal(PlaceOrderStatus.Placed, result.Status);
        Assert.Equal(10000m, result.Order!.Lines[0].UnitPrice);
        Assert.Equal(string.Empty, result.Order.Email);
        using var db = _fx.CreateDbContext();
        var stored = await db.Orders.SingleAsync(o => o.Number == result.Order.Number);
        Assert.Null(stored.UserId);
        Assert.Equal(admin, stored.CreatedByUserId);
        Assert.Equal(0, stored.CustomerDiscountPercent);
    }

    [Fact]
    public async Task AnAdminsOrder_QueuesOnlyTheCustomersConfirmation_NoShopEmailAndNoNewOrderBell()
    {
        var admin = await NewAdminAsync();
        var productId = await _fx.CreateProductAsync(price: 10000, stock: 50);
        await FillStaffCartAsync(admin, productId);

        var result = await _fx.CreateOrderService().PlaceForAdminAsync(admin, null, await InputForAsync(admin, null, email: "someone@example.com"), "en");

        using var db = _fx.CreateDbContext();
        var emails = await db.OutboxEmails.Where(e => e.OrderNumber == result.Order!.Number).ToListAsync();
        Assert.Equal(EmailKind.OrderConfirmation, Assert.Single(emails).Kind);
        Assert.False(await db.AdminNotifications.AnyAsync(n => n.Type == NotificationType.NewOrder && n.OrderNumber == result.Order!.Number));

        // A website order, for comparison, does both.
        var website = await _fx.PlaceOrderAsync();
        Assert.True(await db.OutboxEmails.AnyAsync(e => e.OrderNumber == website.Number && e.Kind == EmailKind.ShopNewOrder));
        Assert.True(await db.AdminNotifications.AnyAsync(n => n.Type == NotificationType.NewOrder && n.OrderNumber == website.Number));
    }

    [Fact]
    public async Task ABankTransferOrder_WaitsForPayment_AndCardIsRefused_WithNothingTaken()
    {
        var admin = await NewAdminAsync();
        var productId = await _fx.CreateProductAsync(price: 10000, stock: 5);
        await FillStaffCartAsync(admin, productId, 2);

        var card = await _fx.CreateOrderService().PlaceForAdminAsync(admin, null, await InputForAsync(admin, null, PaymentMethod.Card), "en");
        Assert.Equal(PlaceOrderStatus.InvalidPayment, card.Status);
        Assert.Equal(5, await _fx.GetStockAsync(productId));
        Assert.Equal(2, await _fx.CreateServices().Cart.CountAsync(new CartOwner(admin, null))); // the staff cart is kept

        var transfer = await _fx.CreateOrderService().PlaceForAdminAsync(admin, null, await InputForAsync(admin, null, PaymentMethod.BankTransfer), "en");
        Assert.Equal(PlaceOrderStatus.Placed, transfer.Status);
        Assert.Equal(OrderStatus.AwaitingPayment, transfer.Order!.Status);
        Assert.Equal(3, await _fx.GetStockAsync(productId));
    }

    [Fact]
    public async Task TheSameChecks_AsTheWebsite_PriceChange_OutOfStock_EmptyCart_BadDelivery_AndTheSameFormTwice()
    {
        var admin = await NewAdminAsync();
        var productId = await _fx.CreateProductAsync(price: 10000, stock: 3);
        var service = _fx.CreateOrderService();

        Assert.Equal(PlaceOrderStatus.EmptyCart, (await service.PlaceForAdminAsync(admin, null, await InputForAsync(admin, null, expected: 1), "en")).Status);

        await FillStaffCartAsync(admin, productId, 2);

        var changed = await service.PlaceForAdminAsync(admin, null, await InputForAsync(admin, null, expected: 1), "en");
        Assert.Equal(PlaceOrderStatus.PriceChanged, changed.Status);
        Assert.True(changed.NewTotal > 1);

        var badSlot = await InputForAsync(admin, null);
        badSlot.DeliverySlotId = 987654;
        Assert.Equal(PlaceOrderStatus.InvalidDelivery, (await service.PlaceForAdminAsync(admin, null, badSlot, "en")).Status);

        var badRegion = await InputForAsync(admin, null);
        badRegion.Region = "mars";
        Assert.Equal(PlaceOrderStatus.InvalidDelivery, (await service.PlaceForAdminAsync(admin, null, badRegion, "en")).Status);

        // The stock drops below the quantity after the admin saw the total: the cart lowers the quantity (like on the website),
        // so the total is different and the order is not placed until the admin confirms the new total.
        var seen = await InputForAsync(admin, null);
        await _fx.SetStockAsync(productId, 1);
        var lowered = await _fx.CreateOrderService().PlaceForAdminAsync(admin, null, seen, "en");
        Assert.Equal(PlaceOrderStatus.PriceChanged, lowered.Status);
        Assert.Equal(1, await _fx.GetStockAsync(productId)); // nothing was taken
        await _fx.SetStockAsync(productId, 3);

        var input = await InputForAsync(admin, null);
        var first = await _fx.CreateOrderService().PlaceForAdminAsync(admin, null, input, "en");
        var second = await _fx.CreateOrderService().PlaceForAdminAsync(admin, null, input, "en"); // the form sent twice
        Assert.Equal(PlaceOrderStatus.Placed, first.Status);
        Assert.Equal(PlaceOrderStatus.AlreadyPlaced, second.Status);
        Assert.Equal(first.Order!.Number, second.Order!.Number);
        Assert.Equal(1, await _fx.GetStockAsync(productId)); // 3 in stock, 2 taken by the order (the failed tries took nothing)
    }

    [Fact]
    public async Task TheStaffCartOfOneAdmin_IsNotTheCartOfAnother_NorTheCustomers()
    {
        var adminA = await NewAdminAsync();
        var adminB = await NewAdminAsync();
        var customer = await _fx.CreateUserAsync();
        var productId = await _fx.CreateProductAsync();

        await FillStaffCartAsync(adminA, productId);
        await _fx.FillCartAsync(customer, (productId, 3));

        var cart = _fx.CreateServices().Cart;
        Assert.Equal(1, await cart.CountAsync(new CartOwner(adminA, null)));
        Assert.Equal(0, await cart.CountAsync(new CartOwner(adminB, null)));
        Assert.Equal(3, await cart.CountAsync(new CartOwner(customer, null)));
    }

    [Fact]
    public async Task ManyPhoneOrdersForOneCustomerAtOnce_NeverGiveTheStandardDiscountMoreThanFiveTimes()
    {
        var admin = await NewAdminAsync();
        var customer = await _fx.CreateUserAsync();
        var productId = await _fx.CreateProductAsync(price: 10000, stock: 100);

        // Eight admins' staff carts, each with one item, all placing an order for the same customer at the same moment.
        var admins = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            var id = await _fx.CreateUserAsync();
            await FillStaffCartAsync(id, productId);
            admins.Add(id);
        }

        var inputs = new List<CheckoutInput>();
        foreach (var id in admins)
        {
            inputs.Add(await InputForAsync(id, customer));
        }

        var results = await Task.WhenAll(admins.Select((id, i) => Task.Run(() => _fx.CreateOrderService().PlaceForAdminAsync(id, customer, inputs[i], "en"))));

        // Each total was computed before the others existed, so some are "price changed" (the discount is gone): the placed ones
        // with the discount are never more than the five that were left.
        var placed = results.Where(r => r.Status == PlaceOrderStatus.Placed).ToList();
        Assert.Equal(5, placed.Count(r => r.Order!.CustomerDiscountApplied));
        Assert.NotNull(admin);
    }
}

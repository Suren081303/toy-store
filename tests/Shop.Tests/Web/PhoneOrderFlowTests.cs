using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Core.Entities;
using Shop.Core.Notifications;
using Shop.Infrastructure.Data;

namespace Shop.Tests.Web;

// The phone-order pages of the admin as a browser uses them: customer, items, delivery and payment, and what comes out of it.
[Collection("Web")]
public class PhoneOrderFlowTests : CheckoutTestBase
{
    public PhoneOrderFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    private static KeyValuePair<string, string> F(string key, string value) => new(key, value);

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword)).StatusCode);

        // The admin's staff cart is shared by all tests of this admin: every test starts with it empty.
        await PostFormListAsync(client, "/en/Admin/PhoneOrders", "/en/Admin/PhoneOrders/Clear", []);
        return client;
    }

    private async Task<T> InDbAsync<T>(Func<ShopDbContext, UserManager<ApplicationUser>, Task<T>> action)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ShopDbContext>(), scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }

    private Task<string> IdOfAsync(string email) => InDbAsync(async (_, users) => (await users.FindByEmailAsync(email))!.Id);

    private async Task<(HttpClient Client, string Email, string Id)> SignedInCustomerAsync()
    {
        var client = NewClient();
        var email = await SignedInUserAsync(client);
        return (client, email, await IdOfAsync(email));
    }

    // A product with enough stock and no discount of its own: its id, SKU and regular price.
    private async Task<(int Id, string Sku, decimal Price)> ProductAsync()
    {
        var id = await PlainProductAsync();
        var product = await InDbAsync((db, _) => db.Products.AsNoTracking().SingleAsync(p => p.Id == id));
        return (id, product.Sku, product.Price);
    }

    private static string Url(string path, string culture = "en") => $"/{culture}/Admin/PhoneOrders{path}";

    private async Task AddItemAsync(HttpClient admin, string customer, int productId, int quantity = 1, string pq = "x")
    {
        var response = await PostFormListAsync(admin, Url("/Items?customer=" + customer), Url("/AddItem"),
            [F("customer", customer), F("pq", pq), F("productId", productId.ToString()), F("quantity", quantity.ToString())]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    // The values of step 3, read from the page: the form fields to send.
    private static Dictionary<string, string> ReadDetailsPage(string html)
    {
        string Hidden(string name) => WebUtility.HtmlDecode(Regex.Match(html, $"name=\"{name}\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);

        return new Dictionary<string, string>
        {
            ["CheckoutId"] = Hidden("CheckoutId"),
            ["Customer"] = Hidden("Customer"),
            ["ExpectedTotal"] = Hidden("ExpectedTotal"),
            ["DeliveryDate"] = Regex.Match(html, "<option value=\"(\\d{4}-\\d{2}-\\d{2})\"[^>]*selected").Groups[1].Value,
            ["DeliverySlotId"] = Regex.Match(html, "<option value=\"(\\d+)\"[^>]*selected").Groups[1].Value
        };
    }

    // Fills the contact and address, and posts the order.
    private async Task<HttpResponseMessage> PlaceAsync(HttpClient admin, string customer, Action<Dictionary<string, string>>? change = null)
    {
        var page = await admin.GetStringAsync(Url("/Details?customer=" + customer));
        var fields = ReadDetailsPage(page);
        fields["CustomerName"] = "Phone Customer";
        fields["Phone"] = "091 765432";
        fields["Email"] = string.Empty;
        fields["Region"] = "yerevan";
        fields["City"] = "Yerevan";
        fields["Street"] = "Tumanyan";
        fields["Building"] = "5";
        fields["Apartment"] = "12";
        fields["Comment"] = "Phone order";
        fields["PaymentMethod"] = "CashOnDelivery";
        fields["Language"] = "en";
        fields["AcceptTerms"] = "true";
        change?.Invoke(fields);

        return await PostFormListAsync(admin, Url("/Details?customer=" + customer), Url("/Place"), fields);
    }

    private static int NumberFrom(HttpResponseMessage placed)
    {
        Assert.Equal(HttpStatusCode.Redirect, placed.StatusCode);
        var match = Regex.Match(placed.Headers.Location!.OriginalString, @"/Orders/Details\?number=(\d+)");
        Assert.True(match.Success, "Unexpected redirect: " + placed.Headers.Location);
        return int.Parse(match.Groups[1].Value);
    }

    // ---- Who may use it ----

    [Fact]
    public async Task VisitorsAndCustomers_AreRefused_OnEveryPageAndAction()
    {
        var customer = NewClient();
        await SignedInUserAsync(customer);

        foreach (var url in new[] { Url(""), Url("/Items?customer=guest"), Url("/Details?customer=guest") })
        {
            Assert.Contains("/Account/Login", (await NewClient().GetAsync(url)).Headers.Location!.ToString());
            Assert.Contains("/Account/AccessDenied", (await customer.GetAsync(url)).Headers.Location!.ToString());
        }

        foreach (var action in new[] { "AddItem", "SetQuantity", "RemoveItem", "Clear", "Place" })
        {
            var response = await PostFormListAsync(customer, "/en/Orders/Lookup", Url("/" + action), [F("customer", "guest"), F("productId", "1"), F("quantity", "1")]);
            Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());
        }
    }

    // ---- Step 1: the customer ----

    [Fact]
    public async Task Step1_FindsARegisteredCustomer_ByEmail_AndNeverAnAdmin_AndOffersACustomerWithoutAnAccount()
    {
        var (_, email, id) = await SignedInCustomerAsync();
        var admin = await AdminClientAsync();

        var found = await admin.GetStringAsync(Url("?q=" + Uri.EscapeDataString(email)));
        Assert.Contains(email, found);
        Assert.Contains($"href=\"/en/Admin/PhoneOrders/Items?customer={id}\"", found);
        Assert.Contains("href=\"/en/Admin/PhoneOrders/Items?customer=guest\"", found);

        Assert.Contains("No registered customer found.", await admin.GetStringAsync(Url("?q=" + Uri.EscapeDataString("nobody-" + Guid.NewGuid().ToString("N")))));
        Assert.Contains("No registered customer found.", await admin.GetStringAsync(Url("?q=" + Uri.EscapeDataString(ShopWebFactory.AdminEmail)))); // admins are not customers
    }

    // ---- Step 2: the items ----

    [Fact]
    public async Task Step2_FindsAProductBySku_AddsIt_ChangesTheQuantity_RemovesIt_AndClears()
    {
        var (productId, sku, price) = await ProductAsync();
        var admin = await AdminClientAsync();
        var items = Url("/Items?customer=guest");

        Assert.Contains("No items yet", await admin.GetStringAsync(items));

        var search = await admin.GetStringAsync(Url($"/Items?customer=guest&pq={Uri.EscapeDataString(sku)}"));
        Assert.Contains(sku, search);
        Assert.Contains("action=\"/en/Admin/PhoneOrders/AddItem\"", search);

        await AddItemAsync(admin, "guest", productId, 2, sku);
        var cart = WebUtility.HtmlDecode(await admin.GetStringAsync(items));
        Assert.Contains(Shop.Web.Money.Format(price * 2), cart);
        Assert.Contains("Continue to delivery and payment", cart);
        Assert.DoesNotContain("personal discount", cart); // no account: no discount

        await PostFormListAsync(admin, items, Url("/SetQuantity"), [F("customer", "guest"), F("productId", productId.ToString()), F("quantity", "3")]);
        Assert.Contains(Shop.Web.Money.Format(price * 3), WebUtility.HtmlDecode(await admin.GetStringAsync(items)));

        await PostFormListAsync(admin, items, Url("/RemoveItem"), [F("customer", "guest"), F("productId", productId.ToString())]);
        Assert.Contains("No items yet", await admin.GetStringAsync(items));

        await AddItemAsync(admin, "guest", productId);
        await PostFormListAsync(admin, items, Url("/Clear"), []);
        Assert.Contains("No items yet", await admin.GetStringAsync(items));
    }

    [Fact]
    public async Task Step2_APersonalDiscount_IsInThePrices_AndTheNote()
    {
        var (_, email, id) = await SignedInCustomerAsync();
        var (productId, _, price) = await ProductAsync();
        var admin = await AdminClientAsync();
        await PostFormListAsync(admin, $"/en/Admin/Customers?Query={Uri.EscapeDataString(email)}", "/en/Admin/Customers/Discount", [F("id", id), F("percent", "20")]);

        await AddItemAsync(admin, id, productId, 2);

        var html = WebUtility.HtmlDecode(await admin.GetStringAsync(Url("/Items?customer=" + id)));
        Assert.Contains("Order for Test User", html);
        Assert.Contains("Your personal discount of 20% is applied where it is lower.", html);
        Assert.Contains(Shop.Web.Money.Format(Math.Round(price * 0.8m, 0, MidpointRounding.AwayFromZero) * 2), html);
        Assert.Contains("(-20%)", html);
    }

    [Fact]
    public async Task Step2_AWrongProduct_OrTooMany_ShowsAClearMessage_AndAnUnknownCustomerIsNotFound()
    {
        var (productId, _, _) = await ProductAsync();
        var admin = await AdminClientAsync();
        var items = Url("/Items?customer=guest");

        await PostFormListAsync(admin, items, Url("/AddItem"), [F("customer", "guest"), F("productId", "99999999"), F("quantity", "1")]);
        Assert.Contains("This product is not available", await admin.GetStringAsync(items));

        await PostFormListAsync(admin, items, Url("/AddItem"), [F("customer", "guest"), F("productId", productId.ToString()), F("quantity", "500")]);
        Assert.Contains("Only", await admin.GetStringAsync(items)); // held back by the stock or the per-product limit

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(Url("/Items?customer=no-such-id"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(Url("/Items?customer=" + await IdOfAsync(ShopWebFactory.AdminEmail)))).StatusCode); // an admin account
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(Url("/Details?customer=no-such-id"))).StatusCode);
    }

    [Fact]
    public async Task Step3_WithAnEmptyCart_GoesBackToTheItems_WithAMessage()
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync(Url("/Details?customer=guest"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/PhoneOrders/Items", response.Headers.Location!.ToString());
        Assert.Contains("Add at least one item first.", await admin.GetStringAsync(Url("/Items?customer=guest")));
    }

    // ---- The whole order for a registered customer ----

    [Fact]
    public async Task ARegisteredCustomersOrder_UsesTheirDiscount_IsInTheirOrders_IsMarkedAsPhone_AndEmailsOnlyTheCustomer()
    {
        var (customerClient, email, id) = await SignedInCustomerAsync();
        var (productId, _, price) = await ProductAsync();
        var admin = await AdminClientAsync();
        await PostFormListAsync(admin, $"/en/Admin/Customers?Query={Uri.EscapeDataString(email)}", "/en/Admin/Customers/Discount", [F("id", id), F("percent", "20")]);
        await AddItemAsync(admin, id, productId, 2);

        // Step 3 is prefilled from the account.
        var page = await admin.GetStringAsync(Url("/Details?customer=" + id));
        Assert.Contains("value=\"Test User\"", page);
        Assert.Contains($"value=\"{email}\"", page);
        Assert.Contains("name=\"PaymentMethod\"", page);
        Assert.DoesNotContain("Online card payment", page);
        Assert.Contains("The customer agreed to the terms of use", page);

        var placed = await PlaceAsync(admin, id, f => { f["Email"] = email; f["Language"] = "ru"; });
        var number = NumberFrom(placed);

        // The admin lands on the order, with the bar and the phone mark.
        var details = WebUtility.HtmlDecode(await admin.GetStringAsync($"/en/Admin/Orders/Details?number={number}"));
        Assert.Contains($"Phone order #{number} placed.", details);
        Assert.Contains("Phone order", details);
        Assert.Contains(ShopWebFactory.AdminEmail, details);

        var stored = await InDbAsync((db, _) => db.Orders.AsNoTracking().Include(o => o.Items).SingleAsync(o => o.Number == number));
        Assert.Equal(id, stored.UserId);
        Assert.Equal(await IdOfAsync(ShopWebFactory.AdminEmail), stored.CreatedByUserId);
        Assert.Equal(20, stored.CustomerDiscountPercent);
        Assert.Equal("ru", stored.Culture);
        Assert.Equal("+37491765432", stored.Phone);
        Assert.Equal(Math.Round(price * 0.8m, 0, MidpointRounding.AwayFromZero), stored.Items.Single().UnitPrice);
        Assert.Equal(2, stored.Items.Single().Quantity);

        // The customer sees it in their own orders.
        Assert.Contains($"#{number}", await customerClient.GetStringAsync("/en/Orders"));
        Assert.Equal(HttpStatusCode.OK, (await customerClient.GetAsync($"/en/Orders/Details?number={number}")).StatusCode);

        // The staff cart is empty again.
        Assert.Contains("No items yet", await admin.GetStringAsync(Url("/Items?customer=" + id)));

        // The list marks it; the customer gets one email in Russian; the shop gets none and there is no "new order" bell.
        Assert.Contains("Phone order", await admin.GetStringAsync($"/en/Admin/Orders?Query={number}"));
        await Fixture.Factory.FlushEmailsAsync();
        var sent = Assert.Single(Fixture.Factory.Emails.To(email).Where(e => e.Subject.Contains(number.ToString())));
        Assert.Contains("lang=\"ru\"", sent.Html);
        Assert.False(await InDbAsync((db, _) => db.OutboxEmails.AnyAsync(e => e.OrderNumber == number && e.Kind == EmailKind.ShopNewOrder)));
        Assert.False(await InDbAsync((db, _) => db.AdminNotifications.AnyAsync(n => n.OrderNumber == number && n.Type == NotificationType.NewOrder)));
    }

    // ---- The whole order for a customer without an account and without an email ----

    [Fact]
    public async Task ACustomerWithoutAnAccount_AndNoEmail_CanOrder_PaysFullPrice_AndNoCustomerEmailIsSent()
    {
        var (productId, _, price) = await ProductAsync();
        var admin = await AdminClientAsync();
        await AddItemAsync(admin, "guest", productId, 1);

        var number = NumberFrom(await PlaceAsync(admin, "guest"));

        var stored = await InDbAsync((db, _) => db.Orders.AsNoTracking().Include(o => o.Items).SingleAsync(o => o.Number == number));
        Assert.Null(stored.UserId);
        Assert.Equal(string.Empty, stored.Email);
        Assert.Equal(price, stored.Items.Single().UnitPrice);
        Assert.Equal(0, stored.CustomerDiscountPercent);

        // The confirmation is queued, then skipped with the reason; nothing is sent.
        var sentBefore = Fixture.Factory.Emails.CountTo(string.Empty);
        await Fixture.Factory.FlushEmailsAsync();
        var row = await InDbAsync((db, _) => db.OutboxEmails.AsNoTracking().SingleAsync(e => e.OrderNumber == number && e.Kind == EmailKind.OrderConfirmation));
        Assert.Equal(OutboxStatus.Skipped, row.Status);
        Assert.Equal("The order has no email address.", row.LastError);
        Assert.Equal(sentBefore, Fixture.Factory.Emails.CountTo(string.Empty));

        // Later status emails are skipped too, and the Emails page says so.
        await PostFormListAsync(admin, $"/en/Admin/Orders/Details?number={number}", "/en/Admin/Orders/ChangeStatus", [F("number", number.ToString()), F("status", "Packed")]);
        await Fixture.Factory.FlushEmailsAsync();
        Assert.Equal(OutboxStatus.Skipped, (await InDbAsync((db, _) => db.OutboxEmails.AsNoTracking().SingleAsync(e => e.OrderNumber == number && e.Kind == EmailKind.OrderPacked))).Status);
        Assert.Contains("Not sent (no email address)", WebUtility.HtmlDecode(await admin.GetStringAsync("/en/Admin/Emails")));

        // The slip works: the courier knows what to collect, and the phone.
        var slip = WebUtility.HtmlDecode(await admin.GetStringAsync($"/en/Admin/Orders/Slip?number={number}"));
        Assert.Contains("+37491765432", slip);
        Assert.Contains("Phone order", slip);
    }

    [Fact]
    public async Task ABankTransferOrder_WaitsForPayment_AndCanBeEditedLater()
    {
        var (productId, _, _) = await ProductAsync();
        var admin = await AdminClientAsync();
        await AddItemAsync(admin, "guest", productId);

        var number = NumberFrom(await PlaceAsync(admin, "guest", f => f["PaymentMethod"] = "BankTransfer"));

        var details = await admin.GetStringAsync($"/en/Admin/Orders/Details?number={number}");
        Assert.Contains("Bank transfer received", details);
        Assert.Contains("Waiting for the bank transfer", WebUtility.HtmlDecode(details));
        Assert.Contains($"/en/Admin/Orders/Edit?number={number}", details);
    }

    // ---- Checks ----

    [Fact]
    public async Task Place_RefusesMissingTerms_BadValues_AndCard_AndCreatesNoOrder()
    {
        var (productId, _, _) = await ProductAsync();
        var admin = await AdminClientAsync();
        await AddItemAsync(admin, "guest", productId);
        var before = await InDbAsync((db, _) => db.Orders.CountAsync());

        async Task<string> Try(Action<Dictionary<string, string>> change)
        {
            var response = await PlaceAsync(admin, "guest", change);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the form again
            return WebUtility.HtmlDecode(await BodyAsync(response));
        }

        Assert.Contains("field-validation-error", await Try(f => f.Remove("AcceptTerms")));
        Assert.Contains("field-validation-error", await Try(f => f["Phone"] = "12"));
        Assert.Contains("field-validation-error", await Try(f => f["CustomerName"] = ""));
        Assert.Contains("field-validation-error", await Try(f => f["Email"] = "not-an-email"));
        Assert.Contains("Choose a region.", await Try(f => f["Region"] = "mars"));
        Assert.Contains("Choose a delivery date from the list.", await Try(f => f["DeliveryDate"] = "2000-01-01"));
        Assert.Contains("Choose a delivery time.", await Try(f => f["DeliverySlotId"] = "987654"));
        Assert.Contains("This payment method is not available yet.", await Try(f => f["PaymentMethod"] = "Card"));

        // What was typed is still in the form.
        var kept = await Try(f => { f["Street"] = "Kept Street"; f.Remove("AcceptTerms"); });
        Assert.Contains("value=\"Kept Street\"", kept);

        Assert.Equal(before, await InDbAsync((db, _) => db.Orders.CountAsync()));
    }

    [Fact]
    public async Task Place_AChangedTotal_AsksToConfirmTheNewOne_AndTheSameFormTwiceMakesOneOrder()
    {
        var (productId, _, price) = await ProductAsync();
        var admin = await AdminClientAsync();
        await AddItemAsync(admin, "guest", productId);
        var before = await InDbAsync((db, _) => db.Orders.CountAsync());

        // The total on the page is old: the price "changed".
        var stale = await PlaceAsync(admin, "guest", f => f["ExpectedTotal"] = "1");
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        var html = WebUtility.HtmlDecode(await BodyAsync(stale));
        Assert.Contains("The total changed to", html);
        Assert.Equal(before, await InDbAsync((db, _) => db.Orders.CountAsync()));

        // The page now holds the real total; pressing the button again places it, and a second press finds the same order.
        var again = ReadDetailsPage(html);
        Assert.NotEqual("1", again["ExpectedTotal"]);

        var page = await admin.GetStringAsync(Url("/Details?customer=guest"));
        var fields = ReadDetailsPage(page);
        var first = await PlaceAsync(admin, "guest", f => f["CheckoutId"] = fields["CheckoutId"]);
        var number = NumberFrom(first);

        await AddItemAsync(admin, "guest", productId); // a new cart, but the old form is sent again
        var second = await PlaceAsync(admin, "guest", f => f["CheckoutId"] = fields["CheckoutId"]);
        Assert.Equal(number, NumberFrom(second));
        Assert.Equal(before + 1, await InDbAsync((db, _) => db.Orders.CountAsync()));
        Assert.True(price > 0);
    }

    [Fact]
    public async Task ABlockedCustomer_ShowsAWarning_ButCanStillBeOrderedFor()
    {
        var (_, email, id) = await SignedInCustomerAsync();
        var (productId, _, _) = await ProductAsync();
        var admin = await AdminClientAsync();
        await PostFormListAsync(admin, $"/en/Admin/Customers?Query={Uri.EscapeDataString(email)}", "/en/Admin/Customers/Block", [F("id", id), F("email", email)]);
        await AddItemAsync(admin, id, productId);

        Assert.Contains("Blocked on the website. You can still make this order.", WebUtility.HtmlDecode(await admin.GetStringAsync(Url("/Items?customer=" + id))));
        Assert.True(NumberFrom(await PlaceAsync(admin, id)) > 0);
    }

    // ---- Languages ----

    [Theory]
    [InlineData("hy")]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task ThePhoneOrderPages_OpenInEveryLanguage_WithNoUntranslatedKeys(string culture)
    {
        var (_, email, id) = await SignedInCustomerAsync();
        var (productId, sku, _) = await ProductAsync();
        var admin = await AdminClientAsync();
        await AddItemAsync(admin, id, productId, 2);

        foreach (var url in new[] { Url($"?q={Uri.EscapeDataString(email)}", culture), Url($"/Items?customer={id}&pq={Uri.EscapeDataString(sku)}", culture), Url($"/Details?customer={id}", culture) })
        {
            var response = await admin.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await BodyAsync(response);
            var text = Regex.Replace(Regex.Replace(html, "<script[\\s\\S]*?</script>", " "), "<[^>]+>", " ");
            Assert.Empty(Regex.Matches(text, @"\b(Admin|Val|Status|Pay|Label|PO|Cart|Checkout|Region)_[A-Za-z_]+\b").Select(m => m.Value).Distinct());
        }
    }
}

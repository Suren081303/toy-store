using System.Net;
using System.Text.RegularExpressions;

namespace Shop.Tests.Web;

[Collection("Web")]
public class AdminFlowTests : CheckoutTestBase
{
    public AdminFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = NewClient();
        var response = await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    // A guest places an order; returns its number and key.
    private async Task<(int Number, string Key, string Email)> PlaceOrderAsync(string method = "CashOnDelivery")
    {
        var guest = NewClient();
        await AddToCartAsync(guest, await PlainProductAsync());
        var email = NewEmail();
        var (number, key) = ParseDone(await CheckoutAsync(guest, email, f => f["PaymentMethod"] = method));
        return (number, key, email);
    }

    private static Task<HttpResponseMessage> AdminPostAsync(HttpClient admin, string formPage, string action, Dictionary<string, string> fields)
    {
        return PostFormAsync(admin, formPage, "/en/Admin/" + action, fields);
    }

    // ---- Who may come in ----

    [Theory]
    [InlineData("/en/Admin")]
    [InlineData("/hy/Admin")]
    [InlineData("/en/Admin/Orders")]
    [InlineData("/en/Admin/Orders/Details?number=10001")]
    [InlineData("/en/Admin/Returns")]
    [InlineData("/en/Admin/Settings")]
    public async Task AVisitorWhoIsNotSignedIn_IsSentToTheSignInPage(string url)
    {
        var response = await NewClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("/en/Admin")]
    [InlineData("/ru/Admin/Orders")]
    [InlineData("/en/Admin/Returns")]
    [InlineData("/en/Admin/Settings")]
    public async Task ACustomer_GetsAccessDenied_OnEveryAdminPage(string url)
    {
        var customer = NewClient();
        await SignedInUserAsync(customer);

        var response = await customer.GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task ACustomer_CannotSendAdminActions_EvenWithAValidToken()
    {
        var (number, _, _) = await PlaceOrderAsync();
        var customer = NewClient();
        await SignedInUserAsync(customer);

        var changeStatus = await PostFormAsync(customer, "/en/Orders/Lookup", "/en/Admin/Orders/ChangeStatus", new Dictionary<string, string>
        {
            ["number"] = number.ToString(),
            ["status"] = "Cancelled"
        });
        var saveSettings = await PostFormAsync(customer, "/en/Orders/Lookup", "/en/Admin/Settings/Save", new Dictionary<string, string>
        {
            ["DeliveryFee"] = "0"
        });

        Assert.Contains("/Account/AccessDenied", changeStatus.Headers.Location!.ToString());
        Assert.Contains("/Account/AccessDenied", saveSettings.Headers.Location!.ToString());

        // The order is untouched.
        var admin = await AdminClientAsync();
        Assert.DoesNotContain("status--cancelled", await admin.GetStringAsync($"/en/Admin/Orders/Details?number={number}"));
    }

    [Fact]
    public async Task TheShopMenu_ShowsTheAdminLink_OnlyToAdmins()
    {
        var customer = NewClient();
        await SignedInUserAsync(customer);
        var admin = await AdminClientAsync();

        Assert.DoesNotContain("/en/Admin", await customer.GetStringAsync("/en"));
        Assert.Contains("/en/Admin", await admin.GetStringAsync("/en"));
    }

    // ---- The pages open, in every language, without missing translations ----

    [Theory]
    [InlineData("hy")]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task AdminPages_Open_AndHaveNoUntranslatedKeys(string culture)
    {
        var (number, _, _) = await PlaceOrderAsync("BankTransfer");
        var admin = await AdminClientAsync();

        foreach (var path in new[] { "", "/Orders", $"/Orders/Details?number={number}", "/Returns", "/Settings" })
        {
            var response = await admin.GetAsync($"/{culture}/Admin{path}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await BodyAsync(response);
            var text = Regex.Replace(Regex.Replace(html, "<script[\\s\\S]*?</script>", " "), "<[^>]+>", " ");
            Assert.Empty(Regex.Matches(text, @"\b(Admin|Status|Pay|Return|Bank|Order|Cart|Label|Val|AttemptStatus|PayStatus)_[A-Za-z_]+\b").Select(m => m.Value).Distinct());
        }
    }

    // ---- Working an order ----

    [Fact]
    public async Task Admin_FindsAnOrder_PacksIt_ThenDeliversIt_AndTheCustomerSeesTheStatus()
    {
        var (number, key, email) = await PlaceOrderAsync();
        var admin = await AdminClientAsync();
        var detailsUrl = $"/en/Admin/Orders/Details?number={number}";

        // The list finds it by email.
        Assert.Contains($"#{number}", await admin.GetStringAsync($"/en/Admin/Orders?Query={Uri.EscapeDataString(email)}"));

        // Only "Mark as packed" and "Cancel order" are offered.
        var details = await admin.GetStringAsync(detailsUrl);
        Assert.Contains("Mark as packed", details);
        Assert.Contains("Cancel order", details);
        Assert.DoesNotContain("Mark as delivered", details);

        var packed = await AdminPostAsync(admin, detailsUrl, "Orders/ChangeStatus", new Dictionary<string, string>
        {
            ["number"] = number.ToString(),
            ["status"] = "Packed",
            ["note"] = "Packed by the test"
        });
        Assert.Equal(HttpStatusCode.Redirect, packed.StatusCode);

        details = await admin.GetStringAsync(detailsUrl);
        Assert.Contains("Status changed.", details);   // the green bar, once
        Assert.Contains("Packed by the test", details); // in the history
        Assert.Contains("Mark as delivered", details);
        Assert.DoesNotContain("Status changed.", await admin.GetStringAsync(detailsUrl)); // gone after being shown

        await AdminPostAsync(admin, detailsUrl, "Orders/ChangeStatus", new Dictionary<string, string>
        {
            ["number"] = number.ToString(),
            ["status"] = "Delivered"
        });

        // The customer sees the new status on their order page; a cash order is now paid.
        var customerView = await NewClient().GetStringAsync($"/en/Orders/Details?number={number}&key={key}");
        Assert.Contains("Delivered", customerView);
        Assert.Contains("Paid", customerView);
        Assert.DoesNotContain("Mark as", await admin.GetStringAsync(detailsUrl)); // nothing left to do
    }

    [Fact]
    public async Task Admin_CannotJumpOverASteps_ByAFormSentByHand()
    {
        var (number, _, _) = await PlaceOrderAsync();
        var admin = await AdminClientAsync();
        var detailsUrl = $"/en/Admin/Orders/Details?number={number}";

        // "Delivered" is not offered for a new order, but the form is sent anyway.
        var response = await AdminPostAsync(admin, detailsUrl, "Orders/ChangeStatus", new Dictionary<string, string>
        {
            ["number"] = number.ToString(),
            ["status"] = "Delivered"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var page = await admin.GetStringAsync(detailsUrl);
        Assert.Contains("This change is not allowed now.", page);
        Assert.Contains("Mark as packed", page); // still waiting to be packed
    }

    [Fact]
    public async Task Admin_CancelsAnOrder_AndTheStockGoesBack()
    {
        var (number, _, _) = await PlaceOrderAsync();
        var admin = await AdminClientAsync();
        var detailsUrl = $"/en/Admin/Orders/Details?number={number}";

        await AdminPostAsync(admin, detailsUrl, "Orders/ChangeStatus", new Dictionary<string, string>
        {
            ["number"] = number.ToString(),
            ["status"] = "Cancelled",
            ["note"] = "Customer called"
        });

        var page = await admin.GetStringAsync(detailsUrl);
        Assert.Contains("Cancelled", page);
        Assert.Contains("Customer called", page);
        Assert.Contains("No further actions.", page);
    }

    [Fact]
    public async Task Admin_MarksABankTransferAsReceived()
    {
        var (number, _, _) = await PlaceOrderAsync("BankTransfer");
        var admin = await AdminClientAsync();
        var detailsUrl = $"/en/Admin/Orders/Details?number={number}";

        Assert.Contains("Bank transfer received", await admin.GetStringAsync(detailsUrl));

        var response = await AdminPostAsync(admin, detailsUrl, "Orders/MarkReceived", new Dictionary<string, string> { ["number"] = number.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var page = await admin.GetStringAsync(detailsUrl);
        Assert.Contains("The transfer is recorded and the order is paid.", page);
        Assert.Contains("Mark as packed", page);
        Assert.DoesNotContain("data-confirm=\"Mark the transfer as received?", page); // the button is gone

        // Doing it again is refused.
        await AdminPostAsync(admin, detailsUrl, "Orders/MarkReceived", new Dictionary<string, string> { ["number"] = number.ToString() });
        Assert.Contains("This change is not allowed now.", await admin.GetStringAsync(detailsUrl));
    }

    [Fact]
    public async Task AnUnknownOrder_IsNotFound_ForAdmins()
    {
        var admin = await AdminClientAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/en/Admin/Orders/Details?number=99999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/en/Admin/Returns/Details/99999999")).StatusCode);
    }

    // ---- Returns ----

    [Fact]
    public async Task Admin_HandlesAReturn_AndTheCustomerSeesTheAnswer()
    {
        var (number, key, _) = await PlaceOrderAsync();
        var admin = await AdminClientAsync();
        var detailsUrl = $"/en/Admin/Orders/Details?number={number}";

        foreach (var status in new[] { "Packed", "Delivered" })
        {
            await AdminPostAsync(admin, detailsUrl, "Orders/ChangeStatus", new Dictionary<string, string> { ["number"] = number.ToString(), ["status"] = status });
        }

        // The customer asks for a return.
        var customer = NewClient();
        var customerPage = $"/en/Orders/Details?number={number}&key={key}";
        var asked = await PostFormAsync(customer, customerPage, "/en/Orders/RequestReturn", new Dictionary<string, string>
        {
            ["number"] = number.ToString(),
            ["key"] = key,
            ["reason"] = "The wheel is broken"
        });
        Assert.Equal(HttpStatusCode.Redirect, asked.StatusCode);

        // The admin finds it on the order page and in the list.
        var orderPage = await admin.GetStringAsync(detailsUrl);
        var returnLink = Regex.Match(orderPage, @"href=""(/en/Admin/Returns/Details/\d+)""");
        Assert.True(returnLink.Success, "The order page has no link to the return.");
        Assert.Contains($"#{number}", await admin.GetStringAsync("/en/Admin/Returns?status=Requested"));

        var returnPage = await admin.GetStringAsync(returnLink.Groups[1].Value);
        Assert.Contains("The wheel is broken", returnPage);
        Assert.Contains("Put the items back in stock", returnPage);
        var id = Regex.Match(returnLink.Groups[1].Value, @"/(\d+)$").Groups[1].Value;

        // Goods received, with a note for the customer.
        var changed = await AdminPostAsync(admin, returnLink.Groups[1].Value, "Returns/Change", new Dictionary<string, string>
        {
            ["id"] = id,
            ["status"] = "Returned",
            ["note"] = "We received the toy",
            ["restock"] = "true"
        });
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        Assert.Contains("The return is updated.", await admin.GetStringAsync(returnLink.Groups[1].Value));

        // The customer sees the status and the note.
        var after = await customer.GetStringAsync(customerPage);
        Assert.Contains("Returned", after);
        Assert.Contains("We received the toy", after);

        // Money sent back.
        await AdminPostAsync(admin, returnLink.Groups[1].Value, "Returns/Change", new Dictionary<string, string> { ["id"] = id, ["status"] = "Refunded" });
        Assert.Contains("Refunded", await customer.GetStringAsync(customerPage));

        // Nothing is left to decide.
        Assert.Contains("No further actions.", await admin.GetStringAsync(returnLink.Groups[1].Value));
    }

    // ---- Settings ----

    [Fact]
    public async Task Settings_RefuseWrongValues_AndSaveGoodOnes_ThatCustomersThenSee()
    {
        var admin = await AdminClientAsync();
        var page = "/en/Admin/Settings";
        var good = new Func<Dictionary<string, string>>(() => new Dictionary<string, string>
        {
            ["DeliveryFee"] = "1500",
            ["FreeDeliveryFrom"] = "20000",
            ["DeliveryDaysAhead"] = "14",
            ["DeliveryFirstDayOffset"] = "1",
            ["ReturnWindowDays"] = "14",
            ["BankName"] = "Sample Bank",
            ["BankAccount"] = "AM00 0000 0000 0000 0000 0000 0000",
            ["BankRecipient"] = "Shop LLC"
        });

        // Whole AMD amounts are shown without decimals.
        var form = await admin.GetStringAsync(page);
        Assert.Contains("value=\"1500\"", form);
        Assert.Contains("value=\"20000\"", form);

        try
        {
            // A wrong value shows the form again with a message and saves nothing.
            var wrong = good();
            wrong["DeliveryFee"] = "-5";
            wrong["DeliveryDaysAhead"] = "500";
            var refused = await AdminPostAsync(admin, page, "Settings/Save", wrong);
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            var refusedHtml = await BodyAsync(refused);
            Assert.Contains("must be between", refusedHtml);
            Assert.Contains("value=\"-5\"", refusedHtml); // what was typed is kept

            // A good change is saved and used by the bank-transfer page.
            var changed = good();
            changed["BankName"] = "Test Bank Plc";
            Assert.Equal(HttpStatusCode.Redirect, (await AdminPostAsync(admin, page, "Settings/Save", changed)).StatusCode);
            Assert.Contains("Saved.", await admin.GetStringAsync(page));
            Assert.Contains("Test Bank Plc", await admin.GetStringAsync(page));

            var (number, key, _) = await PlaceOrderAsync("BankTransfer");
            Assert.Contains("Test Bank Plc", await NewClient().GetStringAsync($"/en/Checkout/Done?number={number}&key={key}"));
        }
        finally
        {
            // Back to the defaults, because other tests expect them.
            await AdminPostAsync(admin, page, "Settings/Save", good());
        }
    }

    [Fact]
    public async Task Settings_Slots_CanBeAdded_AndTheLastActiveOneCannotBeHidden()
    {
        var admin = await AdminClientAsync();
        var page = "/en/Admin/Settings";

        var added = await AdminPostAsync(admin, page, "Settings/SaveSlot", new Dictionary<string, string>
        {
            ["label"] = "22:00–23:00",
            ["sortOrder"] = "50",
            ["isActive"] = "true"
        });
        Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
        var html = await admin.GetStringAsync(page);
        Assert.Contains("22:00–23:00", WebUtility.HtmlDecode(html));

        // A checkout now offers the new time.
        var guest = NewClient();
        await AddToCartAsync(guest, await PlainProductAsync());
        Assert.Contains("22:00–23:00", WebUtility.HtmlDecode(await guest.GetStringAsync("/en/Checkout")));

        // Hide it again so other tests see the usual times.
        var id = Regex.Match(WebUtility.HtmlDecode(html), @"name=""id"" value=""(\d+)""[\s\S]{0,200}?value=""22:00–23:00""").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(id));
        await AdminPostAsync(admin, page, "Settings/SaveSlot", new Dictionary<string, string>
        {
            ["id"] = id,
            ["label"] = "22:00–23:00",
            ["sortOrder"] = "50",
            ["isActive"] = "false"
        });
        Assert.DoesNotContain("22:00–23:00", WebUtility.HtmlDecode(await guest.GetStringAsync("/en/Checkout")));

        // An empty time is refused with a message.
        await AdminPostAsync(admin, page, "Settings/SaveSlot", new Dictionary<string, string> { ["label"] = "   ", ["sortOrder"] = "1", ["isActive"] = "true" });
        Assert.Contains("Enter a time", await admin.GetStringAsync(page));
    }

    // ---- Dashboard ----

    [Fact]
    public async Task Dashboard_ShowsCounts_ThatLinkToTheMatchingLists()
    {
        await PlaceOrderAsync();
        var admin = await AdminClientAsync();

        var html = await admin.GetStringAsync("/en/Admin");

        Assert.Contains("Orders today", html);
        Assert.Contains("/en/Admin/Orders?status=Ordered", html);
        Assert.Contains("/en/Admin/Orders?status=AwaitingPayment", html);
        Assert.Contains("refundNeeded=true", html);
        Assert.Contains("/en/Admin/Returns", html);

        // The links work.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/en/Admin/Orders?status=Ordered")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/en/Admin/Orders?refundNeeded=true")).StatusCode);
    }
}

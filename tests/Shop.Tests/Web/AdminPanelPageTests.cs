using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Core.Entities;
using Shop.Core.Orders;
using Shop.Infrastructure.Data;

namespace Shop.Tests.Web;

// The admin pages as the browser receives them: the header links go where they say, and the order buttons are on the page.
[Collection("Web")]
public class AdminPanelPageTests : CheckoutTestBase
{
    public AdminPanelPageTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword)).StatusCode);
        return client;
    }

    private async Task<(int Number, string Key)> PlaceOrderAsync(HttpClient? buyer = null, string method = "CashOnDelivery")
    {
        var client = buyer ?? NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        return ParseDone(await CheckoutAsync(client, NewEmail(), f => f["PaymentMethod"] = method));
    }

    private async Task SetStatusAsync(int number, OrderStatus status)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        await db.Orders.Where(o => o.Number == number).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, status));
    }

    // The part of the page that holds the row of this order in the orders list.
    private static string RowOf(string listHtml, int number)
    {
        var start = listHtml.IndexOf($"number={number}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Order {number} is not in the list.");
        var end = listHtml.IndexOf("</tr>", start, StringComparison.Ordinal);
        return listHtml[start..end];
    }

    // ---- The header ----

    [Theory]
    [InlineData("hy")]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task TheHeaderLinks_GoToTheShopAndTheDashboard_AndNoLinkOrFormIsEmpty(string culture)
    {
        var (number, _) = await PlaceOrderAsync();
        var admin = await AdminClientAsync();

        foreach (var path in new[] { "", "/Orders", $"/Orders/Details?number={number}", "/Returns", "/Analytics", "/Products", "/Categories", "/AgeGroups", "/Admins", "/Audit", "/Emails", "/Settings", "/Notifications" })
        {
            var html = await BodyAsync(await admin.GetAsync($"/{culture}/Admin{path}"));

            Assert.DoesNotContain("href=\"\"", html);
            Assert.DoesNotContain("action=\"\"", html);
            Assert.Matches($"<a class=\"admin-brand__name\" href=\"/{culture}\">", html);              // the store name -> the shop
            Assert.Matches($"<a class=\"admin-brand__label\" href=\"/{culture}/Admin\">", html);      // "Admin panel" -> the dashboard
            Assert.Matches($"<a class=\"admin-link admin-link--shop\" href=\"/{culture}\">", html);   // the "View shop" button
            Assert.Matches($"<form method=\"post\" action=\"/{culture}/Account/Logout\">", html);      // Sign out
        }
    }

    [Fact]
    public async Task ViewInShop_OnTheProductPage_OpensTheProductInTheShop()
    {
        var admin = await AdminClientAsync();
        var productId = await PlainProductAsync();

        var html = await admin.GetStringAsync($"/en/Admin/Products/Edit/{productId}");
        var link = Regex.Match(html, "<a class=\"btn btn--ghost\" href=\"(/en/Products/Details/[^\"]+)\"");

        Assert.True(link.Success, "No 'view in shop' link on the product page.");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(link.Groups[1].Value)).StatusCode);
    }

    [Fact]
    public async Task TheViewShopButton_OpensTheShop_AndSignOutEndsTheSession()
    {
        var admin = await AdminClientAsync();
        var page = await admin.GetStringAsync("/en/Admin/Orders");

        var shop = await admin.GetAsync("/en");
        Assert.Equal(HttpStatusCode.OK, shop.StatusCode);
        Assert.Contains("/en/Admin", await BodyAsync(shop)); // still signed in: the admin link is in the shop menu

        var signOut = await PostFormAsync(admin, "/en/Admin/Orders", "/en/Account/Logout", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode);
        Assert.Contains("/Account/Login", (await admin.GetAsync("/en/Admin/Orders")).Headers.Location!.ToString());
        Assert.Contains("admin-top", page);
    }

    // ---- Order buttons in the list ----

    [Fact]
    public async Task TheList_HasTheStatusButtons_ThatTheRulesAllow()
    {
        var admin = await AdminClientAsync();
        var (ordered, _) = await PlaceOrderAsync();
        var (packed, _) = await PlaceOrderAsync();
        var (delivered, _) = await PlaceOrderAsync();
        var (cancelled, _) = await PlaceOrderAsync();
        var (waitingTransfer, _) = await PlaceOrderAsync(method: "BankTransfer");
        await SetStatusAsync(packed, OrderStatus.Packed);
        await SetStatusAsync(delivered, OrderStatus.Delivered);
        await SetStatusAsync(cancelled, OrderStatus.Cancelled);

        var rows = new Dictionary<int, string>();
        foreach (var number in new[] { ordered, packed, delivered, cancelled, waitingTransfer })
        {
            rows[number] = RowOf(await admin.GetStringAsync($"/en/Admin/Orders?Query={number}"), number);
        }

        Assert.Contains("Mark as packed", rows[ordered]);
        Assert.Contains("Cancel order", rows[ordered]);
        Assert.DoesNotContain("Mark as delivered", rows[ordered]);

        Assert.Contains("Mark as delivered", rows[packed]);
        Assert.Contains("Cancel order", rows[packed]);
        Assert.DoesNotContain("Mark as packed", rows[packed]);

        Assert.DoesNotContain("<button", rows[delivered]);
        Assert.DoesNotContain("<button", rows[cancelled]);

        Assert.Contains("Bank transfer received", rows[waitingTransfer]);
        Assert.Contains("Cancel order", rows[waitingTransfer]);
        Assert.DoesNotContain("Mark as packed", rows[waitingTransfer]);
    }

    [Fact]
    public async Task APackedButton_InTheList_ChangesTheStatus_AndComesBackToTheSameList_AndTheCustomerSeesIt()
    {
        var customer = NewClient();
        await SignedInUserAsync(customer);
        var (number, key) = await PlaceOrderAsync(customer);
        var admin = await AdminClientAsync();
        var listUrl = $"/en/Admin/Orders?Query={number}&status=Ordered";

        var row = RowOf(await admin.GetStringAsync(listUrl), number);
        Assert.Contains("name=\"returnUrl\"", row);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(listUrl), row);

        var response = await PostFormAsync(admin, listUrl, "/en/Admin/Orders/ChangeStatus", new Dictionary<string, string>
        {
            ["number"] = number.ToString(),
            ["status"] = "Packed",
            ["returnUrl"] = listUrl
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(listUrl, response.Headers.Location!.OriginalString);
        Assert.Contains("Status changed.", await admin.GetStringAsync(listUrl));

        // The customer sees it, in the list of their orders and on the order page.
        Assert.Contains("Packed", RowOrCard(await customer.GetStringAsync("/en/Orders"), number));
        Assert.Contains("Packed", await customer.GetStringAsync($"/en/Orders/Details?number={number}"));
        Assert.Contains("Packed", await NewClient().GetStringAsync($"/en/Orders/Details?number={number}&key={key}"));
    }

    private static string RowOrCard(string html, int number)
    {
        var start = html.IndexOf($"{number}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Order {number} is not on the page.");
        return html[start..Math.Min(html.Length, start + 1500)];
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("//evil.example/steal")]
    public async Task AReturnAddressOutsideTheSite_IsIgnored(string returnUrl)
    {
        var (number, _) = await PlaceOrderAsync();
        var admin = await AdminClientAsync();

        var response = await PostFormAsync(admin, "/en/Admin/Orders", "/en/Admin/Orders/ChangeStatus", new Dictionary<string, string>
        {
            ["number"] = number.ToString(),
            ["status"] = "Packed",
            ["returnUrl"] = returnUrl
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/en/Admin/Orders/Details?number={number}", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task TheBankTransferButton_InTheList_MarksItReceived_AndComesBack()
    {
        var (number, _) = await PlaceOrderAsync(method: "BankTransfer");
        var admin = await AdminClientAsync();
        var listUrl = $"/en/Admin/Orders?Query={number}";

        var response = await PostFormAsync(admin, listUrl, "/en/Admin/Orders/MarkReceived", new Dictionary<string, string>
        {
            ["number"] = number.ToString(),
            ["returnUrl"] = listUrl
        });

        Assert.Equal(listUrl, response.Headers.Location!.OriginalString);
        Assert.DoesNotContain("Bank transfer received", RowOf(await admin.GetStringAsync(listUrl), number));
        Assert.Contains("Mark as packed", RowOf(await admin.GetStringAsync(listUrl), number));
    }

    // ---- The order page says what comes next ----

    [Fact]
    public async Task TheOrderPage_SaysWhatTheOrderWaitsFor()
    {
        var admin = await AdminClientAsync();
        var (ordered, _) = await PlaceOrderAsync();
        var (transfer, _) = await PlaceOrderAsync(method: "BankTransfer");
        var (card, _) = await PlaceOrderAsync();
        await SetStatusAsync(card, OrderStatus.AwaitingPayment);
        using (var scope = Fixture.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Orders.Where(o => o.Number == card)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.PaymentMethod, PaymentMethod.Card));
        }

        Assert.Contains("pack the order", await admin.GetStringAsync($"/en/Admin/Orders/Details?number={ordered}"));
        Assert.Contains("Press \"Bank transfer received\"", WebUtility.HtmlDecode(await admin.GetStringAsync($"/en/Admin/Orders/Details?number={transfer}")));
        Assert.Contains("card payment", await admin.GetStringAsync($"/en/Admin/Orders/Details?number={card}"));

        await SetStatusAsync(ordered, OrderStatus.Packed);
        Assert.Contains("Mark as delivered", await admin.GetStringAsync($"/en/Admin/Orders/Details?number={ordered}"));
        await SetStatusAsync(ordered, OrderStatus.Delivered);
        Assert.Contains("No further actions.", await admin.GetStringAsync($"/en/Admin/Orders/Details?number={ordered}"));
    }
}

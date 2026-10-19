using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Infrastructure.Data;

namespace Shop.Tests.Web;

[Collection("Web")]
public class AdminAnalyticsFlowTests : CheckoutTestBase
{
    public AdminAnalyticsFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword)).StatusCode);
        return client;
    }

    private async Task<(int Number, string Email)> PlaceOrderAsync()
    {
        var guest = NewClient();
        await AddToCartAsync(guest, await PlainProductAsync());
        var email = NewEmail();
        var (number, _) = ParseDone(await CheckoutAsync(guest, email));
        return (number, email);
    }

    private static string VisibleText(string html) =>
        Regex.Replace(Regex.Replace(html, "<script[\\s\\S]*?</script>", " "), "<[^>]+>", " ");

    // ---- Who may come in ----

    [Theory]
    [InlineData("/en/Admin/Analytics")]
    [InlineData("/hy/Admin/Analytics?preset=Today")]
    public async Task AVisitorWhoIsNotSignedIn_IsSentToTheSignInPage(string url)
    {
        var response = await NewClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task ACustomer_GetsAccessDenied_AndCannotExportAnything()
    {
        var customer = NewClient();
        await SignedInUserAsync(customer);

        var page = await customer.GetAsync("/en/Admin/Analytics");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("/Account/AccessDenied", page.Headers.Location!.ToString());

        foreach (var action in new[] { "ExportOrders", "ExportItems" })
        {
            var export = await PostFormAsync(customer, "/en/Orders/Lookup", "/en/Admin/Analytics/" + action, new Dictionary<string, string>
            {
                ["preset"] = "Last30Days"
            });

            Assert.Equal(HttpStatusCode.Redirect, export.StatusCode);
            Assert.Contains("/Account/AccessDenied", export.Headers.Location!.ToString());
            Assert.NotEqual("text/csv", export.Content.Headers.ContentType?.MediaType);
        }
    }

    // ---- The screen ----

    [Theory]
    [InlineData("hy")]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task TheScreen_Opens_InEveryLanguage_WithEveryPeriod_AndNoUntranslatedKeys(string culture)
    {
        await PlaceOrderAsync(); // so that the tables are not empty
        var admin = await AdminClientAsync();

        foreach (var query in new[]
        {
            "", "?preset=Today", "?preset=Last7Days", "?preset=Last30Days", "?preset=ThisMonth", "?preset=LastMonth",
            "?preset=Custom&from=2020-01-01&to=2020-01-31"
        })
        {
            var response = await admin.GetAsync($"/{culture}/Admin/Analytics{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await BodyAsync(response);
            var text = VisibleText(html);
            Assert.Empty(Regex.Matches(text, @"\b(Admin|An|Val|Status|Pay|Region|Checkout)_[A-Za-z0-9_]+\b").Select(m => m.Value).Distinct());
            Assert.Contains("<svg", html);
            Assert.DoesNotContain("<script src=\"http", html); // no chart library, no CDN
        }
    }

    [Fact]
    public async Task TheScreen_ShowsTheNewOrder_InTheLast7Days()
    {
        var before = await Last7DaysOrdersAsync();
        await PlaceOrderAsync();

        var after = await Last7DaysOrdersAsync();

        Assert.Equal(before + 1, after);
    }

    // The "Orders" card: the number in the card next to the label.
    private async Task<int> Last7DaysOrdersAsync()
    {
        var admin = await AdminClientAsync();
        var html = await admin.GetStringAsync("/en/Admin/Analytics?preset=Last7Days");
        var match = Regex.Match(html, @"<strong>(\d+)</strong>\s*<span>Orders</span>");
        Assert.True(match.Success, "The Orders card was not found");
        return int.Parse(match.Groups[1].Value);
    }

    [Fact]
    public async Task ABadCustomRange_ShowsTheLast30Days_AndAMessage()
    {
        var admin = await AdminClientAsync();

        foreach (var query in new[]
        {
            "?preset=Custom",                                      // no dates
            "?preset=Custom&from=2030-03-10&to=2030-03-01",        // reversed
            "?preset=Custom&from=2020-01-01&to=2022-01-01",        // too long
            "?preset=Custom&from=notadate&to=2020-01-01"           // not a date
        })
        {
            var response = await admin.GetAsync("/en/Admin/Analytics" + query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await BodyAsync(response);
            Assert.Contains("The dates were not valid", html);
            Assert.Matches("<a class=\"chip is-active\"[^>]*>30 days</a>", html); // the 30-day button is chosen
        }
    }

    [Fact]
    public async Task AGoodCustomRange_IsShownAsTyped()
    {
        var admin = await AdminClientAsync();

        var html = await admin.GetStringAsync("/en/Admin/Analytics?preset=Custom&from=2020-01-10&to=2020-01-20");

        Assert.DoesNotContain("The dates were not valid", html);
        Assert.Contains("value=\"2020-01-10\"", html);
        Assert.Contains("value=\"2020-01-20\"", html);
        Assert.Contains("10 Jan 2020", html);
    }

    // ---- Exports ----

    private async Task<HttpResponseMessage> ExportAsync(HttpClient admin, string action, string preset = "Last7Days")
    {
        return await PostFormAsync(admin, "/en/Admin/Analytics", "/en/Admin/Analytics/" + action, new Dictionary<string, string>
        {
            ["preset"] = preset
        });
    }

    [Fact]
    public async Task OrdersExport_IsACsvFile_WithTheOrder_WithoutPersonalData_AndIsInTheAuditLog()
    {
        var (number, email) = await PlaceOrderAsync();
        var admin = await AdminClientAsync();

        var response = await ExportAsync(admin, "ExportOrders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Matches(@"^orders-\d{4}-\d{2}-\d{2}_\d{4}-\d{2}-\d{2}\.csv$", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith("Number,Placed,Status,", text);
        Assert.Contains($"\r\n{number},", text);
        Assert.DoesNotContain(email, text);

        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        var audit = await db.AuditLogs.Where(a => a.Action == "Export.Orders").OrderByDescending(a => a.Id).FirstAsync();
        Assert.Equal(ShopWebFactory.AdminEmail, audit.UserEmail, ignoreCase: true);
        Assert.Equal("Analytics", audit.EntityType);
    }

    [Fact]
    public async Task ItemsExport_HasTheSoldItems_WithoutPersonalData_AndIsInTheAuditLog()
    {
        var (_, email) = await PlaceOrderAsync();
        var admin = await AdminClientAsync();

        var response = await ExportAsync(admin, "ExportItems");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Matches(@"^items-", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));

        var text = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("Number,Placed,Status,Sku,Name,Brand,Quantity,UnitPrice,RegularPrice,LineTotal", text);
        Assert.DoesNotContain(email, text);

        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Export.Items"));
    }

    [Fact]
    public async Task Export_IsPostOnly_AndNeedsTheFormToken()
    {
        var admin = await AdminClientAsync();

        // A link or a prefetch cannot start an export.
        foreach (var action in new[] { "ExportOrders", "ExportItems" })
        {
            var get = await admin.GetAsync("/en/Admin/Analytics/" + action);
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
            Assert.NotEqual("text/csv", get.Content.Headers.ContentType?.MediaType);
        }

        // A form from another site has no token.
        var forged = await admin.PostAsync("/en/Admin/Analytics/ExportOrders", new FormUrlEncodedContent(new Dictionary<string, string> { ["preset"] = "Today" }));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
    }

    [Fact]
    public async Task Export_WithABadRange_UsesTheLast30Days()
    {
        var admin = await AdminClientAsync();

        var response = await PostFormAsync(admin, "/en/Admin/Analytics", "/en/Admin/Analytics/ExportOrders", new Dictionary<string, string>
        {
            ["preset"] = "Custom",
            ["from"] = "2030-03-10",
            ["to"] = "2030-03-01"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var today = Shop.Core.Orders.ShopClock.Today(TimeProvider.System);
        Assert.Equal($"orders-{today.AddDays(-29):yyyy-MM-dd}_{today:yyyy-MM-dd}.csv", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
    }
}

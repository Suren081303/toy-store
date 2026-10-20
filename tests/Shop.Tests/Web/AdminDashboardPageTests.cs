using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Core.Admin;
using Shop.Core.Orders;
using Shop.Infrastructure.Data;

namespace Shop.Tests.Web;

// The admin dashboard as the browser receives it: the boxes are there, and the buttons in them work and bring the admin back.
[Collection("Web")]
public class AdminDashboardPageTests : CheckoutTestBase
{
    public AdminDashboardPageTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword)).StatusCode);
        return client;
    }

    // An order whose delivery day is long past, so it is the first in the list of orders to pack.
    private async Task<int> LateOrderAsync()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var (number, _) = ParseDone(await CheckoutAsync(client, NewEmail(), f => f["PaymentMethod"] = "CashOnDelivery"));

        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        await db.Orders.Where(o => o.Number == number).ExecuteUpdateAsync(s => s.SetProperty(o => o.DeliveryDate, new DateOnly(2001, 1, 1)));
        return number;
    }

    // The part of the page between two markers (the boxes are told apart by their class names).
    private static string Between(string html, string from, string to)
    {
        var start = html.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' is not on the page.");
        var end = html.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"'{to}' is not on the page after '{from}'.");
        return html[start..end];
    }

    [Fact]
    public async Task TheDashboard_HasEveryBox_AndTheOrderToPack_WithItsLateFlag()
    {
        var number = await LateOrderAsync();
        var admin = await AdminClientAsync();

        var html = await admin.GetStringAsync("/en/Admin");

        foreach (var text in new[] { "Orders to pack", "Latest orders", "Low stock", "Sales today", "Yesterday", "New customers today", "Updated at ", "Refresh", "Print slips for orders to pack" })
        {
            Assert.Contains(text, html);
        }

        var pack = Between(html, "dash-pack", "dash-chart");
        Assert.Contains($"#{number}", pack);
        Assert.Contains("Late", pack);
        Assert.Contains("Mark as packed", pack);

        Assert.Contains("Sales per day", Between(html, "dash-chart", "dash-low"));   // the 14-day chart
        Assert.Contains($"#{number}", Between(html, "dash-latest", "</section>")); // the newest order is in the latest orders

        // The print link and the refresh link work.
        Assert.Contains("/en/Admin/Orders/Slips?status=Ordered", html);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/en/Admin/Orders/Slips?status=Ordered")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/en/Admin")).StatusCode);
    }

    [Theory]
    [InlineData("hy")]
    [InlineData("ru")]
    public async Task TheDashboard_IsTranslated_NoTextKeyIsShown(string culture)
    {
        await LateOrderAsync();
        var admin = await AdminClientAsync();

        var html = await admin.GetStringAsync($"/{culture}/Admin");

        Assert.DoesNotContain("Admin_", html);
        Assert.DoesNotContain("Orders to pack", html);
        Assert.Contains($"/{culture}/Admin/Orders/Slips?status=Ordered", html);
    }

    [Fact]
    public async Task ThePackButton_PacksTheOrder_AndComesBackToTheDashboard()
    {
        var number = await LateOrderAsync();
        var admin = await AdminClientAsync();

        var html = await admin.GetStringAsync("/en/Admin");
        var pack = Between(html, "dash-pack", "dash-chart");
        Assert.Contains($"name=\"number\" value=\"{number}\"", pack);
        Assert.Contains("name=\"returnUrl\" value=\"/en/Admin\"", pack);

        var response = await PostFormAsync(admin, "/en/Admin", "/en/Admin/Orders/ChangeStatus", new Dictionary<string, string>
        {
            ["number"] = number.ToString(),
            ["status"] = "Packed",
            ["returnUrl"] = "/en/Admin"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/en/Admin", response.Headers.Location!.OriginalString);

        html = await admin.GetStringAsync("/en/Admin");
        Assert.DoesNotContain($"#{number}", Between(html, "dash-pack", "dash-chart")); // no longer waits to be packed
        Assert.Contains($"#{number}", Between(html, "dash-latest", "</section>"));    // still among the latest orders
        Assert.Contains("Mark as delivered", Between(html, "dash-latest", "</section>")); // and the next button is there
    }

    [Fact]
    public async Task TheLowStockBox_ListsTheEmptyProduct_AndTheStockBoxSavesAndComesBack()
    {
        var productId = await PlainProductAsync();
        string sku;

        using (var scope = Fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();

            // The box shows ten products: leave only this one low.
            await db.Products.Where(p => p.Id != productId && p.StockQuantity <= StockLevels.Low)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, 40));
            await db.Products.Where(p => p.Id == productId).ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, 0));
            sku = await db.Products.Where(p => p.Id == productId).Select(p => p.Sku).SingleAsync();
        }

        var admin = await AdminClientAsync();
        var html = await admin.GetStringAsync("/en/Admin");

        var low = Between(html, "dash-low", "dash-latest");
        Assert.Contains(sku, low);
        Assert.Contains("Out of stock", low);
        Assert.Contains("action=\"/en/Admin/Products/SetStock\"", low);
        Assert.Contains("name=\"back\" value=\"/en/Admin\"", low);
        Assert.Contains($"/en/Admin/Products/Edit/{productId}", low);

        var response = await PostFormAsync(admin, "/en/Admin", "/en/Admin/Products/SetStock", new Dictionary<string, string>
        {
            ["id"] = productId.ToString(),
            ["stock"] = "25",
            ["back"] = "/en/Admin"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/en/Admin", response.Headers.Location!.OriginalString);

        html = await admin.GetStringAsync("/en/Admin");
        Assert.DoesNotContain(sku, Between(html, "dash-low", "dash-latest"));   // 25 is not low
        Assert.Contains("Nothing is running low.", html);
    }
}

using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Core.Entities;
using Shop.Infrastructure.Data;

namespace Shop.Tests.Web;

// An admin looks at the shop like a customer but does not buy in it: the server refuses, and the buying parts of the pages are not shown.
[Collection("Web")]
public class AdminShoppingBlockTests : CheckoutTestBase
{
    public AdminShoppingBlockTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    private static KeyValuePair<string, string> F(string key, string value) => new(key, value);

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword)).StatusCode);
        return client;
    }

    private async Task<string> AdminIdAsync()
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(ShopWebFactory.AdminEmail))!.Id;
    }

    private async Task<TResult> InDbAsync<TResult>(Func<ShopDbContext, Task<TResult>> action)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ShopDbContext>());
    }

    private async Task<(int Number, string Key)> PlaceGuestOrderAsync()
    {
        var guest = NewClient();
        await AddToCartAsync(guest, await PlainProductAsync());
        return ParseDone(await CheckoutAsync(guest, NewEmail()));
    }

    // ---- The server refuses ----

    [Theory]
    [InlineData("/en/Cart")]
    [InlineData("/en/Checkout")]
    [InlineData("/en/Favorites")]
    [InlineData("/en/Orders")]
    public async Task BuyingPages_AreRefused_ForAnAdmin_WithAnExplanation(string url)
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync(url);
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Admins cannot buy", html);
        Assert.Contains("href=\"/en/Admin\"", html);
    }

    [Fact]
    public async Task AnAdmin_CannotPutAnythingInACart_ByAPageOrByTheScript()
    {
        var admin = await AdminClientAsync();
        var adminId = await AdminIdAsync();
        var productId = (await PlainProductAsync()).ToString();

        var page = await PostFormAsync(admin, "/en/Account", "/en/Cart/Add", new Dictionary<string, string> { ["productId"] = productId, ["quantity"] = "1" });
        var script = await PostJsonAsync(admin, "/en/Cart/Add", new Dictionary<string, string> { ["productId"] = productId, ["quantity"] = "1" });
        var quantity = await PostFormAsync(admin, "/en/Account", "/en/Cart/SetQuantity", new Dictionary<string, string> { ["productId"] = productId, ["quantity"] = "3" });
        var remove = await PostFormAsync(admin, "/en/Account", "/en/Cart/Remove", new Dictionary<string, string> { ["productId"] = productId });

        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, script.StatusCode);
        Assert.Contains("AdminCannotBuy", await BodyAsync(script)); // the script gets JSON, not a page
        Assert.Equal(HttpStatusCode.Forbidden, quantity.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        Assert.False(await InDbAsync(db => db.Carts.AnyAsync(c => c.UserId == adminId)));
    }

    [Fact]
    public async Task AnAdmin_CannotMakeFavorites()
    {
        var admin = await AdminClientAsync();
        var adminId = await AdminIdAsync();

        var response = await PostJsonAsync(admin, "/en/Favorites/Toggle", new Dictionary<string, string> { ["productId"] = (await PlainProductAsync()).ToString() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await InDbAsync(db => db.Favorites.AnyAsync(f => f.UserId == adminId)));
    }

    [Fact]
    public async Task AnAdmin_CannotCheckOut_PayOrRequestAReturn()
    {
        var (number, key) = await PlaceGuestOrderAsync();
        var admin = await AdminClientAsync();
        var ordersBefore = await InDbAsync(db => db.Orders.CountAsync());
        var paymentsBefore = await InDbAsync(db => db.Payments.CountAsync());

        // A checkout that would be valid for a customer.
        var guest = NewClient();
        await AddToCartAsync(guest, await PlainProductAsync());
        var (total, slotId) = await ReadCheckoutPageAsync(guest);
        var checkout = await PostFormAsync(admin, "/en/Account", "/en/Checkout", Fields(total, slotId, NewEmail()));
        var pay = await PostFormAsync(admin, "/en/Account", "/en/Payments/Start", new Dictionary<string, string> { ["number"] = number.ToString(), ["key"] = key });
        var returnRequest = await PostFormAsync(admin, "/en/Account", "/en/Orders/RequestReturn", new Dictionary<string, string> { ["number"] = number.ToString(), ["key"] = key, ["reason"] = "Not wanted" });

        Assert.Equal(HttpStatusCode.Forbidden, checkout.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, pay.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, returnRequest.StatusCode);
        Assert.Equal(ordersBefore, await InDbAsync(db => db.Orders.CountAsync()));
        Assert.Equal(paymentsBefore, await InDbAsync(db => db.Payments.CountAsync()));
        Assert.False(await InDbAsync(db => db.ReturnRequests.AnyAsync(r => r.Order.Number == number)));
    }

    [Fact]
    public async Task ACartMadeBeforeSigningIn_IsNotMovedIntoAnAdminAccount()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync()); // a guest cart
        var adminId = await AdminIdAsync();

        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword)).StatusCode);

        Assert.False(await InDbAsync(db => db.Carts.AnyAsync(c => c.UserId == adminId)));
    }

    [Fact]
    public async Task AnAdmin_CanStillOpenAnOrderWithItsLink_ToHelpACustomer()
    {
        var (number, key) = await PlaceGuestOrderAsync();
        var admin = await AdminClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/en/Orders/Details?number={number}&key={key}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/en/Orders/Lookup")).StatusCode);
    }

    [Fact]
    public async Task Customers_AndGuests_StillShopAsBefore()
    {
        var customer = NewClient();
        await SignedInUserAsync(customer);
        var productId = await PlainProductAsync();

        await AddToCartAsync(customer, productId);
        await AddToCartAsync(NewClient(), productId); // a guest

        foreach (var url in new[] { "/en/Cart", "/en/Favorites", "/en/Orders" })
        {
            Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync(url)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await NewClient().GetAsync("/en/Cart")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostJsonAsync(customer, "/en/Favorites/Toggle", new Dictionary<string, string> { ["productId"] = productId.ToString() })).StatusCode);
    }

    // ---- The pages ----

    [Theory]
    [InlineData("hy")]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task AnAdmin_SeesTheShop_WithoutBuyingButtons_ButWithTheAdminPanelLinkAndABanner(string culture)
    {
        var admin = await AdminClientAsync();
        var productId = await PlainProductAsync();

        foreach (var path in new[] { "", "/Products", $"/Products/Details/{productId}", "/Gifts", "/Account" })
        {
            var html = await admin.GetStringAsync($"/{culture}{path}");

            // The link to the panel is in the header on every shop page, and the banner explains the situation.
            Assert.Contains($"class=\"icon-btn header__admin\" href=\"/{culture}/Admin\"", html);
            Assert.Contains("class=\"admin-banner\"", html);
            Assert.DoesNotContain("AdminShop_", html);

            // Nothing to buy with.
            Assert.DoesNotContain("data-cart-add", html);
            Assert.DoesNotContain("data-fav-toggle", html);
            Assert.DoesNotContain("class=\"buybar\"", html);
            Assert.DoesNotContain($"href=\"/{culture}/Cart\"", html);
            Assert.DoesNotContain($"href=\"/{culture}/Favorites\"", html);
            Assert.DoesNotContain($"href=\"/{culture}/Orders\"", html);
        }

        // The product page still shows the product.
        var details = WebUtility.HtmlDecode(await admin.GetStringAsync($"/{culture}/Products/Details/{productId}"));
        Assert.Contains("pdp__title", details);
    }

    [Fact]
    public async Task TheAccountPage_OfAnAdmin_LinksToThePanel_AndHasNoOrdersOrFavoritesTabs()
    {
        var admin = await AdminClientAsync();

        var html = await admin.GetStringAsync("/en/Account");

        Assert.Matches("class=\"btn btn--primary\" href=\"/en/Admin\"", html);
        Assert.DoesNotContain("class=\"chip \" href=\"/en/Orders\"", html);
        Assert.DoesNotContain("href=\"/en/Favorites\"", html);
        Assert.Contains("href=\"/en/Account/ChangePassword\"", html); // the profile and password tabs stay
    }

    [Fact]
    public async Task Customers_AndGuests_SeeNoAdminLink_NoBanner_AndTheBuyingButtons()
    {
        var customer = NewClient();
        await SignedInUserAsync(customer);
        var productId = await PlainProductAsync();

        foreach (var client in new[] { customer, NewClient() })
        {
            foreach (var path in new[] { "", "/Products", $"/Products/Details/{productId}" })
            {
                var html = await client.GetStringAsync("/en" + path);

                Assert.DoesNotContain("header__admin", html);
                Assert.DoesNotContain("class=\"admin-banner\"", html);
                Assert.Contains("href=\"/en/Cart\"", html);
                Assert.Contains("href=\"/en/Favorites\"", html);
            }

            Assert.Contains("data-cart-add", await client.GetStringAsync($"/en/Products/Details/{productId}"));
        }
    }
}

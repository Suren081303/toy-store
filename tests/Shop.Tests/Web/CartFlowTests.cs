using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Core.Catalog;
using Shop.Infrastructure.Data;

namespace Shop.Tests.Web;

[Collection("Web")]
public class CartFlowTests : WebTestBase
{
    public CartFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    // Demo products (seeded by the site): plenty of stock without a discount, stock of exactly 2, sold out.
    private Task<int> PlainProductAsync(int skip = 0) => FindProductIdAsync(p => p.StockQuantity >= 5 && p.DiscountPercent == 0 && p.Id != skip);

    private Task<int> LowStockProductAsync() => FindProductIdAsync(p => p.StockQuantity == 2);

    private Task<int> SoldOutProductAsync() => FindProductIdAsync(p => p.StockQuantity == 0);

    private static Task<HttpResponseMessage> AddAsync(HttpClient client, int productId, int quantity = 1)
    {
        return PostJsonAsync(client, "/en/Cart/Add", new Dictionary<string, string>
        {
            ["productId"] = productId.ToString(),
            ["quantity"] = quantity.ToString()
        });
    }

    private static async Task<HttpResponseMessage> PostCartFormAsync(HttpClient client, string action, Dictionary<string, string> fields)
    {
        return await PostFormAsync(client, "/en/Cart", "/en/Cart/" + action, fields);
    }

    [Fact]
    public async Task Guest_AddsAProduct_TheCartCookieAppears_AndTheBadgeAndCartPageShowIt()
    {
        var client = NewClient();
        var productId = await PlainProductAsync();

        var response = await AddAsync(client, productId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await BodyAsync(response);
        Assert.Contains("\"status\":\"Added\"", json);
        Assert.Contains("\"count\":1", json);
        Assert.Contains("Added to cart", json);
        Assert.False(string.IsNullOrEmpty(CookieValue(response, "Shop.Cart")));

        Assert.Contains($"/en/Products/Details/{productId}", await client.GetStringAsync("/en/Cart"));
        Assert.Matches(new Regex("data-cart-count[^>]*>1<"), await client.GetStringAsync("/en"));
    }

    [Fact]
    public async Task Add_AgainAndAgain_StopsAtTheStock_WithAClearMessage()
    {
        var client = NewClient();
        var productId = await LowStockProductAsync();

        await AddAsync(client, productId);
        var second = await AddAsync(client, productId);
        Assert.Contains("\"quantity\":2", await BodyAsync(second));

        var third = await AddAsync(client, productId);

        var json = await BodyAsync(third);
        Assert.Contains("\"status\":\"LimitReached\"", json);
        Assert.Contains("\"quantity\":2", json);
        Assert.Contains("Only 2 available", json);
    }

    [Fact]
    public async Task Add_ASoldOutOrArchivedProduct_IsRefused_AndSetsNoCookie()
    {
        var client = NewClient();
        using var scope = Fixture.Factory.Services.CreateScope();
        var archivedId = await scope.ServiceProvider.GetRequiredService<ShopDbContext>()
            .Products.Where(p => p.IsArchived).Select(p => p.Id).FirstAsync();

        foreach (var productId in new[] { await SoldOutProductAsync(), archivedId, int.MaxValue })
        {
            var response = await AddAsync(client, productId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await BodyAsync(response);
            Assert.Contains("\"status\":\"NotAvailable\"", json);
            Assert.Contains("\"count\":0", json);
            Assert.Null(CookieValue(response, "Shop.Cart"));
        }
    }

    [Fact]
    public async Task Add_WithoutTheAntiforgeryToken_IsRejected()
    {
        var client = NewClient();

        var response = await client.PostAsync("/en/Cart/Add", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["productId"] = (await PlainProductAsync()).ToString()
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Add_WithAPlainFormPost_LandsOnTheCartPage()
    {
        var client = NewClient();
        var productId = await PlainProductAsync();

        var response = await PostFormAsync(client, $"/en/Products/Details/{productId}", "/en/Cart/Add", new Dictionary<string, string>
        {
            ["productId"] = productId.ToString(),
            ["quantity"] = "2"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/en/Cart", response.Headers.Location!.OriginalString);

        var cart = await client.GetStringAsync("/en/Cart");
        Assert.Contains("Added to cart", cart);
        Assert.Matches(new Regex("class=\"qty__value\"[^>]*>2<"), cart);
    }

    [Fact]
    public async Task ChangeQuantity_AndRemove_UpdateTheCart()
    {
        var client = NewClient();
        var productId = await PlainProductAsync();
        await AddAsync(client, productId, quantity: 2);

        var change = await PostCartFormAsync(client, "SetQuantity", new Dictionary<string, string>
        {
            ["productId"] = productId.ToString(),
            ["quantity"] = "4"
        });
        Assert.Equal(HttpStatusCode.Redirect, change.StatusCode);
        Assert.Matches(new Regex("class=\"qty__value\"[^>]*>4<"), await client.GetStringAsync("/en/Cart"));

        var remove = await PostCartFormAsync(client, "Remove", new Dictionary<string, string> { ["productId"] = productId.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, remove.StatusCode);
        Assert.Contains("Your cart is empty", await client.GetStringAsync("/en/Cart"));
        Assert.Matches(new Regex("data-cart-count hidden"), await client.GetStringAsync("/en"));
    }

    [Fact]
    public async Task TheCartPage_ShowsTheRegisterHintToGuests_AndTheCustomerPriceToSignedInCustomers()
    {
        var productId = await PlainProductAsync();
        decimal price;
        using (var scope = Fixture.Factory.Services.CreateScope())
        {
            price = await scope.ServiceProvider.GetRequiredService<ShopDbContext>()
                .Products.Where(p => p.Id == productId).Select(p => p.Price).FirstAsync();
        }

        var customerPrice = ProductRules.GetFinalPrice(price, ProductRules.CustomerDiscountPercent).ToString("N0", new CultureInfo("en"));
        var regularPrice = price.ToString("N0", new CultureInfo("en"));

        var guest = NewClient();
        await AddAsync(guest, productId);
        var guestPage = await guest.GetStringAsync("/en/Cart");
        Assert.Contains("Register and get 10% off", guestPage);
        Assert.DoesNotContain("registered-customer 10% price", guestPage);

        var customer = NewClient();
        await SignedInUserAsync(customer);
        await AddAsync(customer, productId);
        var customerPage = await customer.GetStringAsync("/en/Cart");
        Assert.Contains("registered-customer 10% price", customerPage);
        Assert.DoesNotContain("Register and get 10% off", customerPage);
        Assert.Contains(customerPrice, customerPage);
        Assert.Contains(regularPrice, customerPage);   // the regular price is shown crossed out
    }

    [Fact]
    public async Task SigningIn_MovesTheGuestCartAndFavoritesIntoTheAccount_AndClearsTheCookies()
    {
        var guest = NewClient();
        var cartProduct = await PlainProductAsync();
        var favoriteProduct = await PlainProductAsync(skip: cartProduct);

        var add = await AddAsync(guest, cartProduct, quantity: 2);
        var guestCartId = Guid.Parse(CookieValue(add, "Shop.Cart")!);
        await PostJsonAsync(guest, "/en/Favorites/Toggle", new Dictionary<string, string> { ["productId"] = favoriteProduct.ToString() });

        var email = NewEmail();
        await RegisterAsync(guest, email);
        await ConfirmEmailAsync(guest, email);
        var login = await LoginAsync(guest, email, Password);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.True(CookieCleared(login, "Shop.Cart"));
        Assert.True(CookieCleared(login, "Shop.Favs"));

        Assert.Matches(new Regex("class=\"qty__value\"[^>]*>2<"), await guest.GetStringAsync("/en/Cart"));
        Assert.Contains($"/en/Products/Details/{favoriteProduct}", await guest.GetStringAsync("/en/Favorites"));

        using (var scope = Fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            Assert.False(await db.Carts.AnyAsync(c => c.Id == guestCartId));
        }

        // Signing in on another device shows the same cart and favorites.
        var otherDevice = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(otherDevice, email, Password)).StatusCode);
        Assert.Contains($"/en/Products/Details/{cartProduct}", await otherDevice.GetStringAsync("/en/Cart"));
        Assert.Contains($"/en/Products/Details/{favoriteProduct}", await otherDevice.GetStringAsync("/en/Favorites"));
        Assert.Matches(new Regex("data-cart-count[^>]*>2<"), await otherDevice.GetStringAsync("/en"));
    }

    [Fact]
    public async Task ASignedInCustomersCart_SurvivesSigningOutAndIn()
    {
        var client = NewClient();
        var email = await SignedInUserAsync(client);
        var productId = await PlainProductAsync();
        await AddAsync(client, productId);

        await PostFormAsync(client, "/en/Account", "/en/Account/Logout", new Dictionary<string, string>());
        Assert.Contains("Your cart is empty", await client.GetStringAsync("/en/Cart"));

        await LoginAsync(client, email, Password);
        Assert.Contains($"/en/Products/Details/{productId}", await client.GetStringAsync("/en/Cart"));
    }

    [Fact]
    public async Task UnknownOrMangledCartCookies_AreTreatedAsNoCart()
    {
        var client = Fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });

        foreach (var cookie in new[] { $"Shop.Cart={Guid.NewGuid()}", "Shop.Cart=abc", "Shop.Cart=", "Shop.Favs=1,,x,-5,99999999999" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/en/Cart");
            request.Headers.Add("Cookie", cookie);

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Your cart is empty", await BodyAsync(response));
        }
    }

    [Fact]
    public async Task TheProductPage_OffersAQuantityStepper_LimitedByStock()
    {
        var client = NewClient();
        var productId = await LowStockProductAsync();

        var html = await client.GetStringAsync($"/en/Products/Details/{productId}");

        Assert.Contains("data-qty-step", html);
        Assert.Matches(new Regex("name=\"quantity\" value=\"1\" min=\"1\" max=\"2\""), html);
        Assert.Contains("Only 2 left", html);
    }

    [Fact]
    public async Task TheCartPage_WorksInAllThreeLanguages()
    {
        var client = NewClient();
        await AddAsync(client, await PlainProductAsync());

        foreach (var language in new[] { "hy", "ru", "en" })
        {
            var response = await client.GetAsync($"/{language}/Cart");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await BodyAsync(response);
            Assert.Contains($"<html lang=\"{language}\"", html);
            Assert.DoesNotContain("Cart_", html);   // no missing translation keys
        }
    }
}

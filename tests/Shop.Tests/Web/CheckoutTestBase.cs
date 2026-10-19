using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;
using Shop.Core.Orders;

namespace Shop.Tests.Web;

// Helpers for tests that go through the checkout: put a product in the cart, fill and send the form, read the redirect.
public abstract class CheckoutTestBase : WebTestBase
{
    protected CheckoutTestBase(ShopWebFixture fixture) : base(fixture)
    {
    }

    // A demo product with enough stock and no discount of its own. The demo stock is limited and many tests buy from it,
    // so when no such product has enough left, the plain demo products are refilled.
    protected async Task<int> PlainProductAsync()
    {
        using (var scope = Fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Shop.Infrastructure.Data.ShopDbContext>();

            if (!await db.Products.AnyAsync(p => !p.IsArchived && p.Category.IsActive && p.DiscountPercent == 0 && p.StockQuantity >= 10))
            {
                await db.Products.Where(p => !p.IsArchived && p.DiscountPercent == 0).ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, 40));
            }
        }

        return await FindProductIdAsync(p => p.StockQuantity >= 5 && p.DiscountPercent == 0);
    }

    protected static string FirstDeliveryDate() => ShopClock.Today(TimeProvider.System).AddDays(1).ToString("yyyy-MM-dd");

    protected static async Task AddToCartAsync(HttpClient client, int productId, int quantity = 1)
    {
        var response = await PostJsonAsync(client, "/en/Cart/Add", new Dictionary<string, string>
        {
            ["productId"] = productId.ToString(),
            ["quantity"] = quantity.ToString()
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The total the checkout page shows (it is posted back as ExpectedTotal), and the first time slot.
    protected static async Task<(string Total, string SlotId)> ReadCheckoutPageAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/en/Checkout");
        var total = Regex.Match(html, "name=\"ExpectedTotal\"[^>]*value=\"([^\"]+)\"");
        var slot = Regex.Match(html, "name=\"DeliverySlotId\"[^>]*value=\"(\\d+)\"");
        Assert.True(total.Success, "No ExpectedTotal on the checkout page.");
        Assert.True(slot.Success, "No delivery time option on the checkout page.");
        return (total.Groups[1].Value, slot.Groups[1].Value);
    }

    protected static Dictionary<string, string> Fields(string total, string slotId, string email, Guid? checkoutId = null)
    {
        return new Dictionary<string, string>
        {
            ["CheckoutId"] = (checkoutId ?? Guid.NewGuid()).ToString(),
            ["CustomerName"] = "Anna Hakobyan",
            ["Email"] = email,
            ["Phone"] = "091 123456",
            ["Region"] = "yerevan",
            ["City"] = "Yerevan",
            ["Street"] = "Abovyan",
            ["Building"] = "12",
            ["Apartment"] = "5",
            ["DeliveryDate"] = FirstDeliveryDate(),
            ["DeliverySlotId"] = slotId,
            ["PaymentMethod"] = "CashOnDelivery",
            ["AcceptTerms"] = "true",
            ["ExpectedTotal"] = total
        };
    }

    protected async Task<HttpResponseMessage> CheckoutAsync(HttpClient client, string email, Action<Dictionary<string, string>>? change = null, Guid? checkoutId = null)
    {
        var (total, slotId) = await ReadCheckoutPageAsync(client);
        var fields = Fields(total, slotId, email, checkoutId);
        change?.Invoke(fields);
        return await PostFormAsync(client, "/en/Checkout", "/en/Checkout", fields);
    }

    // "/en/Checkout/Done?number=10001&key=abc" -> (10001, "abc")
    protected static (int Number, string Key) ParseDone(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.Contains("/en/Checkout/Done", location);
        var match = Regex.Match(location, "number=(\\d+)&key=([^&]+)");
        Assert.True(match.Success, "Unexpected redirect: " + location);
        return (int.Parse(match.Groups[1].Value), match.Groups[2].Value);
    }
}

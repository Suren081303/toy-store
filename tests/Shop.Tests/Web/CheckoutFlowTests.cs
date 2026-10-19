using System.Net;
using System.Text.RegularExpressions;
using Shop.Core.Orders;

namespace Shop.Tests.Web;

[Collection("Web")]
public class CheckoutFlowTests : CheckoutTestBase
{
    public CheckoutFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    // ---- Guests ----

    [Fact]
    public async Task Checkout_WithAnEmptyCart_GoesBackToTheCart()
    {
        var response = await NewClient().GetAsync("/en/Checkout");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/en/Cart", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Guest_Orders_EmptiesTheCart_ShowsTheConfirmation_AndSendsTheEmails()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var email = NewEmail();

        var response = await CheckoutAsync(client, email);

        var (number, key) = ParseDone(response);
        var done = await client.GetStringAsync($"/en/Checkout/Done?number={number}&key={key}");
        Assert.Contains(number.ToString(), done);

        // The cart is empty again.
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/en/Checkout")).StatusCode);

        await Fixture.Factory.FlushEmailsAsync();
        Assert.Equal(1, Fixture.Factory.Emails.CountTo(email));
    }

    [Fact]
    public async Task Done_WithAWrongKey_IsNotFound()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var (number, key) = ParseDone(await CheckoutAsync(client, NewEmail()));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/en/Checkout/Done?number={number}&key={key}x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/en/Checkout/Done?number={number}")).StatusCode);
    }

    [Fact]
    public async Task Submitting_TheSameCheckoutTwice_GivesTheSameOrder()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var (total, slotId) = await ReadCheckoutPageAsync(client);
        var fields = Fields(total, slotId, NewEmail());

        var first = ParseDone(await PostFormAsync(client, "/en/Orders/Lookup", "/en/Checkout", new Dictionary<string, string>(fields)));
        var second = ParseDone(await PostFormAsync(client, "/en/Orders/Lookup", "/en/Checkout", new Dictionary<string, string>(fields)));

        Assert.Equal(first.Number, second.Number);
        Assert.Equal(first.Key, second.Key);
    }

    [Fact]
    public async Task Checkout_WithMissingFields_ShowsThePageAgain_AndCreatesNoOrder()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());

        var response = await CheckoutAsync(client, NewEmail(), f =>
        {
            f["CustomerName"] = string.Empty;
            f["Street"] = string.Empty;
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("field-validation-error", await BodyAsync(response));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/en/Checkout")).StatusCode); // the cart still has the product
    }

    [Theory]
    [InlineData("DeliveryDate", "2020-01-01")]
    [InlineData("DeliveryDate", "not-a-date")]
    [InlineData("Region", "atlantis")]
    [InlineData("DeliverySlotId", "999999")]
    [InlineData("PaymentMethod", "7")]
    [InlineData("AcceptTerms", "false")]
    public async Task Checkout_WithAnInvalidChoice_IsRefused(string field, string value)
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());

        var response = await CheckoutAsync(client, NewEmail(), f => f[field] = value);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("validation", await BodyAsync(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Checkout_WhenTheTotalShownIsOutOfDate_AsksTheCustomerToCheckAgain()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());

        var response = await CheckoutAsync(client, NewEmail(), f => f["ExpectedTotal"] = "1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The total changed", await BodyAsync(response));
    }

    // ---- Finding an order ----

    [Fact]
    public async Task Lookup_WithTheRightEmail_OpensTheOrder_AndWithAWrongOneDoesNot()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var email = NewEmail();
        var (number, key) = ParseDone(await CheckoutAsync(client, email));

        var other = NewClient();
        var found = await PostFormAsync(other, "/en/Orders/Lookup", "/en/Orders/Lookup", new Dictionary<string, string>
        {
            ["Number"] = number.ToString(),
            ["Email"] = email
        });
        Assert.Equal(HttpStatusCode.Redirect, found.StatusCode);
        Assert.Contains($"number={number}&key={key}", found.Headers.Location!.ToString());

        var notFound = await PostFormAsync(other, "/en/Orders/Lookup", "/en/Orders/Lookup", new Dictionary<string, string>
        {
            ["Number"] = number.ToString(),
            ["Email"] = "someone-else@example.com"
        });
        Assert.Equal(HttpStatusCode.OK, notFound.StatusCode);
        Assert.Contains("could not find an order", await BodyAsync(notFound));
    }

    [Fact]
    public async Task OrderDetails_NeedTheKey_OrTheOwnerAccount()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var (number, key) = ParseDone(await CheckoutAsync(client, NewEmail()));

        var stranger = NewClient();
        Assert.Equal(HttpStatusCode.OK, (await stranger.GetAsync($"/en/Orders/Details?number={number}&key={key}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/en/Orders/Details?number={number}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/en/Orders/Details?number={number}&key=wrong")).StatusCode);
    }

    [Fact]
    public async Task OrderList_NeedsASignedInCustomer()
    {
        var response = await NewClient().GetAsync("/en/Orders");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    // ---- Signed-in customers ----

    [Fact]
    public async Task SignedIn_Customer_GetsTheCustomerPrice_SeesTheOrderInTheList_AndOthersCannotOpenIt()
    {
        var client = NewClient();
        var email = await SignedInUserAsync(client);
        await AddToCartAsync(client, await PlainProductAsync());

        var (number, _) = ParseDone(await CheckoutAsync(client, email));

        // The order list and the order page open without a key.
        Assert.Contains(number.ToString(), await client.GetStringAsync("/en/Orders"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/en/Orders/Details?number={number}")).StatusCode);

        // Another signed-in customer does not get it.
        var other = NewClient();
        await SignedInUserAsync(other);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/en/Orders/Details?number={number}")).StatusCode);

        // The next checkout is prefilled with the last address.
        await AddToCartAsync(client, await PlainProductAsync());
        Assert.Contains("Abovyan", await client.GetStringAsync("/en/Checkout"));
    }

    [Fact]
    public async Task SignedIn_CheckoutEmail_IsTheAccountEmail_EvenIfTheFormSaysOtherwise()
    {
        var client = NewClient();
        var accountEmail = await SignedInUserAsync(client);
        await AddToCartAsync(client, await PlainProductAsync());

        var (number, key) = ParseDone(await CheckoutAsync(client, "forged@example.com"));

        Assert.Contains(accountEmail, await client.GetStringAsync($"/en/Orders/Details?number={number}&key={key}"));
        await Fixture.Factory.FlushEmailsAsync();
        Assert.Equal(0, Fixture.Factory.Emails.CountTo("forged@example.com"));
    }

    // ---- Returns ----

    [Fact]
    public async Task Return_ForAnOrderThatIsNotDelivered_IsRefusedWithAMessage()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var (number, key) = ParseDone(await CheckoutAsync(client, NewEmail()));
        var detailsUrl = $"/en/Orders/Details?number={number}&key={key}";

        var response = await PostFormAsync(client, "/en/Orders/Lookup", "/en/Orders/RequestReturn", new Dictionary<string, string>
        {
            ["number"] = number.ToString(),
            ["key"] = key,
            ["reason"] = "I changed my mind"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("not possible", await client.GetStringAsync(detailsUrl));
    }

    [Fact]
    public async Task Return_WithAWrongKey_IsNotFound()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var (number, key) = ParseDone(await CheckoutAsync(client, NewEmail()));

        var response = await PostFormAsync(client, "/en/Orders/Lookup", "/en/Orders/RequestReturn", new Dictionary<string, string>
        {
            ["number"] = number.ToString(),
            ["key"] = "wrong",
            ["reason"] = "I changed my mind"
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

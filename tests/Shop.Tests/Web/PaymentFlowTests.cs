using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using Shop.Web.Payments;

namespace Shop.Tests.Web;

[Collection("Web")]
public class PaymentFlowTests : CheckoutTestBase
{
    // The value in appsettings.Development.json: the site started by the tests is in Development.
    private const string FakeSecret = "dev-only-fake-bank-secret";

    public PaymentFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    // What the checkout sent the customer to: the fake bank's page, and where the bank sends the customer back.
    private record BankPage(string PathAndQuery, string Reference, string ReturnPathAndQuery, int Number, string Key);

    private static BankPage ParseBankRedirect(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Contains("/en/FakeBank", location.AbsolutePath);

        var query = HttpUtility.ParseQueryString(location.Query);
        var returnUri = new Uri(query["returnUrl"]!);
        var returnQuery = HttpUtility.ParseQueryString(returnUri.Query);

        return new BankPage(location.PathAndQuery, query["reference"]!, returnUri.PathAndQuery, int.Parse(returnQuery["number"]!), returnQuery["key"]!);
    }

    private Task<HttpResponseMessage> CheckoutWithAsync(HttpClient client, string email, string method)
    {
        return CheckoutAsync(client, email, f => f["PaymentMethod"] = method);
    }

    // Presses a button on the fake bank's page.
    private Task<HttpResponseMessage> BankAnswerAsync(HttpClient client, BankPage bank, string result)
    {
        return PostFormAsync(client, bank.PathAndQuery, "/en/FakeBank/Complete", new Dictionary<string, string>
        {
            ["reference"] = bank.Reference,
            ["returnUrl"] = new Uri(new Uri("http://localhost"), bank.ReturnPathAndQuery).ToString(),
            ["result"] = result
        });
    }

    private static string SignedAnswer(string reference, string status, string amount) => FakePaymentProvider.Sign(FakeSecret, reference, status, amount);

    // The amount as the bank page shows it, read back from the checkout page's total.
    private static async Task<string> AmountOfAsync(HttpClient client, BankPage bank)
    {
        var html = await client.GetStringAsync(bank.PathAndQuery);
        var number = Regex.Match(html, @"<dt>Amount</dt><dd>([\d,]+)").Groups[1].Value.Replace(",", string.Empty);
        return number + ".00";
    }

    // ---- Card ----

    [Fact]
    public async Task Card_Checkout_GoesToTheBank_NoEmailYet_ThenPaidMeansOrdered()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var email = NewEmail();

        var bank = ParseBankRedirect(await CheckoutWithAsync(client, email, "Card"));

        // The bank's page shows the order and the amount; the order is waiting, and nothing was emailed.
        var page = await client.GetStringAsync(bank.PathAndQuery);
        Assert.Contains($"#{bank.Number}", page);
        await Fixture.Factory.FlushEmailsAsync();
        Assert.Equal(0, Fixture.Factory.Emails.CountTo(email));
        Assert.Contains("Awaiting payment", await client.GetStringAsync($"/en/Orders/Details?number={bank.Number}&key={bank.Key}"));

        // The customer pays: the bank answers the shop and sends the customer back.
        var back = await BankAnswerAsync(client, bank, "Paid");
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        var returned = await client.GetAsync(bank.ReturnPathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, returned.StatusCode);
        Assert.Contains("/en/Checkout/Done", returned.Headers.Location!.ToString());

        var done = await client.GetStringAsync($"/en/Checkout/Done?number={bank.Number}&key={bank.Key}");
        Assert.Contains("Thank you for your order", done);
        Assert.Contains("Paid", done);

        await Fixture.Factory.FlushEmailsAsync();
        Assert.Equal(1, Fixture.Factory.Emails.CountTo(email));
    }

    [Fact]
    public async Task Card_Declined_ShowsAFailureAndAllowsTryingAgain()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var email = NewEmail();
        var bank = ParseBankRedirect(await CheckoutWithAsync(client, email, "Card"));

        await BankAnswerAsync(client, bank, "Failed");
        var failedPage = await BodyAsync(await client.GetAsync(bank.ReturnPathAndQuery));
        Assert.Contains("Payment failed", failedPage);
        Assert.Contains("Pay now", failedPage);
        await Fixture.Factory.FlushEmailsAsync();
        Assert.Equal(0, Fixture.Factory.Emails.CountTo(email));

        // "Pay now" starts a new attempt at the bank.
        var retry = await PostFormAsync(client, bank.ReturnPathAndQuery, "/en/Payments/Start", new Dictionary<string, string>
        {
            ["number"] = bank.Number.ToString(),
            ["key"] = bank.Key
        });
        var secondBank = ParseBankRedirect(retry);
        Assert.NotEqual(bank.Reference, secondBank.Reference);

        await BankAnswerAsync(client, secondBank, "Paid");
        await Fixture.Factory.FlushEmailsAsync();
        Assert.Equal(1, Fixture.Factory.Emails.CountTo(email));
        Assert.Contains("Ordered", await client.GetStringAsync($"/en/Orders/Details?number={bank.Number}&key={bank.Key}"));
    }

    [Fact]
    public async Task Card_CancelledAtTheBank_ShowsThatMessage()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var bank = ParseBankRedirect(await CheckoutWithAsync(client, NewEmail(), "Card"));

        await BankAnswerAsync(client, bank, "Cancelled");

        Assert.Contains("Payment cancelled", await client.GetStringAsync(bank.ReturnPathAndQuery));
    }

    [Fact]
    public async Task ComingBackFromTheBankWithoutAnAnswer_ShowsWaiting_NotPaid()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var bank = ParseBankRedirect(await CheckoutWithAsync(client, NewEmail(), "Card"));

        // The customer is back, but the bank has not told the shop anything: typing the address does not make an order paid.
        var page = await client.GetStringAsync(bank.ReturnPathAndQuery);

        Assert.Contains("Waiting for the bank", page);
        Assert.Contains("Awaiting payment", await client.GetStringAsync($"/en/Orders/Details?number={bank.Number}&key={bank.Key}"));
    }

    [Fact]
    public async Task OrderPage_OfAnUnpaidCardOrder_OffersPayNow()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var bank = ParseBankRedirect(await CheckoutWithAsync(client, NewEmail(), "Card"));

        var page = await client.GetStringAsync($"/en/Orders/Details?number={bank.Number}&key={bank.Key}");

        Assert.Contains("Pay now", page);
        Assert.Contains("Please pay by", page);
    }

    [Fact]
    public async Task Return_WithAWrongKey_AndStart_WithoutTheKey_AreNotFound()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var bank = ParseBankRedirect(await CheckoutWithAsync(client, NewEmail(), "Card"));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/en/Payments/Return?number={bank.Number}&key=wrong")).StatusCode);

        var stranger = NewClient();
        var start = await PostFormAsync(stranger, "/en/Orders/Lookup", "/en/Payments/Start", new Dictionary<string, string>
        {
            ["number"] = bank.Number.ToString(),
            ["key"] = string.Empty
        });
        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
    }

    [Fact]
    public async Task TheFakeBankPage_ForAnUnknownPayment_IsNotFound()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await NewClient().GetAsync("/en/FakeBank?reference=nothing")).StatusCode);
    }

    // ---- The bank's own message to the shop ----

    private async Task<HttpResponseMessage> SendCallbackAsync(HttpClient client, Dictionary<string, string> fields)
    {
        // The bank's server: no cookies, no anti-forgery token.
        return await client.PostAsync("/en/Payments/Callback", new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task Callback_WithAValidSignature_MarksTheOrderPaid_AndSendsTheEmailsOnce()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var email = NewEmail();
        var bank = ParseBankRedirect(await CheckoutWithAsync(client, email, "Card"));
        var amount = await AmountOfAsync(client, bank);
        var fields = new Dictionary<string, string>
        {
            ["reference"] = bank.Reference,
            ["status"] = "Paid",
            ["amount"] = amount,
            ["signature"] = SignedAnswer(bank.Reference, "Paid", amount)
        };

        var bankServer = Fixture.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await SendCallbackAsync(bankServer, fields)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendCallbackAsync(bankServer, fields)).StatusCode); // sent twice

        Assert.Contains("Ordered", await client.GetStringAsync($"/en/Orders/Details?number={bank.Number}&key={bank.Key}"));
        await Fixture.Factory.FlushEmailsAsync();
        Assert.Equal(1, Fixture.Factory.Emails.CountTo(email));
    }

    [Fact]
    public async Task Callback_WithAForgedSignature_OrAWrongAmount_IsRefused_AndChangesNothing()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var email = NewEmail();
        var bank = ParseBankRedirect(await CheckoutWithAsync(client, email, "Card"));
        var amount = await AmountOfAsync(client, bank);
        var bankServer = NewClient();

        var forged = await SendCallbackAsync(bankServer, new Dictionary<string, string>
        {
            ["reference"] = bank.Reference,
            ["status"] = "Paid",
            ["amount"] = amount,
            ["signature"] = "0000"
        });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        var cheaper = await SendCallbackAsync(bankServer, new Dictionary<string, string>
        {
            ["reference"] = bank.Reference,
            ["status"] = "Paid",
            ["amount"] = "1.00",
            ["signature"] = SignedAnswer(bank.Reference, "Paid", "1.00")
        });
        Assert.Equal(HttpStatusCode.BadRequest, cheaper.StatusCode);

        Assert.Contains("Awaiting payment", await client.GetStringAsync($"/en/Orders/Details?number={bank.Number}&key={bank.Key}"));
        await Fixture.Factory.FlushEmailsAsync();
        Assert.Equal(0, Fixture.Factory.Emails.CountTo(email));
    }

    [Fact]
    public async Task Callback_ForAnUnknownPayment_OrWithoutAForm_IsRefused()
    {
        var bankServer = NewClient();

        var unknown = await SendCallbackAsync(bankServer, new Dictionary<string, string>
        {
            ["reference"] = "unknown",
            ["status"] = "Paid",
            ["amount"] = "100.00",
            ["signature"] = SignedAnswer("unknown", "Paid", "100.00")
        });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var noForm = await bankServer.PostAsync("/en/Payments/Callback", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, noForm.StatusCode);
    }

    // ---- Bank transfer ----

    [Fact]
    public async Task BankTransfer_ShowsTheBankDetails_AndEmailsThemAtOnce()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var email = NewEmail();

        var (number, key) = ParseDone(await CheckoutWithAsync(client, email, "BankTransfer"));

        var done = await client.GetStringAsync($"/en/Checkout/Done?number={number}&key={key}");
        Assert.Contains("Bank transfer details", done);
        Assert.Contains("Sample Bank", done);
        Assert.Contains("AM00", done);
        Assert.Contains($"Order #{number}", done); // the payment purpose
        Assert.Contains("Awaiting payment", done);

        await Fixture.Factory.FlushEmailsAsync();
        Assert.Equal(1, Fixture.Factory.Emails.CountTo(email));
        await Fixture.Factory.FlushEmailsAsync();
        var mail = Fixture.Factory.Emails.LastHtmlTo(email);
        Assert.Contains("Bank transfer details", mail);
        Assert.Contains("AM00", mail);
    }

    [Fact]
    public async Task BankTransfer_EmailIsWrittenInTheOrdersLanguage()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var email = NewEmail();

        // The checkout is in Russian, so the confirmation is in Russian too.
        var (total, slotId) = await ReadCheckoutPageAsyncIn(client, "ru");
        var fields = Fields(total, slotId, email);
        fields["PaymentMethod"] = "BankTransfer";
        var response = await PostFormAsync(client, "/ru/Checkout", "/ru/Checkout", fields);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // The email encodes non-Latin letters as HTML entities; a mail program shows them as the letters.
        await Fixture.Factory.FlushEmailsAsync();
        Assert.Contains("Реквизиты для банковского перевода", WebUtility.HtmlDecode(Fixture.Factory.Emails.LastHtmlTo(email)));
    }

    private static async Task<(string Total, string SlotId)> ReadCheckoutPageAsyncIn(HttpClient client, string culture)
    {
        var html = await client.GetStringAsync($"/{culture}/Checkout");
        var total = Regex.Match(html, "name=\"ExpectedTotal\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var slot = Regex.Match(html, "name=\"DeliverySlotId\"[^>]*value=\"(\\d+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(total));
        Assert.False(string.IsNullOrEmpty(slot));
        return (total, slot);
    }

    [Fact]
    public async Task CheckoutPage_OffersCardAndBankTransfer_InDevelopment()
    {
        var client = NewClient();
        await AddToCartAsync(client, await PlainProductAsync());

        var html = await client.GetStringAsync("/en/Checkout");

        Assert.Contains("value=\"Card\"", html);
        Assert.Contains("value=\"BankTransfer\"", html);
        Assert.Contains("value=\"CashOnDelivery\"", html);
        Assert.DoesNotContain("Coming soon", html);
    }
}

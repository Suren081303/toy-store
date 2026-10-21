using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shop.Core.Entities;
using Shop.Core.Notifications;
using Shop.Core.Payments;
using Shop.Infrastructure.Data;
using Shop.Web.Payments;

namespace Shop.Tests.Web;

// The email queue, the texts of the emails, and the admins' bell, through the real site.
[Collection("Web")]
public class NotificationFlowTests : CheckoutTestBase
{
    private const string ShopAddress = "shop@example.com"; // Shop:Email in appsettings.json
    private const string FakeSecret = "dev-only-fake-bank-secret";

    public NotificationFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    private static KeyValuePair<string, string> F(string key, string value) => new(key, value);

    private static string Decoded(string html) => WebUtility.HtmlDecode(html);

    private async Task<HttpClient> AdminAsync()
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword)).StatusCode);
        return client;
    }

    private async Task<T> InScopeAsync<T>(Func<ShopDbContext, Task<T>> action, WebApplicationFactory<Program>? factory = null)
    {
        using var scope = (factory ?? Fixture.Factory).Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ShopDbContext>());
    }

    // A guest places an order. method: CashOnDelivery, BankTransfer or Card (Card ends at the fake bank page).
    private async Task<(int Number, string Key, string Email)> PlaceAsync(string method = "CashOnDelivery", string culture = "en", HttpClient? client = null)
    {
        client ??= NewClient();
        await AddToCartAsync(client, await PlainProductAsync());
        var email = NewEmail();

        var html = await client.GetStringAsync($"/{culture}/Checkout");
        var fields = Fields(
            Regex.Match(html, "name=\"ExpectedTotal\"[^>]*value=\"([^\"]+)\"").Groups[1].Value,
            Regex.Match(html, "name=\"DeliverySlotId\"[^>]*value=\"(\\d+)\"").Groups[1].Value,
            email);
        fields["PaymentMethod"] = method;
        var response = await PostFormAsync(client, $"/{culture}/Checkout", $"/{culture}/Checkout", fields);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!.ToString();
        var done = Regex.Match(location, @"number=(\d+)&key=([^&]+)");
        if (done.Success)
        {
            return (int.Parse(done.Groups[1].Value), done.Groups[2].Value, email);
        }

        // Card: the redirect goes to the fake bank, and the order's number and key are in the "return" address inside it.
        var returnUrl = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query)["returnUrl"]!;
        var back = System.Web.HttpUtility.ParseQueryString(new Uri(returnUrl).Query);
        return (int.Parse(back["number"]!), back["key"]!, email);
    }

    private static Task<HttpResponseMessage> AdminPostAsync(HttpClient admin, string formPage, string action, params KeyValuePair<string, string>[] fields) =>
        PostFormListAsync(admin, formPage, "/en/Admin/" + action, fields);

    private async Task<List<OutboxEmail>> QueueAsync(int orderNumber) =>
        await InScopeAsync(db => db.OutboxEmails.AsNoTracking().Where(e => e.OrderNumber == orderNumber).OrderBy(e => e.Id).ToListAsync());

    private async Task MakeAllDueAsync(int orderNumber) =>
        await InScopeAsync(async db =>
        {
            await db.OutboxEmails.Where(e => e.OrderNumber == orderNumber && e.Status == OutboxStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
            return 0;
        });

    private IEnumerable<(string Subject, string Html)> ShopMailsAbout(int orderNumber) =>
        Fixture.Factory.Emails.To(ShopAddress).Where(m => m.Html.Contains($"#{orderNumber}") || m.Subject.Contains($"#{orderNumber}"));

    private async Task<(string Subject, string Html)> LastCustomerMailAsync(string email)
    {
        await Fixture.Factory.FlushEmailsAsync();
        return Fixture.Factory.Emails.To(email).Last();
    }

    // ---- Queue: nothing is sent while the request runs ----

    [Fact]
    public async Task AnOrder_QueuesItsEmails_AndTheSenderDeliversThem_WithCompleteLinks()
    {
        var (number, key, email) = await PlaceAsync();

        // Queued, not sent yet.
        Assert.Equal(0, Fixture.Factory.Emails.CountTo(email));
        Assert.Equal([EmailKind.OrderConfirmation, EmailKind.ShopNewOrder], (await QueueAsync(number)).Select(e => e.Kind));

        await Fixture.Factory.FlushEmailsAsync();

        var customer = Assert.Single(Fixture.Factory.Emails.To(email));
        Assert.Equal($"Order #{number} - WONDER", customer.Subject);
        Assert.Contains($"http://localhost/en/Checkout/Done?number={number}&amp;key={key}", customer.Html); // a complete address

        // The shop's notice is in Armenian and links to the admin page.
        var shop = Assert.Single(ShopMailsAbout(number));
        Assert.Contains("Նոր պատվեր", Decoded(shop.Subject));
        Assert.Contains($"http://localhost/hy/Admin/Orders/Details?number={number}", Decoded(shop.Html));

        Assert.All(await QueueAsync(number), e =>
        {
            Assert.Equal(OutboxStatus.Sent, e.Status);
            Assert.NotNull(e.SentAt);
            Assert.NotNull(e.ToAddress);
        });

        // Sending again does nothing.
        await Fixture.Factory.FlushEmailsAsync();
        Assert.Equal(1, Fixture.Factory.Emails.CountTo(email));
    }

    [Fact]
    public async Task ARussianCustomer_GetsRussianEmails_WhileTheShopStillGetsArmenian()
    {
        var (number, key, email) = await PlaceAsync(culture: "ru");
        var admin = await AdminAsync();

        await AdminPostAsync(admin, $"/en/Admin/Orders/Details?number={number}", "Orders/ChangeStatus", F("number", number.ToString()), F("status", "Packed"));
        await Fixture.Factory.FlushEmailsAsync();

        var mails = Fixture.Factory.Emails.To(email);
        Assert.Equal(2, mails.Count); // confirmation, packed
        Assert.Contains("упакован", Decoded(mails[1].Subject));
        Assert.Contains($"http://localhost/ru/Checkout/Done?number={number}", mails[1].Html);
        Assert.Contains("Նոր պատվեր", Decoded(Assert.Single(ShopMailsAbout(number)).Subject));
    }

    // ---- Status news ----

    [Fact]
    public async Task Packing_AndDelivering_EmailTheCustomer()
    {
        var (number, key, email) = await PlaceAsync();
        var admin = await AdminAsync();
        var page = $"/en/Admin/Orders/Details?number={number}";

        await AdminPostAsync(admin, page, "Orders/ChangeStatus", F("number", number.ToString()), F("status", "Packed"));
        var packed = await LastCustomerMailAsync(email);
        Assert.Equal($"Order #{number} is packed", packed.Subject);
        Assert.Contains("We packed order", Decoded(packed.Html));
        Assert.Contains(ShopClockText(DateTime.UtcNow, 1), Decoded(packed.Html)); // the delivery day chosen at checkout (tomorrow)

        await AdminPostAsync(admin, page, "Orders/ChangeStatus", F("number", number.ToString()), F("status", "Delivered"));
        var delivered = await LastCustomerMailAsync(email);
        Assert.Equal($"Order #{number} was delivered", delivered.Subject);
        Assert.Contains("ask for a return until", Decoded(delivered.Html));
        Assert.Contains(ShopClockText(DateTime.UtcNow, 14), Decoded(delivered.Html)); // the return window is 14 days
        Assert.Contains($"number={number}&amp;key={key}", delivered.Html);
    }

    // The Armenian calendar day, some days from now, as the emails write it ("19 October 2026").
    private static string ShopClockText(DateTime nowUtc, int daysAhead) =>
        Shop.Core.Orders.ShopClock.ToShopTime(nowUtc).AddDays(daysAhead).ToString("d MMMM yyyy", new System.Globalization.CultureInfo("en"));

    [Fact]
    public async Task Cancelling_EmailsTheCustomer_WithTheAdminsNote()
    {
        var (number, _, email) = await PlaceAsync();
        var admin = await AdminAsync();

        await AdminPostAsync(admin, $"/en/Admin/Orders/Details?number={number}", "Orders/ChangeStatus",
            F("number", number.ToString()), F("status", "Cancelled"), F("note", "Sorry, the supplier is out of stock <b>today</b>"));

        var mail = await LastCustomerMailAsync(email);
        Assert.Equal($"Order #{number} was cancelled", mail.Subject);
        Assert.Contains("Sorry, the supplier is out of stock <b>today</b>", Decoded(mail.Html)); // the note is shown, and encoded safely
        Assert.DoesNotContain("<b>today</b>", mail.Html);
        Assert.DoesNotContain("ran out", mail.Html);
    }

    [Fact]
    public async Task ATimedOutCardOrder_EmailsTheCustomer_AboutTheTimeout()
    {
        var (number, _, email) = await PlaceAsync("Card");
        Assert.Empty(await QueueAsync(number)); // card: nothing queued before payment

        // Make the order old enough, then run the expiry job's work.
        await InScopeAsync(async db =>
        {
            await db.Orders.Where(o => o.Number == number).ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedAt, DateTime.UtcNow.AddHours(-2)));
            return 0;
        });
        using (var scope = Fixture.Factory.Services.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IPaymentService>().ExpireUnpaidOrdersAsync() >= 1);
        }

        var mail = await LastCustomerMailAsync(email);
        Assert.Equal($"Order #{number} was cancelled", mail.Subject);
        Assert.Contains($"The time to pay for order #{number} ran out", Decoded(mail.Html));
    }

    [Fact]
    public async Task ABankTransferReceived_EmailsTheCustomer()
    {
        var (number, _, email) = await PlaceAsync("BankTransfer");
        await Fixture.Factory.FlushEmailsAsync();
        var admin = await AdminAsync();

        await AdminPostAsync(admin, $"/en/Admin/Orders/Details?number={number}", "Orders/MarkReceived", F("number", number.ToString()));

        var mail = await LastCustomerMailAsync(email);
        Assert.Equal($"We received your payment for order #{number}", mail.Subject);
        Assert.Equal(2, Fixture.Factory.Emails.CountTo(email)); // the confirmation with the bank details, then this one
    }

    // ---- Returns ----

    [Fact]
    public async Task AReturn_IsAnnouncedToTheShop_AndEachAnswerEmailsTheCustomer()
    {
        var (number, key, email) = await PlaceAsync();
        var admin = await AdminAsync();
        var order = $"/en/Admin/Orders/Details?number={number}";
        foreach (var status in new[] { "Packed", "Delivered" })
        {
            await AdminPostAsync(admin, order, "Orders/ChangeStatus", F("number", number.ToString()), F("status", status));
        }

        // The customer asks for a return.
        var customer = NewClient();
        await PostFormListAsync(customer, $"/en/Orders/Details?number={number}&key={key}", "/en/Orders/RequestReturn",
            [F("number", number.ToString()), F("key", key), F("reason", "The wheel is <i>broken</i>")]);
        await Fixture.Factory.FlushEmailsAsync();

        var returnId = int.Parse(Regex.Match(await admin.GetStringAsync(order), @"/Returns/Details/(\d+)").Groups[1].Value);
        var shopMail = ShopMailsAbout(number).Single(m => Decoded(m.Subject).Contains("Վերադարձ"));
        Assert.Contains("The wheel is <i>broken</i>", Decoded(shopMail.Html));
        Assert.DoesNotContain("<i>broken</i>", shopMail.Html);
        Assert.Contains($"http://localhost/hy/Admin/Returns/Details/{returnId}", Decoded(shopMail.Html));

        var returnPage = $"/en/Admin/Returns/Details/{returnId}";
        await AdminPostAsync(admin, returnPage, "Returns/Change", F("id", returnId.ToString()), F("status", "Returned"), F("note", "Please keep the box"), F("restock", "true"));
        var received = await LastCustomerMailAsync(email);
        Assert.Equal($"Return for order #{number}: we received the goods", received.Subject);
        Assert.Contains("Please keep the box", Decoded(received.Html));

        await AdminPostAsync(admin, returnPage, "Returns/Change", F("id", returnId.ToString()), F("status", "Refunded"));
        var refunded = await LastCustomerMailAsync(email);
        Assert.Equal($"Return for order #{number}: money sent back", refunded.Subject);
    }

    [Fact]
    public async Task ARejectedReturn_EmailsTheCustomer_WithTheReason()
    {
        var (number, key, email) = await PlaceAsync();
        var admin = await AdminAsync();
        foreach (var status in new[] { "Packed", "Delivered" })
        {
            await AdminPostAsync(admin, $"/en/Admin/Orders/Details?number={number}", "Orders/ChangeStatus", F("number", number.ToString()), F("status", status));
        }

        await PostFormListAsync(NewClient(), "/en/Orders/Lookup", "/en/Orders/RequestReturn",
            [F("number", number.ToString()), F("key", key), F("reason", "I changed my mind about the colour")]);
        var returnId = int.Parse(Regex.Match(await admin.GetStringAsync($"/en/Admin/Orders/Details?number={number}"), @"/Returns/Details/(\d+)").Groups[1].Value);

        await AdminPostAsync(admin, $"/en/Admin/Returns/Details/{returnId}", "Returns/Change", F("id", returnId.ToString()), F("status", "Rejected"), F("note", "A different colour is not a reason"));

        var mail = await LastCustomerMailAsync(email);
        Assert.Equal($"Your return request for order #{number}", mail.Subject);
        Assert.Contains("A different colour is not a reason", Decoded(mail.Html));
    }

    // ---- Money that cannot be used ----

    [Fact]
    public async Task ALatePayment_EmailsTheShop_AndRingsTheBell()
    {
        var (number, key, _) = await PlaceAsync("Card");
        var admin = await AdminAsync();
        await AdminPostAsync(admin, $"/en/Admin/Orders/Details?number={number}", "Orders/ChangeStatus", F("number", number.ToString()), F("status", "Cancelled"));

        // The bank's answer arrives after the cancel (its reference is on the open payment attempt).
        var attempt = await InScopeAsync(async db => await db.Payments.Where(p => p.Order.Number == number).Select(p => new { p.Reference, p.Amount }).SingleAsync());
        var amount = attempt.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var callback = await NewClient().PostAsync("/en/Payments/Callback", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["reference"] = attempt.Reference,
            ["status"] = "Paid",
            ["amount"] = amount,
            ["signature"] = FakePaymentProvider.Sign(FakeSecret, attempt.Reference, "Paid", amount)
        }));
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        await Fixture.Factory.FlushEmailsAsync();

        var mail = ShopMailsAbout(number).Single(m => Decoded(m.Subject).Contains("վերադարձնել"));
        Assert.Contains($"http://localhost/hy/Admin/Orders/Details?number={number}", Decoded(mail.Html));

        var bell = Decoded(await admin.GetStringAsync("/en/Admin/Notifications"));
        Assert.Contains($"Refund needed: ", bell);
        Assert.Contains($"for order #{number}", bell);
    }

    // ---- When sending fails ----

    [Fact]
    public async Task ASendFailure_DoesNotBreakTheOrder_IsRecorded_AndRetriedLater()
    {
        Fixture.Factory.Emails.FailWith("Mail server is down");
        try
        {
            var (number, key, email) = await PlaceAsync(); // the order works although mail is down
            Assert.Equal(HttpStatusCode.OK, (await NewClient().GetAsync($"/en/Checkout/Done?number={number}&key={key}")).StatusCode);

            await Fixture.Factory.FlushEmailsAsync();

            var failed = await QueueAsync(number);
            Assert.All(failed, e =>
            {
                Assert.Equal(OutboxStatus.Pending, e.Status);
                Assert.Equal(1, e.Attempts);
                Assert.Contains("Mail server is down", e.LastError);
                Assert.True(e.NextAttemptAt > DateTime.UtcNow.AddSeconds(30)); // the next try is about a minute away
            });
            Assert.Equal(0, Fixture.Factory.Emails.CountTo(email));

            // Flushing again at once does not try again (it is not time yet).
            await Fixture.Factory.FlushEmailsAsync();
            Assert.All(await QueueAsync(number), e => Assert.Equal(1, e.Attempts));

            // The mail server is back, and the time has come.
            Fixture.Factory.Emails.FailWith(null);
            await MakeAllDueAsync(number);
            await Fixture.Factory.FlushEmailsAsync();

            Assert.Equal(1, Fixture.Factory.Emails.CountTo(email));
            Assert.All(await QueueAsync(number), e =>
            {
                Assert.Equal(OutboxStatus.Sent, e.Status);
                Assert.Equal(2, e.Attempts);
                Assert.Null(e.LastError);
            });
        }
        finally
        {
            Fixture.Factory.Emails.FailWith(null);
        }
    }

    [Fact]
    public async Task AfterFiveFailures_TheEmailIsFailed_ShownToTheAdmin_AndCanBeRetried()
    {
        var admin = await AdminAsync();
        Fixture.Factory.Emails.FailWith("Mailbox full");
        try
        {
            var (number, _, email) = await PlaceAsync();

            for (var round = 1; round <= OutboxRules.MaxAttempts; round++)
            {
                await MakeAllDueAsync(number);
                await Fixture.Factory.FlushEmailsAsync();
            }

            Assert.All(await QueueAsync(number), e =>
            {
                Assert.Equal(OutboxStatus.Failed, e.Status);
                Assert.Equal(OutboxRules.MaxAttempts, e.Attempts);
            });

            // No more tries, even when "due".
            await MakeAllDueAsync(number);
            await Fixture.Factory.FlushEmailsAsync();
            Assert.All(await QueueAsync(number), e => Assert.Equal(OutboxRules.MaxAttempts, e.Attempts));

            // The admin sees it on the dashboard and in the list, with the error.
            Assert.Matches(@"admin-card admin-card--alert[^>]*href=""/en/Admin/Emails\?status=Failed""", await admin.GetStringAsync("/en/Admin"));
            var list = Decoded(await admin.GetStringAsync("/en/Admin/Emails?status=Failed"));
            Assert.Contains("Mailbox full", list);
            Assert.Contains($"#{number}", list);
            Assert.Contains("Retry", list);

            // Retry, with the mail server back.
            Fixture.Factory.Emails.FailWith(null);
            var failedId = (await QueueAsync(number)).First(e => e.Kind == EmailKind.OrderConfirmation).Id;
            var retry = await AdminPostAsync(admin, "/en/Admin/Emails?status=Failed", "Emails/Retry", F("id", failedId.ToString()), F("status", "Failed"));
            Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
            Assert.Contains("The email is back in the queue.", Decoded(await admin.GetStringAsync("/en/Admin/Emails?status=Failed")));

            await Fixture.Factory.FlushEmailsAsync();
            Assert.Equal(1, Fixture.Factory.Emails.CountTo(email));
        }
        finally
        {
            Fixture.Factory.Emails.FailWith(null);
            await InScopeAsync(async db =>
            {
                await db.OutboxEmails.Where(e => e.Status == OutboxStatus.Failed).ExecuteDeleteAsync(); // do not leave failed rows for other tests
                return 0;
            });
        }
    }

    [Fact]
    public async Task TwoSendersAtTheSameMoment_SendEachEmailOnce()
    {
        var (_, _, email) = await PlaceAsync();

        await Task.WhenAll(Fixture.Factory.FlushEmailsAsync(), Fixture.Factory.FlushEmailsAsync(), Fixture.Factory.FlushEmailsAsync());

        Assert.Equal(1, Fixture.Factory.Emails.CountTo(email));
    }

    [Fact]
    public async Task AMissingBaseUrl_IsRecordedOnTheEmail_NotSentWithBrokenLinks()
    {
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Shop:BaseUrl"] = "" })));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var (number, _, email) = await PlaceAsync(client: client);

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEmailOutboxProcessor>().ProcessAsync();
        }

        var rows = await QueueAsync(number);
        Assert.All(rows, e =>
        {
            Assert.Equal(1, e.Attempts);
            Assert.Contains("Shop:BaseUrl is not configured", e.LastError);
        });
        Assert.Equal(0, Fixture.Factory.Emails.CountTo(email));

        // Tidy up: do not let these rows be sent later by another test.
        await InScopeAsync(async db =>
        {
            await db.OutboxEmails.Where(e => e.OrderNumber == number).ExecuteDeleteAsync();
            return 0;
        });
    }

    // ---- The bell ----

    [Theory]
    [InlineData("/en/Admin/Notifications")]
    [InlineData("/ru/Admin/Emails")]
    public async Task TheBellAndEmailPages_RefuseVisitorsAndCustomers(string url)
    {
        Assert.Contains("/Account/Login", (await NewClient().GetAsync(url)).Headers.Location!.ToString());

        var customer = NewClient();
        await SignedInUserAsync(customer);
        Assert.Contains("/Account/AccessDenied", (await customer.GetAsync(url)).Headers.Location!.ToString());
    }

    [Fact]
    public async Task TheBellSummary_IsForAdminsOnly_AndSaysHowManyAreUnread()
    {
        // A visitor or a customer asking for JSON gets a plain 401 / 403, not a page.
        async Task<HttpStatusCode> AskAsync(HttpClient client)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/en/Admin/Notifications/Summary");
            request.Headers.Add("Accept", "application/json");
            return (await client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await AskAsync(NewClient()));
        var customer = NewClient();
        await SignedInUserAsync(customer);
        Assert.Equal(HttpStatusCode.Forbidden, await AskAsync(customer));

        var admin = await AdminAsync();
        await admin.GetAsync("/en/Admin/Notifications"); // warm up
        await PostFormListAsync(admin, "/en/Admin/Notifications", "/en/Admin/Notifications/MarkAllRead", []);
        var before = await UnreadAsync(admin);
        Assert.Equal(0, before);

        await PlaceAsync();
        await PlaceAsync();
        Assert.Equal(2, await UnreadAsync(admin));

        var response = await admin.GetAsync("/en/Admin/Notifications/Summary");
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    private static async Task<int> UnreadAsync(HttpClient admin)
    {
        var json = await admin.GetStringAsync("/en/Admin/Notifications/Summary");
        return int.Parse(Regex.Match(json, "\"unread\":(\\d+)").Groups[1].Value);
    }

    [Fact]
    public async Task TheBell_ShowsTheNumberInEveryAdminPage_ListsItems_AndOpeningOneMarksItRead()
    {
        var admin = await AdminAsync();
        await PostFormListAsync(admin, "/en/Admin/Notifications", "/en/Admin/Notifications/MarkAllRead", []);
        var (number, _, _) = await PlaceAsync();

        // The badge on an ordinary admin page.
        Assert.Matches(@"data-bell-count>1</span>", await admin.GetStringAsync("/en/Admin/Orders"));

        var page = Decoded(await admin.GetStringAsync("/en/Admin/Notifications"));
        Assert.Contains($"New order #{number}", page);
        Assert.Contains("admin-notif--unread", page);

        // The unread filter shows it too.
        Assert.Contains($"New order #{number}", Decoded(await admin.GetStringAsync("/en/Admin/Notifications?unread=true")));

        // "Open" goes to the order, and the item is read from then on.
        var id = Regex.Match(page, @"/Notifications/Open/(\d+)").Groups[1].Value;
        var open = await admin.GetAsync($"/en/Admin/Notifications/Open/{id}");
        Assert.Equal(HttpStatusCode.Redirect, open.StatusCode);
        Assert.Equal($"/en/Admin/Orders/Details?number={number}", open.Headers.Location!.OriginalString);
        Assert.Equal(0, await UnreadAsync(admin));
        Assert.DoesNotContain($"New order #{number}", Decoded(await admin.GetStringAsync("/en/Admin/Notifications?unread=true")));
        Assert.Contains($"New order #{number}", Decoded(await admin.GetStringAsync("/en/Admin/Notifications"))); // still in the full list

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/en/Admin/Notifications/Open/99999999")).StatusCode);
    }

    [Fact]
    public async Task MarkRead_AndMarkAllRead_WorkFromThePage()
    {
        var admin = await AdminAsync();
        await PostFormListAsync(admin, "/en/Admin/Notifications", "/en/Admin/Notifications/MarkAllRead", []);
        await PlaceAsync();
        await PlaceAsync();
        await PlaceAsync();
        Assert.Equal(3, await UnreadAsync(admin));

        var page = await admin.GetStringAsync("/en/Admin/Notifications");
        var firstId = Regex.Match(page, @"name=""id"" value=""(\d+)""").Groups[1].Value;
        await PostFormListAsync(admin, "/en/Admin/Notifications", "/en/Admin/Notifications/MarkRead", [F("id", firstId)]);
        Assert.Equal(2, await UnreadAsync(admin));

        var all = await PostFormListAsync(admin, "/en/Admin/Notifications", "/en/Admin/Notifications/MarkAllRead", []);
        Assert.Equal(HttpStatusCode.Redirect, all.StatusCode);
        Assert.Equal(0, await UnreadAsync(admin));
        Assert.Contains("All notifications are marked as read.", Decoded(await admin.GetStringAsync("/en/Admin/Notifications")));
    }

    [Fact]
    public async Task AReturnRequest_RingsTheBell_AndOpensTheReturn()
    {
        var (number, key, _) = await PlaceAsync();
        var admin = await AdminAsync();
        foreach (var status in new[] { "Packed", "Delivered" })
        {
            await AdminPostAsync(admin, $"/en/Admin/Orders/Details?number={number}", "Orders/ChangeStatus", F("number", number.ToString()), F("status", status));
        }

        await PostFormListAsync(NewClient(), "/en/Orders/Lookup", "/en/Orders/RequestReturn", [F("number", number.ToString()), F("key", key), F("reason", "It does not work")]);

        var page = Decoded(await admin.GetStringAsync("/en/Admin/Notifications"));
        Assert.Contains($"Return requested for order #{number}", page);

        var id = Regex.Matches(page, @"<li class=""admin-notif[^""]*"">[\s\S]*?</li>")
            .First(m => m.Value.Contains($"Return requested for order #{number}"))
            .Value;
        var openId = Regex.Match(id, @"/Notifications/Open/(\d+)").Groups[1].Value;
        var open = await admin.GetAsync($"/en/Admin/Notifications/Open/{openId}");
        Assert.Matches(@"^/en/Admin/Returns/Details/\d+$", open.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("hy")]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task BellAndEmailPages_Open_AndHaveNoUntranslatedKeys(string culture)
    {
        await PlaceAsync(); // so that there is something to show
        await Fixture.Factory.FlushEmailsAsync();
        var admin = await AdminAsync();

        foreach (var path in new[] { "/Notifications", "/Notifications?unread=true", "/Emails", "/Emails?status=Sent", "/Emails?status=Pending" })
        {
            var response = await admin.GetAsync($"/{culture}/Admin{path}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await BodyAsync(response);
            var text = Regex.Replace(Regex.Replace(html, "<script[\\s\\S]*?</script>", " "), "<[^>]+>", " ");
            Assert.Empty(Regex.Matches(text, @"\b(Admin|Notif|EmailKind|OutboxStatus|Email|Val)_[A-Za-z_]+\b").Select(m => m.Value).Distinct());
        }
    }

    [Fact]
    public async Task TheEmailsPage_ListsWhatWasSent_WithItsKindAndAddress()
    {
        var (number, _, email) = await PlaceAsync();
        await Fixture.Factory.FlushEmailsAsync();
        var admin = await AdminAsync();

        var page = Decoded(await admin.GetStringAsync("/en/Admin/Emails?status=Sent"));

        Assert.Contains("Order confirmation", page);
        Assert.Contains("New order (to the shop)", page);
        Assert.Contains(email, page);
        Assert.Contains($"number={number}", page); // the link to the order
    }
}

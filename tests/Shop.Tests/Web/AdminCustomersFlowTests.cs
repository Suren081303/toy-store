using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Core.Entities;
using Shop.Infrastructure.Data;

namespace Shop.Tests.Web;

// The Customers page of the admin panel as a browser uses it: the list, the forms in each row, and what the customer then sees.
[Collection("Web")]
public class AdminCustomersFlowTests : CheckoutTestBase
{
    public AdminCustomersFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    private static KeyValuePair<string, string> F(string key, string value) => new(key, value);

    private async Task<HttpClient> AdminClientAsync(string culture = "en")
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword)).StatusCode);
        return client;
    }

    private async Task<T> InDbAsync<T>(Func<ShopDbContext, UserManager<ApplicationUser>, Task<T>> action)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ShopDbContext>(), scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }

    private Task<string> IdOfAsync(string email) => InDbAsync(async (_, users) => (await users.FindByEmailAsync(email))!.Id);

    // A registered customer who is signed in on the returned client.
    private async Task<(HttpClient Client, string Email, string Id)> SignedInCustomerAsync()
    {
        var client = NewClient();
        var email = await SignedInUserAsync(client);
        return (client, email, await IdOfAsync(email));
    }

    // A registered customer who never confirmed the email.
    private async Task<(string Email, string Id)> UnconfirmedCustomerAsync()
    {
        var email = NewEmail();
        Assert.Equal(HttpStatusCode.Redirect, (await RegisterAsync(NewClient(), email)).StatusCode);
        return (email, await IdOfAsync(email));
    }

    private static string ListUrl(string email, string culture = "en") => $"/{culture}/Admin/Customers?Query={Uri.EscapeDataString(email)}";

    private Task<HttpResponseMessage> PostAsync(HttpClient admin, string email, string action, string culture, params KeyValuePair<string, string>[] fields) =>
        PostFormListAsync(admin, ListUrl(email, culture), $"/{culture}/Admin/Customers/{action}", fields);

    private static async Task<string> BarAsync(HttpClient admin, string url) => WebUtility.HtmlDecode(await admin.GetStringAsync(url));

    // The row's panel (everything from its id to the next row).
    private static string PanelOf(string html, string id)
    {
        var start = html.IndexOf($"id=\"more-{id}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "No panel for " + id);
        var end = html.IndexOf("</tr>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    // ---- Who may come in ----

    [Fact]
    public async Task AVisitorIsSentToSignIn_AndACustomerGetsAccessDenied_ForThePageAndEveryAction()
    {
        Assert.Contains("/Account/Login", (await NewClient().GetAsync("/en/Admin/Customers")).Headers.Location!.ToString());

        var (client, email, id) = await SignedInCustomerAsync();
        Assert.Contains("/Account/AccessDenied", (await client.GetAsync("/en/Admin/Customers")).Headers.Location!.ToString());

        foreach (var (action, fields) in new (string, KeyValuePair<string, string>[])[]
        {
            ("Block", [F("id", id)]),
            ("Unblock", [F("id", id)]),
            ("Discount", [F("id", id), F("percent", "50")]),
            ("Message", [F("id", id), F("subject", "S"), F("body", "B")])
        })
        {
            var response = await PostFormListAsync(client, "/en/Orders/Lookup", "/en/Admin/Customers/" + action, fields);
            Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());
        }

        // Nothing happened to the customer.
        var user = await InDbAsync((db, users) => users.FindByIdAsync(id));
        Assert.Null(user!.BlockedAt);
        Assert.Null(user.PersonalDiscountPercent);
        Assert.False(await InDbAsync((db, _) => db.CustomerMessages.AnyAsync(m => m.UserId == id)));
        Assert.NotNull(email);
    }

    // ---- The list ----

    [Fact]
    public async Task TheList_ShowsTheCustomerWithItsPanel_AndTheNavChip_ButNeverAnAdmin()
    {
        var (_, email, id) = await SignedInCustomerAsync();
        var admin = await AdminClientAsync();

        var html = await admin.GetStringAsync(ListUrl(email));

        Assert.Contains(email, html);
        Assert.Contains("Test User", html);
        Assert.Contains("+37491123456", WebUtility.HtmlDecode(html).Replace(" ", "")); // the phone as stored
        Assert.Contains("flag--ok", html);                                             // Active
        Assert.Contains($"href=\"#more-{id}\"", html);                                 // the "Manage" link
        Assert.Contains("href=\"/en/Admin/Customers\"", html);                         // the nav chip

        var panel = PanelOf(html, id);
        Assert.Contains("action=\"/en/Admin/Customers/Block\"", panel);
        Assert.Contains("action=\"/en/Admin/Customers/Discount\"", panel);
        Assert.Contains("action=\"/en/Admin/Customers/Message\"", panel);
        Assert.DoesNotContain("/Customers/Unblock", panel);
        Assert.Contains("name=\"percent\"", panel);
        Assert.Contains("maxlength=\"2000\"", panel);

        // The admin account does not appear, whatever is searched.
        Assert.Contains("Nothing found.", await admin.GetStringAsync(ListUrl(ShopWebFactory.AdminEmail)));
    }

    [Fact]
    public async Task TheList_SearchesAndFilters_AndAnUnconfirmedEmailHasNoMessageForm()
    {
        var (_, confirmedEmail, confirmedId) = await SignedInCustomerAsync();
        var (unconfirmedEmail, unconfirmedId) = await UnconfirmedCustomerAsync();
        var admin = await AdminClientAsync();

        var unconfirmed = await admin.GetStringAsync(ListUrl(unconfirmedEmail));
        Assert.Contains("Email not confirmed", unconfirmed);
        Assert.DoesNotContain(confirmedEmail, unconfirmed);
        var panel = PanelOf(unconfirmed, unconfirmedId);
        Assert.DoesNotContain("action=\"/en/Admin/Customers/Message\"", panel);
        Assert.Contains("is not confirmed", WebUtility.HtmlDecode(panel));

        await PostAsync(admin, confirmedEmail, "Block", "en", F("id", confirmedId), F("email", confirmedEmail));

        var blockedOnly = await admin.GetStringAsync($"/en/Admin/Customers?Query={Uri.EscapeDataString(confirmedEmail)}&Status=Blocked");
        Assert.Contains(confirmedEmail, blockedOnly);
        var activeOnly = await admin.GetStringAsync($"/en/Admin/Customers?Query={Uri.EscapeDataString(confirmedEmail)}&Status=Active");
        Assert.Contains("Nothing found.", activeOnly);

        // Every sort order opens.
        foreach (var sort in new[] { "Newest", "Name", "Orders", "Spent", "LastOrder" })
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/en/Admin/Customers?Sort={sort}")).StatusCode);
        }
    }

    [Theory]
    [InlineData("hy")]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task TheCustomersPage_OpensInEveryLanguage_WithNoUntranslatedKeys(string culture)
    {
        var (_, email, id) = await SignedInCustomerAsync();
        var admin = await AdminClientAsync();
        await PostAsync(admin, email, "Discount", "en", F("id", id), F("percent", "15"), F("until", "2099-01-31"));
        await PostAsync(admin, email, "Message", "en", F("id", id), F("email", email), F("subject", "Hello"), F("body", "Text"));
        await Fixture.Factory.FlushEmailsAsync();

        var response = await admin.GetAsync(ListUrl(email, culture));
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = Regex.Replace(Regex.Replace(html, "<script[\\s\\S]*?</script>", " "), "<[^>]+>", " ");
        Assert.Empty(Regex.Matches(text, @"\b(Admin|Val|Status|Pay|Label|OutboxStatus|EmailKind|Msg|Cart)_[A-Za-z_]+\b").Select(m => m.Value).Distinct());
        Assert.Contains("Hello", html); // the message just sent is in the panel
    }

    // ---- Block ----

    [Fact]
    public async Task Block_EndsTheirSessionAtOnce_RefusesTheSignIn_AndUnblockLetsThemBackIn()
    {
        var (client, email, id) = await SignedInCustomerAsync();
        var admin = await AdminClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/en/Account")).StatusCode); // signed in

        var block = await PostAsync(admin, email, "Block", "en", F("id", id), F("email", email), F("returnUrl", ListUrl(email)));

        Assert.Equal(HttpStatusCode.Redirect, block.StatusCode);
        Assert.Equal(ListUrl(email) + $"#more-{id}", block.Headers.Location!.OriginalString); // back to the list, panel open
        var after = await BarAsync(admin, ListUrl(email));
        Assert.Contains($"{email} is blocked.", after);
        Assert.Contains("flag--alert", after);
        Assert.Contains("action=\"/en/Admin/Customers/Unblock\"", PanelOf(after, id));

        // The customer's open session is over, on the very next request.
        var session = await client.GetAsync("/en/Account");
        Assert.Equal(HttpStatusCode.Redirect, session.StatusCode);
        Assert.Contains("/Account/Login", session.Headers.Location!.ToString());

        // The right password is refused with a clear message; a wrong password does not reveal anything.
        var refused = await LoginAsync(NewClient(), email, Password);
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("This account is blocked. Please contact the shop.", WebUtility.HtmlDecode(await BodyAsync(refused)));
        var wrong = await LoginAsync(NewClient(), email, "Wrong-Pass-999");
        Assert.Contains("Wrong email or password.", WebUtility.HtmlDecode(await BodyAsync(wrong)));
        Assert.DoesNotContain("blocked", await BodyAsync(wrong));

        // Unblock: signs in again.
        await PostAsync(admin, email, "Unblock", "en", F("id", id), F("email", email));
        Assert.Contains($"{email} is unblocked.", await BarAsync(admin, ListUrl(email)));
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(NewClient(), email, Password)).StatusCode);

        // Both actions are in the audit log, and the log links to this page.
        var audit = await admin.GetStringAsync("/en/Admin/Audit?Query=" + Uri.EscapeDataString(email));
        Assert.Contains("Customer.Blocked", audit);
        Assert.Contains("Customer.Unblocked", audit);
        Assert.Contains("href=\"/en/Admin/Customers\"", audit);
    }

    [Fact]
    public async Task Block_AGuestCheckoutWithTheSameEmail_IsNotStopped()
    {
        var (_, email, id) = await SignedInCustomerAsync();
        var admin = await AdminClientAsync();
        await PostAsync(admin, email, "Block", "en", F("id", id), F("email", email));

        var guest = NewClient();
        await AddToCartAsync(guest, await PlainProductAsync());
        var (number, _) = ParseDone(await CheckoutAsync(guest, email)); // a guest cannot be told apart

        Assert.True(number > 0);
    }

    [Fact]
    public async Task TheActions_RefuseAdminsUnknownAccounts_AndAnOutsideReturnAddress()
    {
        var (_, email, id) = await SignedInCustomerAsync();
        var admin = await AdminClientAsync();
        var adminId = await IdOfAsync(ShopWebFactory.AdminEmail);

        foreach (var action in new[] { "Block", "Unblock", "Discount", "Message" })
        {
            var forAdmin = await PostAsync(admin, email, action, "en", F("id", adminId), F("percent", "10"), F("subject", "S"), F("body", "B"));
            Assert.Equal(HttpStatusCode.NotFound, forAdmin.StatusCode);   // admins are managed on the Admins page

            var unknown = await PostAsync(admin, email, action, "en", F("id", "no-such-id"), F("percent", "10"), F("subject", "S"), F("body", "B"));
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        Assert.Null((await InDbAsync((db, users) => users.FindByIdAsync(adminId)))!.BlockedAt);

        var outside = await PostAsync(admin, email, "Block", "en", F("id", id), F("returnUrl", "https://evil.example/steal"));
        Assert.Equal(HttpStatusCode.Redirect, outside.StatusCode);
        Assert.Equal($"/en/Admin/Customers#more-{id}", outside.Headers.Location!.OriginalString);
    }

    // ---- Personal discount ----

    [Fact]
    public async Task Discount_IsSetInThePanel_ShowsInTheCartAndCheckout_AndIsStoredOnTheOrder()
    {
        var (client, email, id) = await SignedInCustomerAsync();
        var admin = await AdminClientAsync();

        var save = await PostAsync(admin, email, "Discount", "en", F("id", id), F("percent", "15"), F("until", "2099-12-31"), F("returnUrl", ListUrl(email)));
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);

        var list = await BarAsync(admin, ListUrl(email));
        Assert.Contains("Discount saved.", list);
        Assert.Contains("Discount 15% until", list);
        Assert.Contains("value=\"15\"", PanelOf(list, id));
        Assert.Contains("value=\"2099-12-31\"", PanelOf(list, id));

        // The customer sees it.
        await AddToCartAsync(client, await PlainProductAsync());
        var cart = WebUtility.HtmlDecode(await client.GetStringAsync("/en/Cart"));
        Assert.Contains("-15%", cart);
        Assert.Contains("Your personal discount of 15% is applied where it is lower.", cart);
        Assert.DoesNotContain("Discounted orders left", cart);
        var checkout = WebUtility.HtmlDecode(await client.GetStringAsync("/en/Checkout"));
        Assert.Contains("Your personal discount of 15%", checkout);

        // The order keeps the percent.
        var (number, _) = ParseDone(await CheckoutAsync(client, email));
        var stored = await InDbAsync((db, _) => db.Orders.Where(o => o.Number == number).Select(o => new { o.CustomerDiscountPercent, Item = o.Items.Select(i => i.CustomerDiscountPercent).First() }).SingleAsync());
        Assert.Equal(15, stored.CustomerDiscountPercent);
        Assert.Equal(15, stored.Item);

        // Removing it: the standard rule is back for the next order.
        await PostAsync(admin, email, "Discount", "en", F("id", id), F("percent", "0"));
        var removed = await BarAsync(admin, ListUrl(email));
        Assert.Contains("Discount removed.", removed);
        Assert.DoesNotContain("Discount 15%", removed);

        await AddToCartAsync(client, await PlainProductAsync());
        var standard = WebUtility.HtmlDecode(await client.GetStringAsync("/en/Cart"));
        Assert.Contains("-10%", standard);
        Assert.Contains("Discounted orders left", standard);
    }

    [Fact]
    public async Task Discount_BadValues_ShowARedBar_AndChangeNothing()
    {
        var (_, email, id) = await SignedInCustomerAsync();
        var admin = await AdminClientAsync();
        var yesterday = DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd");

        foreach (var (percent, until) in new[] { ("51", ""), ("-3", ""), ("20", yesterday) })
        {
            await PostAsync(admin, email, "Discount", "en", F("id", id), F("percent", percent), F("until", until));
            var html = await BarAsync(admin, ListUrl(email));
            Assert.Contains("Enter a percent from 1 to 50 and a last day that is not in the past.", html);
            Assert.Contains("alert--error", html);
        }

        Assert.Null((await InDbAsync((db, users) => users.FindByIdAsync(id)))!.PersonalDiscountPercent);

        await PostAsync(admin, email, "Discount", "en", F("id", id), F("percent", "0")); // nothing to remove
        Assert.Contains("Nothing to change.", await BarAsync(admin, ListUrl(email)));
    }

    // ---- Messages ----

    [Fact]
    public async Task Message_IsSentByEmail_AsPlainText_InTheLanguageOfTheAdminsPage()
    {
        var (_, email, id) = await SignedInCustomerAsync();
        var admin = await AdminClientAsync();
        const string body = "Line one <b>bold?</b> & more\n\nLine <script>alert(1)</script> two";

        var send = await PostAsync(admin, email, "Message", "ru", F("id", id), F("email", email), F("subject", "Новости <магазина>"), F("body", body), F("returnUrl", ListUrl(email, "ru")));

        Assert.Equal(HttpStatusCode.Redirect, send.StatusCode);
        var bar = await BarAsync(admin, ListUrl(email, "ru"));
        Assert.Contains(email, bar);
        Assert.Contains("alert--success", bar);

        await Fixture.Factory.FlushEmailsAsync();
        var sent = Assert.Single(Fixture.Factory.Emails.To(email).Where(e => e.Subject == "Новости <магазина>"));
        var html = sent.Html;
        Assert.Contains("lang=\"ru\"", html);
        Assert.Contains("Здравствуйте, Test User!", WebUtility.HtmlDecode(html));
        Assert.Contains("Открыть магазин", WebUtility.HtmlDecode(html));
        Assert.Contains("href=\"http://localhost/ru\"", html);
        Assert.Contains("Line one &lt;b&gt;bold?&lt;/b&gt; &amp; more", html);   // shown as typed, never as HTML
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.DoesNotContain("<b>bold?</b>", html);

        // The panel lists it with how the email went, and the email queue shows it without an order.
        var list = await BarAsync(admin, ListUrl(email, "ru"));
        var panel = PanelOf(list, id);
        Assert.Contains("Новости <магазина>", panel);
        Assert.Contains("flag--ok", panel); // sent
        var emails = await admin.GetStringAsync("/en/Admin/Emails");
        Assert.Contains("Message to a customer", emails);
    }

    [Fact]
    public async Task Message_InEnglish_UsesTheEnglishFrame()
    {
        var (_, email, id) = await SignedInCustomerAsync();
        var admin = await AdminClientAsync();

        await PostAsync(admin, email, "Message", "en", F("id", id), F("email", email), F("subject", "Your gift"), F("body", "Come and pick it up."));
        await Fixture.Factory.FlushEmailsAsync();

        var html = Fixture.Factory.Emails.LastHtmlTo(email);
        Assert.Contains("lang=\"en\"", html);
        Assert.Contains("Hello, Test User!", WebUtility.HtmlDecode(html));
        Assert.Contains("Come and pick it up.", html);
        Assert.Contains("Open the shop", html);
    }

    [Fact]
    public async Task Message_TheSixthInADay_AnEmptyOne_AndOneToAnUnconfirmedEmail_AreRefused()
    {
        var (_, email, id) = await SignedInCustomerAsync();
        var (unconfirmedEmail, unconfirmedId) = await UnconfirmedCustomerAsync();
        var admin = await AdminClientAsync();

        for (var i = 1; i <= 5; i++)
        {
            await PostAsync(admin, email, "Message", "en", F("id", id), F("email", email), F("subject", "No " + i), F("body", "Text"));
            Assert.Contains("is queued", await BarAsync(admin, ListUrl(email)));
        }

        await PostAsync(admin, email, "Message", "en", F("id", id), F("email", email), F("subject", "No 6"), F("body", "Text"));
        Assert.Contains("This customer already got 5 messages in the last 24 hours.", await BarAsync(admin, ListUrl(email)));

        await PostAsync(admin, email, "Message", "en", F("id", id), F("email", email), F("subject", ""), F("body", "Text"));
        Assert.Contains("Enter a subject (up to 150 characters) and a message (up to 2000 characters).", await BarAsync(admin, ListUrl(email)));

        await PostAsync(admin, unconfirmedEmail, "Message", "en", F("id", unconfirmedId), F("email", unconfirmedEmail), F("subject", "Hi"), F("body", "Text"));
        Assert.Contains("is not confirmed, so it cannot get messages.", await BarAsync(admin, ListUrl(unconfirmedEmail)));

        Assert.Equal(5, await InDbAsync((db, _) => db.CustomerMessages.CountAsync(m => m.UserId == id)));
        Assert.False(await InDbAsync((db, _) => db.CustomerMessages.AnyAsync(m => m.UserId == unconfirmedId)));
        await Fixture.Factory.FlushEmailsAsync();
        Assert.Equal(0, Fixture.Factory.Emails.To(unconfirmedEmail).Count(e => e.Subject == "Hi"));
    }

    [Fact]
    public async Task Message_AFailedEmail_ShowsAsFailedInThePanel_AndCanBeRetriedFromTheEmailsPage()
    {
        var (_, email, id) = await SignedInCustomerAsync();
        var admin = await AdminClientAsync();
        await PostAsync(admin, email, "Message", "en", F("id", id), F("email", email), F("subject", "Will fail"), F("body", "Text"));

        Fixture.Factory.Emails.FailWith("Mail server is down");
        try
        {
            // Five tries, each one due at once for this test.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await InDbAsync(async (db, _) =>
                {
                    await db.OutboxEmails.Where(e => e.CustomerMessageId != null && db.CustomerMessages.Any(m => m.Id == e.CustomerMessageId && m.UserId == id))
                        .ExecuteUpdateAsync(s => s.SetProperty(e => e.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1)));
                    return 0;
                });
                await Fixture.Factory.FlushEmailsAsync();
            }
        }
        finally
        {
            Fixture.Factory.Emails.FailWith(null);
        }

        var panel = PanelOf(await BarAsync(admin, ListUrl(email)), id);
        Assert.Contains("flag--alert", panel);
        Assert.Equal(0, Fixture.Factory.Emails.To(email).Count(e => e.Subject == "Will fail"));

        // The shop sees the error on the Emails page (an email of this kind has no order number).
        var emails = WebUtility.HtmlDecode(await admin.GetStringAsync("/en/Admin/Emails?status=Failed"));
        Assert.Contains("Mail server is down", emails);
    }
}

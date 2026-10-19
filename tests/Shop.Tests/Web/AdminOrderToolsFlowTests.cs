using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Infrastructure.Data;

namespace Shop.Tests.Web;

// The order pages of the admin as a browser uses them: notes, edit, packing slips and the phone search.
[Collection("Web")]
public class AdminOrderToolsFlowTests : CheckoutTestBase
{
    public AdminOrderToolsFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    private static KeyValuePair<string, string> F(string key, string value) => new(key, value);

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword)).StatusCode);
        return client;
    }

    private async Task<(int Number, string Key, string Email)> PlaceOrderAsync(string method = "CashOnDelivery")
    {
        var guest = NewClient();
        await AddToCartAsync(guest, await PlainProductAsync());
        var email = NewEmail();
        var (number, key) = ParseDone(await CheckoutAsync(guest, email, f => f["PaymentMethod"] = method));
        return (number, key, email);
    }

    private async Task<T> InDbAsync<T>(Func<ShopDbContext, Task<T>> action)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ShopDbContext>());
    }

    private static string DetailsUrl(int number, string culture = "en") => $"/{culture}/Admin/Orders/Details?number={number}";

    private static string EditUrl(int number, string culture = "en") => $"/{culture}/Admin/Orders/Edit?number={number}";

    // The values the edit page offers, read from its HTML.
    private static string Value(string html, string name)
    {
        var match = Regex.Match(html, $"name=\"{name}\"[^>]*value=\"([^\"]*)\"");
        if (!match.Success)
        {
            match = Regex.Match(html, $"value=\"([^\"]*)\"[^>]*name=\"{name}\"");
        }

        Assert.True(match.Success, "No field " + name);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    // The option values of a <select name="...">.
    private static List<string> Options(string html, string name)
    {
        var start = html.IndexOf($"name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "No select " + name);
        var end = html.IndexOf("</select>", start, StringComparison.Ordinal);
        return Regex.Matches(html[start..end], "<option value=\"([^\"]*)\"").Select(m => m.Groups[1].Value).Where(v => v.Length > 0).ToList();
    }

    private async Task<List<KeyValuePair<string, string>>> EditFieldsAsync(HttpClient admin, int number)
    {
        var html = await admin.GetStringAsync(EditUrl(number));
        var fields = new List<KeyValuePair<string, string>> { F("Number", number.ToString()) };
        foreach (var name in new[] { "CustomerName", "Phone", "Email", "City", "Street", "Building", "Apartment" })
        {
            fields.Add(F(name, Value(html, name)));
        }

        if (html.Contains("<select"))
        {
            fields.Add(F("Region", Regex.Match(html, "<option value=\"([^\"]+)\" selected").Groups[1].Value));
            fields.Add(F("DeliveryDate", Regex.Match(html, "<option value=\"(\\d{4}-\\d{2}-\\d{2})\" selected").Groups[1].Value));
            fields.Add(F("DeliverySlotId", Regex.Matches(html, "<option value=\"(\\d+)\" selected").Last().Groups[1].Value));
        }
        else
        {
            // A packed order: the page sends these back as hidden fields.
            foreach (var name in new[] { "Region", "DeliveryDate", "DeliverySlotId" })
            {
                fields.Add(F(name, Value(html, name)));
            }
        }

        fields.Add(F("Comment", string.Empty));
        return fields;
    }

    private static void Set(List<KeyValuePair<string, string>> fields, string name, string value)
    {
        fields.RemoveAll(f => f.Key == name);
        fields.Add(F(name, value));
    }

    // ---- Who may use it ----

    [Fact]
    public async Task VisitorsAndCustomers_CannotEditAddNotesOrPrint()
    {
        var (number, _, _) = await PlaceOrderAsync();
        var customer = NewClient();
        await SignedInUserAsync(customer);

        foreach (var url in new[] { EditUrl(number), $"/en/Admin/Orders/Slip?number={number}", "/en/Admin/Orders/Slips" })
        {
            Assert.Contains("/Account/Login", (await NewClient().GetAsync(url)).Headers.Location!.ToString());
            Assert.Contains("/Account/AccessDenied", (await customer.GetAsync(url)).Headers.Location!.ToString());
        }

        var note = await PostFormListAsync(customer, "/en/Orders/Lookup", "/en/Admin/Orders/AddNote", [F("number", number.ToString()), F("text", "hacked")]);
        var edit = await PostFormListAsync(customer, "/en/Orders/Lookup", "/en/Admin/Orders/Edit", [F("Number", number.ToString()), F("CustomerName", "Hacked")]);
        Assert.Contains("/Account/AccessDenied", note.Headers.Location!.ToString());
        Assert.Contains("/Account/AccessDenied", edit.Headers.Location!.ToString());
        Assert.False(await InDbAsync(db => db.OrderNotes.AnyAsync(n => n.Order.Number == number)));
    }

    // ---- The order page ----

    [Fact]
    public async Task TheOrderPage_OffersEdit_PrintSlip_AndNotes()
    {
        var (number, _, _) = await PlaceOrderAsync();
        var admin = await AdminClientAsync();

        var html = await admin.GetStringAsync(DetailsUrl(number));

        Assert.Contains($"href=\"/en/Admin/Orders/Edit?number={number}\"", html);
        Assert.Contains($"href=\"/en/Admin/Orders/Slip?number={number}\"", html);
        Assert.Contains("target=\"_blank\"", html);
        Assert.Contains("id=\"notes\"", html);
        Assert.Contains("No notes yet.", html);
        Assert.DoesNotContain("Phone order", html); // a website order
    }

    [Fact]
    public async Task Notes_AreAdded_ShownWithTheAuthor_CountedInTheList_AndNeverShownToTheCustomer()
    {
        var (number, key, _) = await PlaceOrderAsync();
        var admin = await AdminClientAsync();
        const string text = "Customer asked to call before delivery <b>twice</b>";

        var add = await PostFormListAsync(admin, DetailsUrl(number), "/en/Admin/Orders/AddNote", [F("number", number.ToString()), F("text", text)]);

        Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);
        var page = await admin.GetStringAsync(DetailsUrl(number));
        Assert.Contains("Note added.", page);
        Assert.Contains("Customer asked to call before delivery &lt;b&gt;twice&lt;/b&gt;", page); // shown as text
        Assert.Contains(ShopWebFactory.AdminEmail, page);
        Assert.DoesNotContain("No notes yet.", page);

        var list = await admin.GetStringAsync($"/en/Admin/Orders?Query={number}");
        Assert.Contains("1 notes", list);

        // The customer sees their order, but not the note.
        var customerPage = await NewClient().GetStringAsync($"/en/Orders/Details?number={number}&key={key}");
        Assert.DoesNotContain("Customer asked to call", customerPage);
        Assert.DoesNotContain("Internal notes", customerPage);

        // An empty note is refused with a clear message.
        await PostFormListAsync(admin, DetailsUrl(number), "/en/Admin/Orders/AddNote", [F("number", number.ToString()), F("text", "   ")]);
        Assert.Contains("Write a note (up to 1000 characters).", WebUtility.HtmlDecode(await admin.GetStringAsync(DetailsUrl(number))));
        Assert.Equal(HttpStatusCode.NotFound, (await PostFormListAsync(admin, DetailsUrl(number), "/en/Admin/Orders/AddNote", [F("number", "99999999"), F("text", "x")])).StatusCode);
    }

    // ---- Edit ----

    [Fact]
    public async Task Edit_ShowsTheSavedValues_SavesChanges_AndTheCustomerGetsTheNewDelivery()
    {
        var (number, key, email) = await PlaceOrderAsync();
        var admin = await AdminClientAsync();

        var page = await admin.GetStringAsync(EditUrl(number));
        Assert.Contains("Items and prices cannot be edited here.", page);
        Assert.Equal("Anna Hakobyan", Value(page, "CustomerName"));
        Assert.Equal("Abovyan", Value(page, "Street"));
        var days = Options(page, "DeliveryDate");
        var slots = Options(page, "DeliverySlotId");
        Assert.True(days.Count >= 2 && slots.Count >= 2);

        var fields = await EditFieldsAsync(admin, number);
        Set(fields, "Street", "New Street");
        Set(fields, "Building", "99");
        Set(fields, "Phone", "093 555777");
        Set(fields, "Comment", "Please call from the gate");
        Set(fields, "DeliveryDate", days.Last());
        Set(fields, "DeliverySlotId", slots.Last());

        var save = await PostFormListAsync(admin, EditUrl(number), "/en/Admin/Orders/Edit", fields);

        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        var details = WebUtility.HtmlDecode(await admin.GetStringAsync(DetailsUrl(number)));
        Assert.Contains("Order changed.", details);
        Assert.Contains("New Street 99", details);
        Assert.Contains("+37493555777", details);
        Assert.Contains("Please call from the gate", details);
        Assert.Contains("Edited:", details); // in the history

        // The customer sees the new data and is emailed about the new delivery.
        var customerPage = WebUtility.HtmlDecode(await NewClient().GetStringAsync($"/en/Orders/Details?number={number}&key={key}"));
        Assert.Contains("New Street 99", customerPage);

        await Fixture.Factory.FlushEmailsAsync();
        var sent = Fixture.Factory.Emails.To(email).Single(e => e.Subject == $"Order #{number}: delivery details changed");
        Assert.Contains("New Street 99", WebUtility.HtmlDecode(sent.Html));
        Assert.Contains("delivery details of your order", WebUtility.HtmlDecode(sent.Html));
    }

    [Fact]
    public async Task Edit_BadValues_StayOnTheForm_WithTheProblemShown_AndNothingChanges()
    {
        var (number, _, _) = await PlaceOrderAsync();
        var admin = await AdminClientAsync();

        var badPhone = await EditFieldsAsync(admin, number);
        Set(badPhone, "Phone", "12");
        var phoneResponse = await PostFormListAsync(admin, EditUrl(number), "/en/Admin/Orders/Edit", badPhone);
        Assert.Equal(HttpStatusCode.OK, phoneResponse.StatusCode);
        Assert.Contains("field-validation-error", await BodyAsync(phoneResponse));

        var badRegion = await EditFieldsAsync(admin, number);
        Set(badRegion, "Region", "mars");
        var regionResponse = await PostFormListAsync(admin, EditUrl(number), "/en/Admin/Orders/Edit", badRegion);
        Assert.Equal(HttpStatusCode.OK, regionResponse.StatusCode);
        Assert.Contains("The region, delivery day or time is not valid.", WebUtility.HtmlDecode(await BodyAsync(regionResponse)));

        var badDay = await EditFieldsAsync(admin, number);
        Set(badDay, "DeliveryDate", "2000-01-01");
        Assert.Equal(HttpStatusCode.OK, (await PostFormListAsync(admin, EditUrl(number), "/en/Admin/Orders/Edit", badDay)).StatusCode);

        var noChange = await EditFieldsAsync(admin, number);
        Assert.Equal(HttpStatusCode.Redirect, (await PostFormListAsync(admin, EditUrl(number), "/en/Admin/Orders/Edit", noChange)).StatusCode);
        Assert.Contains("Nothing was changed.", await admin.GetStringAsync(DetailsUrl(number)));

        Assert.Equal("Abovyan", await InDbAsync(db => db.Orders.Where(o => o.Number == number).Select(o => o.Street).SingleAsync()));
    }

    [Fact]
    public async Task Edit_AfterPacking_IsCommentOnly_AndAfterDelivery_IsClosed()
    {
        var (number, _, _) = await PlaceOrderAsync();
        var admin = await AdminClientAsync();
        await PostFormListAsync(admin, DetailsUrl(number), "/en/Admin/Orders/ChangeStatus", [F("number", number.ToString()), F("status", "Packed")]);

        var page = await admin.GetStringAsync(EditUrl(number));
        Assert.Contains("This order is packed, so only the comment can be changed.", WebUtility.HtmlDecode(page));
        Assert.DoesNotContain("<select", page); // no region, day or time to change

        var comment = await EditFieldsAsync(admin, number);
        Set(comment, "Comment", "Ring twice");
        Assert.Equal(HttpStatusCode.Redirect, (await PostFormListAsync(admin, EditUrl(number), "/en/Admin/Orders/Edit", comment)).StatusCode);
        Assert.Contains("Ring twice", await admin.GetStringAsync(DetailsUrl(number)));

        // A hand-made form that changes the street is refused.
        var forged = await EditFieldsAsync(admin, number);
        Set(forged, "Street", "Hacked");
        await PostFormListAsync(admin, EditUrl(number), "/en/Admin/Orders/Edit", forged);
        Assert.Contains("This order can no longer be edited.", WebUtility.HtmlDecode(await admin.GetStringAsync(DetailsUrl(number))));
        Assert.Equal("Abovyan", await InDbAsync(db => db.Orders.Where(o => o.Number == number).Select(o => o.Street).SingleAsync()));

        await PostFormListAsync(admin, DetailsUrl(number), "/en/Admin/Orders/ChangeStatus", [F("number", number.ToString()), F("status", "Delivered")]);
        var closed = await admin.GetAsync(EditUrl(number));
        Assert.Equal(HttpStatusCode.Redirect, closed.StatusCode); // back to the order with a message
        Assert.Contains("This order can no longer be edited.", WebUtility.HtmlDecode(await admin.GetStringAsync(DetailsUrl(number))));
        Assert.DoesNotContain($"/en/Admin/Orders/Edit?number={number}", await admin.GetStringAsync(DetailsUrl(number))); // no Edit button
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(EditUrl(99999999))).StatusCode);
    }

    // ---- Packing slips ----

    [Fact]
    public async Task TheSlip_IsAStandalonePrintPage_WithItemsPricesAddressAndTheCashToCollect_ButNoNotes()
    {
        var (number, _, _) = await PlaceOrderAsync();
        var admin = await AdminClientAsync();
        await PostFormListAsync(admin, DetailsUrl(number), "/en/Admin/Orders/AddNote", [F("number", number.ToString()), F("text", "INTERNAL-ONLY-NOTE")]);
        var order = await InDbAsync(db => db.Orders.Include(o => o.Items).SingleAsync(o => o.Number == number));
        var item = order.Items.Single();

        var response = await admin.GetAsync($"/en/Admin/Orders/Slip?number={number}");
        var html = WebUtility.HtmlDecode(await BodyAsync(response));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("admin-top", html);                                 // no admin header: a clean sheet
        Assert.Contains("/css/slip.css", html);
        Assert.Contains("/js/slip.js", html);
        Assert.Contains("data-print", html);
        Assert.Contains("name=\"robots\" content=\"noindex, nofollow\"", html);
        Assert.Contains($"#{number}", html);
        Assert.Contains(item.Sku, html);
        Assert.Contains(item.Name, html);
        Assert.Contains(Shop.Web.Money.Format(item.UnitPrice), html);              // full prices
        Assert.Contains(Shop.Web.Money.Format(order.Total), html);
        Assert.Contains("Abovyan", html);
        Assert.Contains("+37491123456", html);
        Assert.Contains("Cash to collect", html);
        Assert.DoesNotContain("INTERNAL-ONLY-NOTE", html);

        // The page script and style exist.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/css/slip.css")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/js/slip.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/en/Admin/Orders/Slip?number=99999999")).StatusCode);

        // A cancelled order says so, instead of "bank transfer".
        await PostFormListAsync(admin, DetailsUrl(number), "/en/Admin/Orders/ChangeStatus", [F("number", number.ToString()), F("status", "Cancelled")]);
        var cancelled = WebUtility.HtmlDecode(await admin.GetStringAsync($"/en/Admin/Orders/Slip?number={number}"));
        Assert.Contains("Cancelled: nothing to collect.", cancelled);
        Assert.DoesNotContain("Cash to collect", cancelled);
    }

    [Fact]
    public async Task Slips_PrintSeveralOrders_InDeliveryOrder_WithTheSameFilterAsTheList()
    {
        var a = (await PlaceOrderAsync()).Number;
        var b = (await PlaceOrderAsync("BankTransfer")).Number;
        var phone = "+37477" + new Random().Next(100000, 999999);
        await InDbAsync(async db =>
        {
            await db.Orders.Where(o => o.Number == a || o.Number == b).ExecuteUpdateAsync(s => s.SetProperty(o => o.Phone, phone));
            return 0;
        });
        var admin = await AdminClientAsync();

        var html = WebUtility.HtmlDecode(await admin.GetStringAsync($"/en/Admin/Orders/Slips?Query={Uri.EscapeDataString(phone)}"));

        Assert.Equal(2, Regex.Matches(html, "<article class=\"slip\">").Count);
        Assert.Contains("2 slips", html);
        Assert.Contains("Nothing to collect (bank transfer).", html);              // the transfer order
        Assert.Contains("Cash to collect", html);                                  // the cash order

        var none = WebUtility.HtmlDecode(await admin.GetStringAsync("/en/Admin/Orders/Slips?Query=no-such-person-anywhere"));
        Assert.Contains("No orders match this filter.", none);
        Assert.DoesNotContain("<article", none);
    }

    // ---- The list ----

    [Fact]
    public async Task TheList_FindsAPhone_InAnyWritingOfIt_AndHasTheNewButtons_KeepingTheFilterForSlips()
    {
        var (number, _, _) = await PlaceOrderAsync();
        var digits = "77" + new Random().Next(100000, 999999);
        await InDbAsync(async db =>
        {
            await db.Orders.Where(o => o.Number == number).ExecuteUpdateAsync(s => s.SetProperty(o => o.Phone, "+374" + digits));
            return 0;
        });
        var admin = await AdminClientAsync();

        foreach (var typed in new[] { "0" + digits, "0" + digits[..2] + " " + digits[2..], "+374 " + digits[..2] + " " + digits[2..], digits, "00374" + digits })
        {
            var html = await admin.GetStringAsync($"/en/Admin/Orders?Query={Uri.EscapeDataString(typed)}");
            Assert.Contains($"number={number}\"", html);
        }

        var list = await admin.GetStringAsync($"/en/Admin/Orders?Query={Uri.EscapeDataString(digits)}&Status=Ordered");
        Assert.Contains("Print slips", list);
        Assert.Contains("New phone order", list);
        Assert.Contains($"href=\"/en/Admin/Orders/Slips?Query={digits}&amp;Status=Ordered\"", list);
        Assert.Contains("href=\"/en/Admin/PhoneOrders\"", list);
    }

    [Fact]
    public async Task TheOrdersCsv_HasASourceColumn_AtTheEnd()
    {
        var admin = await AdminClientAsync();
        var page = await admin.GetStringAsync("/en/Admin/Analytics");
        var from = DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-dd");

        var response = await PostFormListAsync(admin, "/en/Admin/Analytics", "/en/Admin/Analytics/ExportOrders", [F("preset", "Custom"), F("from", from), F("to", to)]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("CustomerDiscount,Source", csv.Split('\n')[0]);
        Assert.Contains("Website", csv);
        Assert.NotNull(page);
    }

    // ---- Languages ----

    [Theory]
    [InlineData("hy")]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task TheNewOrderPages_OpenInEveryLanguage_WithNoUntranslatedKeys(string culture)
    {
        var (number, _, _) = await PlaceOrderAsync();
        var admin = await AdminClientAsync();
        await PostFormListAsync(admin, DetailsUrl(number, culture), $"/{culture}/Admin/Orders/AddNote", [F("number", number.ToString()), F("text", "Note")]);

        foreach (var url in new[] { DetailsUrl(number, culture), EditUrl(number, culture), $"/{culture}/Admin/Orders/Slip?number={number}", $"/{culture}/Admin/Orders/Slips?Query={number}", $"/{culture}/Admin/Orders?Query={number}" })
        {
            var response = await admin.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await BodyAsync(response);
            var text = Regex.Replace(Regex.Replace(html, "<script[\\s\\S]*?</script>", " "), "<[^>]+>", " ");
            Assert.Empty(Regex.Matches(text, @"\b(Admin|Val|Status|Pay|Label|PayStatus|Slip|Slips|PO|Order|Cart|Checkout|Region)_[A-Za-z_]+\b").Select(m => m.Value).Distinct());
        }
    }
}

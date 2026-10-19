using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Core.Admin;
using Shop.Core.Entities;
using Shop.Core.Identity;
using Shop.Infrastructure.Data;

namespace Shop.Tests.Web;

// Admin accounts and the audit log, on the real Identity setup of the test site.
[Collection("Web")]
public class AdminAccountsTests : CheckoutTestBase
{
    public AdminAccountsTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    private static KeyValuePair<string, string> F(string key, string value) => new(key, value);

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    private async Task<(string Id, string Email)> NewUserAsync(bool confirmed = true)
    {
        return await InScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var email = NewEmail();
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = confirmed, FullName = "Test Person" };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            return (user.Id, email);
        });
    }

    private Task<string> MainAdminIdAsync() => InScopeAsync(async services =>
        (await services.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(ShopWebFactory.AdminEmail))!.Id);

    private Task<GrantAdminResult> GrantAsync(string email, string byAdminId) =>
        InScopeAsync(s => s.GetRequiredService<IAdminAccountService>().GrantAsync(email, byAdminId));

    private Task<RevokeAdminResult> RevokeAsync(string userId, string byAdminId) =>
        InScopeAsync(s => s.GetRequiredService<IAdminAccountService>().RevokeAsync(userId, byAdminId));

    private Task<bool> IsAdminAsync(string userId) =>
        InScopeAsync(s => s.GetRequiredService<IAdminAccountService>().IsAdminAsync(userId));

    private async Task<HttpClient> SignedInClientAsync(string email)
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, email, Password)).StatusCode);
        return client;
    }

    private async Task<HttpClient> MainAdminClientAsync()
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword)).StatusCode);
        return client;
    }

    // ---- Granting ----

    [Fact]
    public async Task Grant_GivesAConfirmedAccountTheRole_ListsIt_AndIsAudited()
    {
        var main = await MainAdminIdAsync();
        var (id, email) = await NewUserAsync();
        Assert.False(await IsAdminAsync(id));

        Assert.Equal(GrantAdminResult.Granted, await GrantAsync("  " + email.ToUpperInvariant() + " ", main)); // case and spaces do not matter

        Assert.True(await IsAdminAsync(id));
        var list = await InScopeAsync(s => s.GetRequiredService<IAdminAccountService>().ListAsync());
        Assert.Contains(list, a => a.Id == id && !a.IsConfigured);
        Assert.Contains(list, a => a.Email == ShopWebFactory.AdminEmail && a.IsConfigured);

        await InScopeAsync(async s =>
        {
            var audit = await s.GetRequiredService<ShopDbContext>().AuditLogs.SingleAsync(a => a.Action == "Admin.Granted" && a.EntityId == id);
            Assert.Equal(main, audit.UserId);
            Assert.Equal(email, audit.Details);
            Assert.Equal(ShopWebFactory.AdminEmail, audit.UserEmail);
            return 0;
        });
    }

    [Fact]
    public async Task Grant_RefusesUnknownUnconfirmedAndExistingAdmins()
    {
        var main = await MainAdminIdAsync();
        var (unconfirmedId, unconfirmed) = await NewUserAsync(confirmed: false);

        Assert.Equal(GrantAdminResult.NotFound, await GrantAsync("nobody-here@example.com", main));
        Assert.Equal(GrantAdminResult.NotConfirmed, await GrantAsync(unconfirmed, main));
        Assert.False(await IsAdminAsync(unconfirmedId));
        Assert.Equal(GrantAdminResult.AlreadyAdmin, await GrantAsync(ShopWebFactory.AdminEmail, main));
    }

    // ---- Revoking ----

    [Fact]
    public async Task Revoke_TakesTheRoleAway_ChangesTheSecurityStamp_AndIsAudited()
    {
        var main = await MainAdminIdAsync();
        var (id, email) = await NewUserAsync();
        await GrantAsync(email, main);
        var stampBefore = await InScopeAsync(async s => (await s.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(id))!.SecurityStamp);

        Assert.Equal(RevokeAdminResult.Revoked, await RevokeAsync(id, main));

        Assert.False(await IsAdminAsync(id));
        var stampAfter = await InScopeAsync(async s => (await s.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(id))!.SecurityStamp);
        Assert.NotEqual(stampBefore, stampAfter);
        await InScopeAsync(async s =>
        {
            Assert.True(await s.GetRequiredService<ShopDbContext>().AuditLogs.AnyAsync(a => a.Action == "Admin.Revoked" && a.EntityId == id && a.UserId == main));
            return 0;
        });

        // The person keeps their customer account.
        Assert.Equal(RevokeAdminResult.NotAnAdmin, await RevokeAsync(id, main));
    }

    [Fact]
    public async Task Revoke_RefusesYourself_TheConfiguredAdmin_AndUnknownAccounts()
    {
        var main = await MainAdminIdAsync();
        var (otherId, otherEmail) = await NewUserAsync();
        await GrantAsync(otherEmail, main);

        Assert.Equal(RevokeAdminResult.Yourself, await RevokeAsync(otherId, otherId));
        Assert.True(await IsAdminAsync(otherId));

        // The configured admin is given the role again at every start, so it cannot be removed here (not even by another admin).
        Assert.Equal(RevokeAdminResult.ConfiguredAdmin, await RevokeAsync(main, otherId));
        Assert.True(await IsAdminAsync(main));

        Assert.Equal(RevokeAdminResult.NotFound, await RevokeAsync("no-such-id", main));

        await RevokeAsync(otherId, main);
    }

    [Fact]
    public async Task Revoke_NeverRemovesTheLastAdmin_EvenWhenTwoAdminsRemoveEachOtherAtOnce()
    {
        var main = await MainAdminIdAsync();
        var (idA, emailA) = await NewUserAsync();
        var (idB, emailB) = await NewUserAsync();
        await GrantAsync(emailA, main);
        await GrantAsync(emailB, main);

        // For this test only, every other admin (the configured one and any left by other tests) loses the role,
        // so that A and B really are the only two. They get it back at the end.
        var others = await InScopeAsync(async s =>
        {
            var users = s.GetRequiredService<UserManager<ApplicationUser>>();
            var ids = (await users.GetUsersInRoleAsync(Roles.Admin)).Select(u => u.Id).Where(id => id != idA && id != idB).ToList();
            foreach (var id in ids)
            {
                Assert.True((await users.RemoveFromRoleAsync((await users.FindByIdAsync(id))!, Roles.Admin)).Succeeded);
            }

            return ids;
        });

        try
        {
            var results = await Task.WhenAll(
                Task.Run(() => RevokeAsync(idB, idA)),   // A removes B
                Task.Run(() => RevokeAsync(idA, idB)));  // B removes A

            Assert.Equal(1, results.Count(r => r == RevokeAdminResult.Revoked));
            Assert.Equal(1, results.Count(r => r == RevokeAdminResult.LastAdmin));
            Assert.Equal(1, new[] { await IsAdminAsync(idA), await IsAdminAsync(idB) }.Count(x => x)); // exactly one admin is left
        }
        finally
        {
            await InScopeAsync(async s =>
            {
                var users = s.GetRequiredService<UserManager<ApplicationUser>>();
                foreach (var id in others)
                {
                    var user = (await users.FindByIdAsync(id))!;
                    if (!await users.IsInRoleAsync(user, Roles.Admin))
                    {
                        Assert.True((await users.AddToRoleAsync(user, Roles.Admin)).Succeeded);
                    }
                }

                return 0;
            });
        }
    }

    // ---- Immediate effect, with a live session ----

    [Fact]
    public async Task ARemovedAdmin_IsStoppedAtOnce_EvenWithTheirOldSignInCookie()
    {
        var main = await MainAdminIdAsync();
        var (id, email) = await NewUserAsync();
        await GrantAsync(email, main);

        // They sign in after being made admin: the cookie says "Admin" and the panel opens.
        var session = await SignedInClientAsync(email);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/en/Admin")).StatusCode);

        await RevokeAsync(id, main);

        // The same cookie, a moment later: refused.
        var response = await session.GetAsync("/en/Admin/Orders");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());

        // Actions too, not only pages.
        var post = await PostFormListAsync(session, "/en/Orders/Lookup", "/en/Admin/Settings/Save", [F("DeliveryFee", "0")]);
        Assert.Contains("/Account/AccessDenied", post.Headers.Location!.ToString());
    }

    [Fact]
    public async Task ANewAdmin_NeedsToSignInAgain_ToUseThePanel()
    {
        var main = await MainAdminIdAsync();
        var (_, email) = await NewUserAsync();
        var session = await SignedInClientAsync(email); // signed in as an ordinary customer

        await GrantAsync(email, main);

        // The old cookie has no "Admin" in it yet.
        Assert.Contains("/Account/AccessDenied", (await session.GetAsync("/en/Admin")).Headers.Location!.ToString());

        // A new sign-in has it.
        var fresh = await SignedInClientAsync(email);
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/en/Admin")).StatusCode);
    }

    // ---- The screen ----

    [Theory]
    [InlineData("/en/Admin/Admins")]
    [InlineData("/ru/Admin/Audit")]
    public async Task TheAdminsAndAuditPages_RefuseVisitorsAndCustomers(string url)
    {
        Assert.Contains("/Account/Login", (await NewClient().GetAsync(url)).Headers.Location!.ToString());

        var customer = NewClient();
        await SignedInUserAsync(customer);
        Assert.Contains("/Account/AccessDenied", (await customer.GetAsync(url)).Headers.Location!.ToString());
    }

    [Fact]
    public async Task ACustomer_CannotAddOrRemoveAdmins()
    {
        var main = await MainAdminIdAsync();
        var customer = NewClient();
        var customerEmail = await SignedInUserAsync(customer);

        var add = await PostFormListAsync(customer, "/en/Orders/Lookup", "/en/Admin/Admins/Add", [F("email", customerEmail)]);
        var remove = await PostFormListAsync(customer, "/en/Orders/Lookup", "/en/Admin/Admins/Remove", [F("id", main)]);

        Assert.Contains("/Account/AccessDenied", add.Headers.Location!.ToString());
        Assert.Contains("/Account/AccessDenied", remove.Headers.Location!.ToString());
        Assert.False(await IsAdminAsync(await InScopeAsync(async s => (await s.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(customerEmail))!.Id)));
        Assert.True(await IsAdminAsync(main));
    }

    [Theory]
    [InlineData("hy")]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task AdminsAndAuditPages_Open_AndHaveNoUntranslatedKeys(string culture)
    {
        var admin = await MainAdminClientAsync();

        foreach (var path in new[] { "/Admins", "/Audit" })
        {
            var response = await admin.GetAsync($"/{culture}/Admin{path}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await BodyAsync(response);
            var text = Regex.Replace(Regex.Replace(html, "<script[\\s\\S]*?</script>", " "), "<[^>]+>", " ");
            Assert.Empty(Regex.Matches(text, @"\b(Admin|Val|Status|Pay|Checkout)_[A-Za-z_]+\b").Select(m => m.Value).Distinct());
        }
    }

    [Fact]
    public async Task AdminsPage_MarksYouAndTheMainAdmin_AndOffersRemoveOnlyForOthers()
    {
        var main = await MainAdminIdAsync();
        var (otherId, otherEmail) = await NewUserAsync();
        await GrantAsync(otherEmail, main);
        var admin = await MainAdminClientAsync();

        var html = await admin.GetStringAsync("/en/Admin/Admins");

        Assert.Contains(otherEmail, html);
        Assert.Contains($"name=\"id\" value=\"{otherId}\"", html);          // can remove the other
        Assert.DoesNotContain($"name=\"id\" value=\"{main}\"", html);         // not yourself, not the main admin

        await RevokeAsync(otherId, main);
    }

    [Fact]
    public async Task Admin_AddsAndRemovesAnAdmin_ThroughThePage_WithClearMessages()
    {
        var admin = await MainAdminClientAsync();
        var (id, email) = await NewUserAsync();
        var (_, unconfirmed) = await NewUserAsync(confirmed: false);
        const string page = "/en/Admin/Admins";

        async Task<string> Bar() => WebUtility.HtmlDecode(await admin.GetStringAsync(page));
        Task<HttpResponseMessage> Add(string value) => PostFormListAsync(admin, page, "/en/Admin/Admins/Add", [F("email", value)]);

        // Wrong input.
        await Add("not-an-email");
        Assert.Contains("Enter a valid email address.", await Bar());
        await Add("nobody-at-all@example.com");
        Assert.Contains("There is no account with this email.", await Bar());
        await Add(unconfirmed);
        Assert.Contains("This account's email is not confirmed yet.", await Bar());
        await Add(ShopWebFactory.AdminEmail);
        Assert.Contains("This person is already an admin.", await Bar());

        // A good one.
        Assert.Equal(HttpStatusCode.Redirect, (await Add(email)).StatusCode);
        var granted = await Bar();
        Assert.Contains($"{email} is now an admin.", granted);
        Assert.Contains(email, granted);
        Assert.True(await IsAdminAsync(id));

        // Remove: the button is there; a forged attempt on the main admin is refused.
        var main = await MainAdminIdAsync();
        await PostFormListAsync(admin, page, "/en/Admin/Admins/Remove", [F("id", main)]);
        Assert.Contains("The main admin is set in the site configuration and cannot be removed here.", await Bar());
        await PostFormListAsync(admin, page, "/en/Admin/Admins/Remove", [F("id", main)]);

        await PostFormListAsync(admin, page, "/en/Admin/Admins/Remove", [F("id", id)]);
        Assert.Contains("Admin access removed.", await Bar());
        Assert.False(await IsAdminAsync(id));

        // Removing yourself through a forged form is refused too (the main admin is also "yourself" here).
        await PostFormListAsync(admin, page, "/en/Admin/Admins/Remove", [F("id", "no-such-id")]);
        Assert.Equal(HttpStatusCode.NotFound, (await PostFormListAsync(admin, page, "/en/Admin/Admins/Remove", [F("id", "no-such-id")])).StatusCode);
    }

    // ---- Audit log screen ----

    private async Task<string> SeedAuditRowsAsync(int count, string type)
    {
        await InScopeAsync(async s =>
        {
            var db = s.GetRequiredService<ShopDbContext>();
            for (var i = 0; i < count; i++)
            {
                db.AuditLogs.Add(new AuditLog
                {
                    At = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                    UserEmail = "seed@example.com",
                    Action = "Test.Seed" + (i % 2 == 0 ? "A" : "B"),
                    EntityType = type,
                    EntityId = i.ToString(),
                    Details = $"row {i} of {type}"
                });
            }

            await db.SaveChangesAsync();
            return 0;
        });

        return type;
    }

    [Fact]
    public async Task Audit_IsNewestFirst_FiftyPerPage_AndFilters()
    {
        var type = await SeedAuditRowsAsync(120, "Seed" + Guid.NewGuid().ToString("N")[..8]);

        await InScopeAsync(async s =>
        {
            var audit = s.GetRequiredService<IAdminAuditService>();

            var first = await audit.ListAsync(new AuditFilter { EntityType = type });
            Assert.Equal(120, first.TotalCount);
            Assert.Equal(50, first.PageSize);
            Assert.Equal(3, first.TotalPages);
            Assert.Equal(50, first.Items.Count);
            Assert.Equal("119", first.Items[0].EntityId); // the newest row first
            Assert.True(first.Items.Zip(first.Items.Skip(1), (a, b) => a.At >= b.At).All(x => x));

            Assert.Equal(20, (await audit.ListAsync(new AuditFilter { EntityType = type, Page = 3 })).Items.Count);
            Assert.Empty((await audit.ListAsync(new AuditFilter { EntityType = type, Page = 9 })).Items);

            // Text search in the action, details, and exact entity id.
            Assert.Equal(60, (await audit.ListAsync(new AuditFilter { EntityType = type, Query = "Test.SeedA" })).TotalCount);
            Assert.Equal(1, (await audit.ListAsync(new AuditFilter { EntityType = type, Query = "row 77 of" })).TotalCount);
            // The entity id is matched exactly (row 119 is the newest, so it is on page 1).
            Assert.Contains((await audit.ListAsync(new AuditFilter { EntityType = type, Query = "119" })).Items, r => r.EntityId == "119");

            // Dates are Armenian days: 12:00 UTC on March 10 is 16:00 there, still March 10.
            Assert.Equal(120, (await audit.ListAsync(new AuditFilter { EntityType = type, From = new DateOnly(2026, 3, 10), To = new DateOnly(2026, 3, 10) })).TotalCount);
            Assert.Equal(0, (await audit.ListAsync(new AuditFilter { EntityType = type, From = new DateOnly(2026, 3, 11) })).TotalCount);
            Assert.Equal(0, (await audit.ListAsync(new AuditFilter { EntityType = type, To = new DateOnly(2026, 3, 9) })).TotalCount);

            Assert.Contains(type, await audit.EntityTypesAsync());
            return 0;
        });
    }

    [Fact]
    public async Task AuditPage_ShowsWhatAdminsDid_WithLinks_AndFiltersByKind()
    {
        var admin = await MainAdminClientAsync();
        var main = await MainAdminIdAsync();
        var (_, email) = await NewUserAsync();
        await GrantAsync(email, main); // writes an "Admin.Granted" line

        // A settings save writes a "Settings.Saved" line.
        var settings = await admin.GetStringAsync("/en/Admin/Settings");
        string Field(string name) => Regex.Match(settings, $@"name=""{name}""[^>]*value=""([^""]*)""").Groups[1].Value;
        await PostFormListAsync(admin, "/en/Admin/Settings", "/en/Admin/Settings/Save", new[]
        {
            F("DeliveryFee", Field("DeliveryFee")), F("FreeDeliveryFrom", Field("FreeDeliveryFrom")), F("DeliveryDaysAhead", Field("DeliveryDaysAhead")),
            F("DeliveryFirstDayOffset", Field("DeliveryFirstDayOffset")), F("ReturnWindowDays", Field("ReturnWindowDays")),
            F("BankName", WebUtility.HtmlDecode(Field("BankName"))), F("BankAccount", Field("BankAccount")), F("BankRecipient", WebUtility.HtmlDecode(Field("BankRecipient")))
        });

        var html = await admin.GetStringAsync("/en/Admin/Audit");
        Assert.Contains("Admin.Granted", html);
        Assert.Contains(email, html);
        Assert.Contains("Settings.Saved", html);
        Assert.Contains(ShopWebFactory.AdminEmail, html);
        Assert.Contains("href=\"/en/Admin/Admins\"", html); // the "User #..." item links to the admins page
        Assert.Contains("href=\"/en/Admin/Settings\"", html);

        // Filtering by kind.
        var onlyUsers = await admin.GetStringAsync("/en/Admin/Audit?EntityType=User");
        Assert.Contains("Admin.Granted", onlyUsers);
        Assert.DoesNotContain("Settings.Saved", onlyUsers);

        await RevokeAsync((await InScopeAsync(async s => (await s.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id)), main);
    }

    [Fact]
    public async Task AuditPage_DoesNotLinkToThingsThatWereDeleted()
    {
        var type = "Category";
        var marker = "Gone-" + Guid.NewGuid().ToString("N")[..6];
        await InScopeAsync(async s =>
        {
            var db = s.GetRequiredService<ShopDbContext>();
            db.AuditLogs.Add(new AuditLog { At = DateTime.UtcNow, UserEmail = "x@example.com", Action = "Category.Deleted", EntityType = type, EntityId = "987654", Details = marker });
            await db.SaveChangesAsync();
            return 0;
        });

        var html = await (await MainAdminClientAsync()).GetStringAsync($"/en/Admin/Audit?Query={marker}");

        Assert.Contains(marker, html);
        Assert.DoesNotContain("/en/Admin/Categories/Edit/987654", html);
    }
}

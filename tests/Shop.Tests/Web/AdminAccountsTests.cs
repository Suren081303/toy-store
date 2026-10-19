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

// Admin accounts (create, edit, delete) and the audit log, on the real Identity setup of the test site.
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

    // An ordinary customer account (never an admin).
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

    private static AdminAccountInput Input(string email, string password = Password, string name = "New Admin", string phone = "091 123456") =>
        new() { FullName = name, Email = email, Phone = phone, Password = password };

    private Task<AdminSaveResult> CreateAsync(AdminAccountInput input, string byAdminId) =>
        InScopeAsync(s => s.GetRequiredService<IAdminAccountService>().CreateAsync(input, byAdminId));

    private Task<AdminSaveResult> UpdateAsync(string userId, AdminAccountInput input, string byAdminId) =>
        InScopeAsync(s => s.GetRequiredService<IAdminAccountService>().UpdateAsync(userId, input, byAdminId));

    private Task<DeleteAdminResult> DeleteAsync(string userId, string byAdminId) =>
        InScopeAsync(s => s.GetRequiredService<IAdminAccountService>().DeleteAsync(userId, byAdminId));

    private Task<bool> IsAdminAsync(string userId) =>
        InScopeAsync(s => s.GetRequiredService<IAdminAccountService>().IsAdminAsync(userId));

    private Task<ApplicationUser?> FindUserAsync(string id) =>
        InScopeAsync(s => s.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(id));

    private Task<ApplicationUser?> FindUserByEmailAsync(string email) =>
        InScopeAsync(s => s.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email));

    // A new admin account made the way the panel makes it.
    private async Task<(string Id, string Email)> NewAdminAsync(string byAdminId)
    {
        var email = NewEmail();
        var result = await CreateAsync(Input(email), byAdminId);
        Assert.Equal(AdminSaveStatus.Saved, result.Status);
        return (result.UserId!, email);
    }

    private async Task<HttpClient> SignedInClientAsync(string email, string password = Password)
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, email, password)).StatusCode);
        return client;
    }

    private async Task<HttpClient> MainAdminClientAsync()
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword)).StatusCode);
        return client;
    }

    // ---- Creating ----

    [Fact]
    public async Task Create_MakesAConfirmedAccountWithOnlyTheAdminRole_ThatCanSignInAtOnce_AndIsAudited()
    {
        var main = await MainAdminIdAsync();
        var email = NewEmail();

        var result = await CreateAsync(Input("  " + email + " ", name: " Anna Admin ", phone: " 093 555111 "), main); // spaces do not matter

        Assert.Equal(AdminSaveStatus.Saved, result.Status);
        var user = (await FindUserAsync(result.UserId!))!;
        Assert.Equal(email, user.Email);
        Assert.True(user.EmailConfirmed); // nobody has to open an email
        Assert.Equal("Anna Admin", user.FullName);
        Assert.Equal("093 555111", user.PhoneNumber);

        await InScopeAsync(async s =>
        {
            var users = s.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True(await users.IsInRoleAsync(user, Roles.Admin));
            Assert.False(await users.IsInRoleAsync(user, Roles.Customer)); // an admin account is not a customer account

            var audit = await s.GetRequiredService<ShopDbContext>().AuditLogs.SingleAsync(a => a.Action == "Admin.Created" && a.EntityId == user.Id);
            Assert.Equal(main, audit.UserId);
            Assert.Equal(email, audit.Details);
            Assert.Equal(ShopWebFactory.AdminEmail, audit.UserEmail);

            var list = await s.GetRequiredService<IAdminAccountService>().ListAsync();
            Assert.Contains(list, a => a.Id == user.Id && !a.IsConfigured && a.Phone == "093 555111");
            Assert.Contains(list, a => a.Email == ShopWebFactory.AdminEmail && a.IsConfigured);
            return 0;
        });

        // Signs in straight away and the panel opens, no second sign-in needed.
        var session = await SignedInClientAsync(email);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/en/Admin")).StatusCode);

        await DeleteAsync(user.Id, main);
    }

    [Fact]
    public async Task Create_RefusesATakenEmail_AndAWeakPassword_AndLeavesNothingBehind()
    {
        var main = await MainAdminIdAsync();
        var (customerId, customerEmail) = await NewUserAsync();

        // A customer's email, the main admin's email in capitals: both are taken.
        Assert.Equal(AdminSaveStatus.EmailTaken, (await CreateAsync(Input(customerEmail), main)).Status);
        Assert.Equal(AdminSaveStatus.EmailTaken, (await CreateAsync(Input(ShopWebFactory.AdminEmail.ToUpperInvariant()), main)).Status);
        Assert.False(await IsAdminAsync(customerId)); // the customer was not turned into an admin

        var shortPassword = NewEmail();
        var noDigit = NewEmail();
        var weak1 = await CreateAsync(Input(shortPassword, password: "short1"), main);
        var weak2 = await CreateAsync(Input(noDigit, password: "onlyletters"), main);

        Assert.Equal(AdminSaveStatus.WeakPassword, weak1.Status);
        Assert.NotEmpty(weak1.Errors!);
        Assert.Equal(AdminSaveStatus.WeakPassword, weak2.Status);
        Assert.Null(await FindUserByEmailAsync(shortPassword));
        Assert.Null(await FindUserByEmailAsync(noDigit));
    }

    // ---- Editing ----

    [Fact]
    public async Task Update_ChangesNameEmailAndPassword_SignsTheirOtherSessionsOut_AndNeverWritesThePasswordToTheAudit()
    {
        var main = await MainAdminIdAsync();
        var (id, oldEmail) = await NewAdminAsync(main);
        var stampBefore = (await FindUserAsync(id))!.SecurityStamp;
        var newEmail = NewEmail();

        var result = await UpdateAsync(id, Input(newEmail, password: "New-Pass-456", name: "Renamed Admin", phone: "093 111222"), main);

        Assert.Equal(AdminSaveStatus.Saved, result.Status);
        var user = (await FindUserAsync(id))!;
        Assert.Equal(newEmail, user.Email);
        Assert.Equal(newEmail, user.UserName); // the sign-in name follows the email
        Assert.Equal("Renamed Admin", user.FullName);
        Assert.Equal("093 111222", user.PhoneNumber);
        Assert.NotEqual(stampBefore, user.SecurityStamp);

        // The new email and password work; the old email and password do not.
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(NewClient(), newEmail, "New-Pass-456")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(NewClient(), oldEmail, Password)).StatusCode);

        await InScopeAsync(async s =>
        {
            var audit = await s.GetRequiredService<ShopDbContext>().AuditLogs.SingleAsync(a => a.Action == "Admin.Updated" && a.EntityId == id);
            Assert.Equal(main, audit.UserId);
            foreach (var word in new[] { "name", "phone", "email", "password" })
            {
                Assert.Contains(word, audit.Details);
            }

            Assert.DoesNotContain("New-Pass-456", audit.Details);
            Assert.False(await s.GetRequiredService<ShopDbContext>().AuditLogs.AnyAsync(a => a.Details!.Contains("New-Pass-456") || a.Details!.Contains(Password)));
            return 0;
        });

        await DeleteAsync(id, main);
    }

    [Fact]
    public async Task Update_WithoutAPassword_KeepsThePassword_AndTheSessions()
    {
        var main = await MainAdminIdAsync();
        var (id, email) = await NewAdminAsync(main);
        var stampBefore = (await FindUserAsync(id))!.SecurityStamp;

        var result = await UpdateAsync(id, Input(email, password: "", name: "Only The Name Changed"), main);

        Assert.Equal(AdminSaveStatus.Saved, result.Status);
        Assert.Equal("Only The Name Changed", (await FindUserAsync(id))!.FullName);
        Assert.Equal(stampBefore, (await FindUserAsync(id))!.SecurityStamp);
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(NewClient(), email, Password)).StatusCode);

        // Saving the same values again changes nothing and writes no second audit line.
        var lines = await InScopeAsync(s => s.GetRequiredService<ShopDbContext>().AuditLogs.CountAsync(a => a.Action == "Admin.Updated" && a.EntityId == id));
        Assert.Equal(AdminSaveStatus.Saved, (await UpdateAsync(id, Input(email, password: "", name: "Only The Name Changed"), main)).Status);
        Assert.Equal(lines, await InScopeAsync(s => s.GetRequiredService<ShopDbContext>().AuditLogs.CountAsync(a => a.Action == "Admin.Updated" && a.EntityId == id)));

        await DeleteAsync(id, main);
    }

    [Fact]
    public async Task Update_AnAdminEditingThemself_IsNotSignedOut()
    {
        var main = await MainAdminIdAsync();
        var (id, email) = await NewAdminAsync(main);
        var stampBefore = (await FindUserAsync(id))!.SecurityStamp;

        var newEmail = NewEmail();
        Assert.Equal(AdminSaveStatus.Saved, (await UpdateAsync(id, Input(newEmail, password: "", name: "Me Again"), id)).Status);

        Assert.Equal(stampBefore, (await FindUserAsync(id))!.SecurityStamp);
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(NewClient(), newEmail, Password)).StatusCode);

        await DeleteAsync(id, main);
    }

    [Fact]
    public async Task Update_Refuses_ATakenEmail_AWeakPassword_TheMainAdminsEmail_AndAccountsThatAreNotAdmins()
    {
        var main = await MainAdminIdAsync();
        var (id, email) = await NewAdminAsync(main);
        var (customerId, customerEmail) = await NewUserAsync();

        Assert.Equal(AdminSaveStatus.EmailTaken, (await UpdateAsync(id, Input(customerEmail), main)).Status);

        var weak = await UpdateAsync(id, Input(email, password: "short1"), main);
        Assert.Equal(AdminSaveStatus.WeakPassword, weak.Status);
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(NewClient(), email, Password)).StatusCode); // the old password still works

        // The main admin: the email is the one in the site configuration (the site would create the account again).
        var newMainEmail = NewEmail();
        Assert.Equal(AdminSaveStatus.ConfiguredEmail, (await UpdateAsync(main, Input(newMainEmail), id)).Status);
        Assert.Equal(ShopWebFactory.AdminEmail, (await FindUserAsync(main))!.Email);
        Assert.Null(await FindUserByEmailAsync(newMainEmail));

        Assert.Equal(AdminSaveStatus.NotFound, (await UpdateAsync("no-such-id", Input(NewEmail()), main)).Status);
        Assert.Equal(AdminSaveStatus.NotAnAdmin, (await UpdateAsync(customerId, Input(customerEmail), main)).Status);

        await DeleteAsync(id, main);
    }

    // ---- Deleting ----

    [Fact]
    public async Task Delete_RemovesTheAccount_KeepsTheirOrdersAndTheHistory_AndIsAudited()
    {
        var main = await MainAdminIdAsync();
        var (id, email) = await NewAdminAsync(main);
        await UpdateAsync(id, Input(email, password: "", name: "Will Be Deleted"), id); // a line in the audit log made by this admin

        // An order that points to this account (made before it became an admin account, or by hand).
        var guest = NewClient();
        await AddToCartAsync(guest, await PlainProductAsync());
        var (number, _) = ParseDone(await CheckoutAsync(guest, NewEmail()));
        await InScopeAsync(async s =>
        {
            await s.GetRequiredService<ShopDbContext>().Orders.Where(o => o.Number == number).ExecuteUpdateAsync(u => u.SetProperty(o => o.UserId, id));
            return 0;
        });

        Assert.Equal(DeleteAdminResult.Deleted, await DeleteAsync(id, main));

        Assert.Null(await FindUserAsync(id));
        Assert.False(await IsAdminAsync(id));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(NewClient(), email, Password)).StatusCode); // cannot sign in any more

        await InScopeAsync(async s =>
        {
            var db = s.GetRequiredService<ShopDbContext>();
            var order = await db.Orders.SingleAsync(o => o.Number == number);
            Assert.Null(order.UserId);                  // the order stays, without the link to the deleted account
            Assert.False(string.IsNullOrWhiteSpace(order.Email));

            var deleted = await db.AuditLogs.SingleAsync(a => a.Action == "Admin.Deleted" && a.EntityId == id);
            Assert.Equal(main, deleted.UserId);
            Assert.Equal(email, deleted.Details);       // the history still says who it was

            var earlier = await db.AuditLogs.SingleAsync(a => a.Action == "Admin.Updated" && a.EntityId == id);
            Assert.Equal(email, earlier.UserEmail);     // their own earlier actions keep their email
            return 0;
        });
    }

    [Fact]
    public async Task Delete_Refuses_TheMainAdmin_UnknownAccounts_AndAccountsThatAreNotAdmins()
    {
        var main = await MainAdminIdAsync();
        var (otherId, _) = await NewAdminAsync(main);
        var (customerId, _) = await NewUserAsync();

        // The main admin is made again at every start, so it cannot be deleted here (not even by another admin).
        Assert.Equal(DeleteAdminResult.ConfiguredAdmin, await DeleteAsync(main, otherId));
        Assert.True(await IsAdminAsync(main));

        Assert.Equal(DeleteAdminResult.NotFound, await DeleteAsync("no-such-id", main));
        Assert.Equal(DeleteAdminResult.NotAnAdmin, await DeleteAsync(customerId, main));
        Assert.NotNull(await FindUserAsync(customerId)); // a customer is never deleted here

        await DeleteAsync(otherId, main);
    }

    [Fact]
    public async Task Delete_AnAdminMayDeleteTheirOwnAccount_WhenAnotherAdminRemains()
    {
        var main = await MainAdminIdAsync();
        var (id, _) = await NewAdminAsync(main);

        Assert.Equal(DeleteAdminResult.Deleted, await DeleteAsync(id, id));

        Assert.Null(await FindUserAsync(id));
        Assert.True(await IsAdminAsync(main));
    }

    [Fact]
    public async Task Delete_NeverRemovesTheLastAdmin_EvenWhenTwoAdminsDeleteEachOtherAtOnce()
    {
        var main = await MainAdminIdAsync();
        var (idA, _) = await NewAdminAsync(main);
        var (idB, _) = await NewAdminAsync(main);

        // For this test only, every other admin (the main one and any left by other tests) loses the role,
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
                Task.Run(() => DeleteAsync(idB, idA)),   // A deletes B
                Task.Run(() => DeleteAsync(idA, idB)));  // B deletes A

            Assert.Equal(1, results.Count(r => r == DeleteAdminResult.Deleted));
            Assert.Equal(1, results.Count(r => r == DeleteAdminResult.LastAdmin));
            Assert.Equal(1, new[] { await IsAdminAsync(idA), await IsAdminAsync(idB) }.Count(x => x)); // exactly one admin is left

            // The one who is left cannot delete their own account either.
            var survivor = await IsAdminAsync(idA) ? idA : idB;
            Assert.Equal(DeleteAdminResult.LastAdmin, await DeleteAsync(survivor, survivor));
            Assert.True(await IsAdminAsync(survivor));
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
    public async Task ADeletedAdmin_IsStoppedAtOnce_EvenWithTheirOldSignInCookie()
    {
        var main = await MainAdminIdAsync();
        var (id, email) = await NewAdminAsync(main);

        var session = await SignedInClientAsync(email);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/en/Admin")).StatusCode);

        await DeleteAsync(id, main);

        // The same cookie, a moment later: refused.
        var response = await session.GetAsync("/en/Admin/Orders");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());

        // Actions too, not only pages.
        var post = await PostFormListAsync(session, "/en/Orders/Lookup", "/en/Admin/Settings/Save", [F("DeliveryFee", "0")]);
        Assert.Contains("/Account/AccessDenied", post.Headers.Location!.ToString());
    }

    // ---- The screens ----

    [Theory]
    [InlineData("/en/Admin/Admins")]
    [InlineData("/en/Admin/Admins/Create")]
    [InlineData("/ru/Admin/Audit")]
    public async Task TheAdminsAndAuditPages_RefuseVisitorsAndCustomers(string url)
    {
        Assert.Contains("/Account/Login", (await NewClient().GetAsync(url)).Headers.Location!.ToString());

        var customer = NewClient();
        await SignedInUserAsync(customer);
        Assert.Contains("/Account/AccessDenied", (await customer.GetAsync(url)).Headers.Location!.ToString());
    }

    [Fact]
    public async Task ACustomer_CannotCreateEditOrDeleteAdmins()
    {
        var main = await MainAdminIdAsync();
        var customer = NewClient();
        var customerEmail = await SignedInUserAsync(customer);
        var newAdminEmail = NewEmail();

        var create = await PostFormListAsync(customer, "/en/Account", "/en/Admin/Admins/Create", [F("FullName", "X"), F("Email", newAdminEmail), F("Phone", "091 123456"), F("Password", Password)]);
        var edit = await PostFormListAsync(customer, "/en/Account", "/en/Admin/Admins/Edit", [F("Id", main), F("FullName", "Hacked"), F("Email", ShopWebFactory.AdminEmail), F("Phone", "091 123456"), F("Password", "Hacked-123")]);
        var delete = await PostFormListAsync(customer, "/en/Account", "/en/Admin/Admins/Delete", [F("id", main)]);

        foreach (var response in new[] { create, edit, delete })
        {
            Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());
        }

        Assert.Null(await FindUserByEmailAsync(newAdminEmail));
        Assert.False(await IsAdminAsync((await FindUserByEmailAsync(customerEmail))!.Id));
        Assert.True(await IsAdminAsync(main));
        Assert.NotEqual("Hacked", (await FindUserAsync(main))!.FullName);
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(NewClient(), ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword)).StatusCode); // the main admin's password is unchanged
    }

    [Theory]
    [InlineData("hy")]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task AdminsAndAuditPages_Open_AndHaveNoUntranslatedKeys(string culture)
    {
        var admin = await MainAdminClientAsync();
        var main = await MainAdminIdAsync();

        foreach (var path in new[] { "/Admins", "/Admins/Create", $"/Admins/Edit/{main}", "/Audit" })
        {
            var response = await admin.GetAsync($"/{culture}/Admin{path}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await BodyAsync(response);
            var text = Regex.Replace(Regex.Replace(html, "<script[\\s\\S]*?</script>", " "), "<[^>]+>", " ");
            Assert.Empty(Regex.Matches(text, @"\b(Admin|Val|Status|Pay|Checkout|Label)_[A-Za-z_]+\b").Select(m => m.Value).Distinct());
        }
    }

    [Fact]
    public async Task AdminsPage_MarksYouAndTheMainAdmin_AndOffersEditForAll_AndDeleteForAllButTheMainAdmin()
    {
        var main = await MainAdminIdAsync();
        var (otherId, otherEmail) = await NewAdminAsync(main);
        var admin = await MainAdminClientAsync();

        var html = await admin.GetStringAsync("/en/Admin/Admins");

        Assert.Contains(otherEmail, html);
        Assert.Contains($"href=\"/en/Admin/Admins/Edit/{otherId}\"", html);
        Assert.Contains($"href=\"/en/Admin/Admins/Edit/{main}\"", html);
        Assert.Contains($"name=\"id\" value=\"{otherId}\"", html);          // can delete the other
        Assert.DoesNotContain($"name=\"id\" value=\"{main}\"", html);         // not the main admin
        Assert.Contains("href=\"/en/Admin/Admins/Create\"", html);

        // On another admin's own page their own row can be deleted by themself too.
        var other = await SignedInClientAsync(otherEmail);
        Assert.Contains($"name=\"id\" value=\"{otherId}\"", await other.GetStringAsync("/en/Admin/Admins"));

        await DeleteAsync(otherId, main);
    }

    [Fact]
    public async Task Admin_CreatesEditsAndDeletesAnAdmin_ThroughThePages_WithClearMessages()
    {
        var admin = await MainAdminClientAsync();
        var main = await MainAdminIdAsync();
        var (_, customerEmail) = await NewUserAsync();
        const string list = "/en/Admin/Admins";
        const string createPage = "/en/Admin/Admins/Create";

        async Task<string> Bar() => WebUtility.HtmlDecode(await admin.GetStringAsync(list));
        Task<HttpResponseMessage> Create(string name, string email, string phone, string password) =>
            PostFormListAsync(admin, createPage, createPage, [F("FullName", name), F("Email", email), F("Phone", phone), F("Password", password)]);

        // The form is there, with a password box that does not show what is typed.
        var form = await admin.GetStringAsync(createPage);
        Assert.Contains("type=\"password\"", form);
        Assert.Contains("autocomplete=\"new-password\"", form);

        // Wrong input shows the form again with the problem next to the field; nothing is created.
        var email = NewEmail();
        foreach (var (name, mail, phone, password, expected) in new[]
        {
            ("", email, "091 123456", Password, "is required."),
            ("Anna", "not-an-email", "091 123456", Password, "Enter a valid email address."),
            ("Anna", email, "12", Password, "Enter a valid phone number"),
            ("Anna", email, "091 123456", "", "is required."),
            ("Anna", customerEmail, "091 123456", Password, "Another account already uses this email."),
            ("Anna", email, "091 123456", "short1", "8")
        })
        {
            var response = await Create(name, mail, phone, password);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = WebUtility.HtmlDecode(await BodyAsync(response));
            Assert.Contains(expected, html);
            Assert.Contains("field-validation-error", html);
        }

        Assert.Null(await FindUserByEmailAsync(email));

        // A good one.
        Assert.Equal(HttpStatusCode.Redirect, (await Create("Anna Admin", email, "091 123456", Password)).StatusCode);
        var created = await Bar();
        Assert.Contains($"Admin {email} created. They can sign in now.", created);
        Assert.Contains(email, created);
        var id = (await FindUserByEmailAsync(email))!.Id;
        Assert.True(await IsAdminAsync(id));

        // Editing: the page shows the saved values; empty password keeps the old one.
        var editPage = $"/en/Admin/Admins/Edit/{id}";
        var editHtml = await admin.GetStringAsync(editPage);
        Assert.Contains("value=\"Anna Admin\"", editHtml);
        Assert.Contains($"value=\"{email}\"", editHtml);

        var save = await PostFormListAsync(admin, editPage, "/en/Admin/Admins/Edit",
            [F("Id", id), F("FullName", "Anna Renamed"), F("Email", email), F("Phone", "093 222333"), F("Password", "")]);
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        Assert.Contains("Changes saved.", await Bar());
        Assert.Contains("Anna Renamed", await Bar());
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(NewClient(), email, Password)).StatusCode);

        // A taken email on edit stays on the form.
        var taken = await PostFormListAsync(admin, editPage, "/en/Admin/Admins/Edit",
            [F("Id", id), F("FullName", "Anna Renamed"), F("Email", customerEmail), F("Phone", "093 222333"), F("Password", "")]);
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        Assert.Contains("Another account already uses this email.", WebUtility.HtmlDecode(await BodyAsync(taken)));

        // The main admin: the email box is read-only, and a forged email is refused with a clear message.
        var mainEditPage = $"/en/Admin/Admins/Edit/{main}";
        Assert.Matches("<input[^>]*readonly[^>]*value=\"" + Regex.Escape(ShopWebFactory.AdminEmail) + "\"|<input[^>]*value=\"" + Regex.Escape(ShopWebFactory.AdminEmail) + "\"[^>]*readonly", await admin.GetStringAsync(mainEditPage));
        var ownPage = await admin.GetStringAsync(mainEditPage);
        Assert.DoesNotContain("name=\"Password\"", ownPage);                      // your own password is changed on the account page
        Assert.Contains("href=\"/en/Account/ChangePassword\"", ownPage);
        Assert.Contains("name=\"Password\"", await admin.GetStringAsync(editPage)); // another admin's password can be set here
        var forged = await PostFormListAsync(admin, mainEditPage, "/en/Admin/Admins/Edit",
            [F("Id", main), F("FullName", "Administrator"), F("Email", NewEmail()), F("Phone", "091 123456"), F("Password", "")]);
        Assert.Equal(HttpStatusCode.OK, forged.StatusCode);
        Assert.Contains("cannot be changed here", WebUtility.HtmlDecode(await BodyAsync(forged)));
        Assert.Equal(ShopWebFactory.AdminEmail, (await FindUserAsync(main))!.Email);

        // Deleting: the main admin cannot be deleted even by a forged form; the new one can; an unknown id is not found.
        await PostFormListAsync(admin, list, "/en/Admin/Admins/Delete", [F("id", main)]);
        Assert.Contains("The main admin is set in the site configuration and cannot be deleted here.", await Bar());
        Assert.True(await IsAdminAsync(main));

        Assert.Equal(HttpStatusCode.Redirect, (await PostFormListAsync(admin, list, "/en/Admin/Admins/Delete", [F("id", id)])).StatusCode);
        Assert.Contains("Admin account deleted.", await Bar());
        Assert.Null(await FindUserAsync(id));

        Assert.Equal(HttpStatusCode.NotFound, (await PostFormListAsync(admin, list, "/en/Admin/Admins/Delete", [F("id", "no-such-id")])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/en/Admin/Admins/Edit/no-such-id")).StatusCode);
    }

    [Fact]
    public async Task AnAdmin_DeletingTheirOwnAccount_ThroughThePage_IsSignedOutAtOnce()
    {
        var main = await MainAdminIdAsync();
        var (id, email) = await NewAdminAsync(main);
        var session = await SignedInClientAsync(email);

        var response = await PostFormListAsync(session, "/en/Admin/Admins", "/en/Admin/Admins/Delete", [F("id", id)]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/en", response.Headers.Location!.OriginalString);
        Assert.Null(await FindUserAsync(id));
        Assert.Contains("/Account/Login", (await session.GetAsync("/en/Admin")).Headers.Location!.ToString()); // not signed in any more
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
        var (newAdminId, email) = await NewAdminAsync(main); // writes an "Admin.Created" line

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
        Assert.Contains("Admin.Created", html);
        Assert.Contains(email, html);
        Assert.Contains("Settings.Saved", html);
        Assert.Contains(ShopWebFactory.AdminEmail, html);
        Assert.Contains("href=\"/en/Admin/Admins\"", html); // the "User #..." item links to the admins page
        Assert.Contains("href=\"/en/Admin/Settings\"", html);

        // Filtering by kind.
        var onlyUsers = await admin.GetStringAsync("/en/Admin/Audit?EntityType=User");
        Assert.Contains("Admin.Created", onlyUsers);
        Assert.DoesNotContain("Settings.Saved", onlyUsers);

        await DeleteAsync(newAdminId, main);
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

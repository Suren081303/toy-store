using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shop.Core.Entities;
using Shop.Core.Identity;

namespace Shop.Tests.Web;

[Collection("Web")]
public class AccountFlowTests : WebTestBase
{
    public AccountFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Register_SendsAConfirmationEmail_AndShowsTheCheckEmailPage()
    {
        var client = NewClient();
        var email = NewEmail();

        var response = await RegisterAsync(client, email);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/en/Account/RegisterConfirmation", response.Headers.Location!.OriginalString);
        Assert.Equal(1, Fixture.Factory.Emails.CountTo(email));
        Assert.Contains("ConfirmEmail", Fixture.Factory.Emails.LastLinkFor(email));
    }

    [Fact]
    public async Task Register_GivesTheNewUserTheCustomerRole_AndNormalizesThePhone()
    {
        var client = NewClient();
        var email = NewEmail();

        await RegisterAsync(client, email, phone: "(091) 123-456");

        using var scope = Fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);

        Assert.NotNull(user);
        Assert.Equal("+37491123456", user.PhoneNumber);
        Assert.False(user.EmailConfirmed);
        Assert.True(await users.IsInRoleAsync(user, Roles.Customer));
        Assert.False(await users.IsInRoleAsync(user, Roles.Admin));
    }

    [Fact]
    public async Task Login_BeforeTheEmailIsConfirmed_IsRefused()
    {
        var client = NewClient();
        var email = NewEmail();
        await RegisterAsync(client, email);

        var response = await LoginAsync(client, email, Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Your email is not confirmed yet", await BodyAsync(response));

        // Still a guest: the account page sends us to the sign-in page.
        var account = await client.GetAsync("/en/Account");
        Assert.Equal(HttpStatusCode.Redirect, account.StatusCode);
    }

    [Fact]
    public async Task Login_WithAWrongPasswordForAnUnconfirmedAccount_DoesNotRevealThatItIsUnconfirmed()
    {
        var client = NewClient();
        var email = NewEmail();
        await RegisterAsync(client, email);

        var response = await LoginAsync(client, email, "Wrong-Pass-999");

        var body = await BodyAsync(response);
        Assert.Contains("Wrong email or password", body);
        Assert.DoesNotContain("not confirmed", body);
    }

    [Fact]
    public async Task ConfirmEmail_ThenLogin_ShowsTheAccountPage()
    {
        var client = NewClient();
        var email = NewEmail();
        await RegisterAsync(client, email, name: "Anna Petrosyan");

        var confirm = await client.GetAsync(Fixture.Factory.Emails.LastLinkFor(email));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Contains("Email confirmed", await BodyAsync(confirm));

        var login = await LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/en", login.Headers.Location!.OriginalString);

        var account = await client.GetAsync("/en/Account");
        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
        var html = await BodyAsync(account);
        Assert.Contains("value=\"Anna Petrosyan\"", html);
        Assert.Contains(email, html);

        // The header greets the customer by first name.
        var home = await BodyAsync(await client.GetAsync("/en"));
        Assert.Contains("Anna", home);
    }

    [Fact]
    public async Task ConfirmEmail_WithAMangledLink_ShowsTheInvalidLinkPage()
    {
        var client = NewClient();

        var response = await client.GetAsync("/en/Account/ConfirmEmail?userId=nobody&token=***");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("This link does not work", await BodyAsync(response));
    }

    [Fact]
    public async Task Login_WithAWrongPassword_ShowsAGenericError_AndLocksTheAccountAfterFiveFailures()
    {
        var client = NewClient();
        var email = NewEmail();
        await RegisterAsync(client, email);
        await ConfirmEmailAsync(client, email);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var failed = await LoginAsync(client, email, "Wrong-Pass-999");
            Assert.Contains("Wrong email or password", await BodyAsync(failed));
        }

        await LoginAsync(client, email, "Wrong-Pass-999");

        // Now even the right password is refused for a few minutes.
        var locked = await LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Contains("Too many attempts", await BodyAsync(locked));
    }

    [Fact]
    public async Task Login_UnknownEmail_ShowsTheSameErrorAsAWrongPassword()
    {
        var client = NewClient();

        var response = await LoginAsync(client, NewEmail(), Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Wrong email or password", await BodyAsync(response));
    }

    [Fact]
    public async Task Login_ReturnUrl_IsFollowedOnlyWhenItIsOnThisSite()
    {
        var client = NewClient();
        var email = NewEmail();
        await RegisterAsync(client, email);
        await ConfirmEmailAsync(client, email);

        var outside = await LoginAsync(client, email, Password, returnUrl: "https://evil.example/steal");
        Assert.Equal("/en", outside.Headers.Location!.OriginalString);

        var otherClient = NewClient();
        var inside = await LoginAsync(otherClient, email, Password, returnUrl: "/en/Products");
        Assert.Equal("/en/Products", inside.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ForgotPassword_ForAnUnknownEmail_SendsNothing_AndShowsTheSameMessage()
    {
        var client = NewClient();
        var email = NewEmail();

        var response = await PostFormAsync(client, "/en/Account/ForgotPassword", "/en/Account/ForgotPassword",
            new Dictionary<string, string> { ["Email"] = email });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("If there is an account for this address", await BodyAsync(response));
        Assert.Equal(0, Fixture.Factory.Emails.CountTo(email));
    }

    [Fact]
    public async Task ForgotPassword_ThenReset_ChangesThePassword()
    {
        var client = NewClient();
        var email = NewEmail();
        await RegisterAsync(client, email);
        await ConfirmEmailAsync(client, email);

        await PostFormAsync(client, "/en/Account/ForgotPassword", "/en/Account/ForgotPassword",
            new Dictionary<string, string> { ["Email"] = email });

        // Two emails to this person now: the confirmation and the reset.
        Assert.Equal(2, Fixture.Factory.Emails.CountTo(email));
        var resetLink = Fixture.Factory.Emails.LastLinkFor(email);
        Assert.Contains("ResetPassword", resetLink);

        var form = await client.GetAsync(resetLink);
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        var formHtml = await BodyAsync(form);
        var token = System.Text.RegularExpressions.Regex.Match(formHtml, "name=\"Token\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);

        var reset = await PostFormAsync(client, resetLink, "/en/Account/ResetPassword", new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Token"] = token,
            ["Password"] = "Brand-New-Pass-77"
        });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Contains("Password changed", await BodyAsync(reset));

        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(NewClient(), email, "Brand-New-Pass-77")).StatusCode);
        Assert.Contains("Wrong email or password", await BodyAsync(await LoginAsync(NewClient(), email, Password)));
    }

    [Fact]
    public async Task ResetPassword_WithAWrongToken_ShowsTheInvalidLinkPage()
    {
        var client = NewClient();
        var email = NewEmail();
        await RegisterAsync(client, email);
        await ConfirmEmailAsync(client, email);

        var response = await PostFormAsync(client, "/en/Account/Register", "/en/Account/ResetPassword", new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Token"] = Shop.Web.Identity.TokenEncoding.Encode("not-a-real-token"),
            ["Password"] = "Brand-New-Pass-77"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("This link does not work", await BodyAsync(response));
        // The old password still works.
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(NewClient(), email, Password)).StatusCode);
    }

    [Fact]
    public async Task Post_WithoutTheAntiforgeryToken_IsRejected()
    {
        var client = NewClient();

        var response = await client.PostAsync("/en/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "someone@example.com",
            ["Password"] = Password
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithAnEmailThatIsAlreadyUsed_ShowsAnError()
    {
        var client = NewClient();
        var email = NewEmail();
        await RegisterAsync(client, email);

        var second = await RegisterAsync(NewClient(), email);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("An account with this email already exists", await BodyAsync(second));
    }

    [Fact]
    public async Task Register_WithAWeakPassword_ShowsThePasswordRules()
    {
        var noDigit = await RegisterAsync(NewClient(), NewEmail(), password: "abcdefgh");
        Assert.Contains("at least one digit", await BodyAsync(noDigit));

        var noLetter = await RegisterAsync(NewClient(), NewEmail(), password: "12345678");
        Assert.Contains("at least one letter", await BodyAsync(noLetter));

        var tooShort = await RegisterAsync(NewClient(), NewEmail(), password: "abc123");
        Assert.Contains("at least 8 characters", await BodyAsync(tooShort));
    }

    [Fact]
    public async Task Register_WithAnInvalidPhone_ShowsAnError_AndCreatesNoAccount()
    {
        var email = NewEmail();

        var response = await RegisterAsync(NewClient(), email, phone: "12345");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Enter a valid phone number", await BodyAsync(response));

        using var scope = Fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Null(await users.FindByEmailAsync(email));
    }

    [Fact]
    public async Task Register_WithEmptyFields_ShowsTheMessagesInTheVisitorsLanguage()
    {
        var client = NewClient();

        var expected = new[]
        {
            ("en", "Full name is required.", "Enter a valid phone number"),
            ("ru", "Поле «Имя и фамилия» обязательно.", "Необходимо принять условия."),
            ("hy", "«Անուն Ազգանուն» դաշտը պարտադիր է։", "Անհրաժեշտ է ընդունել պայմանները։")
        };

        foreach (var (language, requiredMessage, otherMessage) in expected)
        {
            var response = await PostFormAsync(client, $"/{language}/Account/Register", $"/{language}/Account/Register",
                new Dictionary<string, string>
                {
                    ["FullName"] = "",
                    ["Email"] = "not-an-email",
                    ["Phone"] = "12345",
                    ["Password"] = "",
                    ["AcceptTerms"] = "false"
                });

            // Razor writes non-Latin text as numeric HTML entities.
            var html = System.Net.WebUtility.HtmlDecode(await BodyAsync(response));

            Assert.Contains(requiredMessage, html);
            Assert.Contains(otherMessage, html);
            Assert.DoesNotContain("Val_", html);
        }
    }

    [Fact]
    public async Task Register_WithoutAcceptingTheTerms_ShowsAnError()
    {
        var client = NewClient();

        var response = await PostFormAsync(client, "/en/Account/Register", "/en/Account/Register", new Dictionary<string, string>
        {
            ["FullName"] = "Test User",
            ["Email"] = NewEmail(),
            ["Phone"] = "091 123456",
            ["Password"] = Password,
            ["AcceptTerms"] = "false"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("You need to accept the terms", await BodyAsync(response));
    }

    [Fact]
    public async Task GuestVisitingAProtectedPage_IsSentToTheSignInPageInTheirLanguage()
    {
        var client = NewClient();

        var response = await client.GetAsync("/ru/Account");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/ru/Account/Login?returnUrl=%2Fru%2FAccount", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Profile_CanBeUpdated_AndThePhoneIsNormalized()
    {
        var client = NewClient();
        var email = await SignedInUserAsync(client);

        var response = await PostFormAsync(client, "/en/Account", "/en/Account", new Dictionary<string, string>
        {
            ["FullName"] = "  Anna   Karapetyan ",
            ["Phone"] = "077 11 22 33"
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = Fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        Assert.Equal("Anna Karapetyan", user!.FullName);
        Assert.Equal("+37477112233", user.PhoneNumber);

        // The greeting in the header changes at once.
        Assert.Contains("Anna", await BodyAsync(await client.GetAsync("/en")));
    }

    [Fact]
    public async Task ChangePassword_NeedsTheCurrentPassword()
    {
        var client = NewClient();
        var email = await SignedInUserAsync(client);

        var wrong = await PostFormAsync(client, "/en/Account/ChangePassword", "/en/Account/ChangePassword", new Dictionary<string, string>
        {
            ["CurrentPassword"] = "Wrong-Pass-999",
            ["NewPassword"] = "Another-Pass-55"
        });
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("Incorrect password", await BodyAsync(wrong));

        var right = await PostFormAsync(client, "/en/Account/ChangePassword", "/en/Account/ChangePassword", new Dictionary<string, string>
        {
            ["CurrentPassword"] = Password,
            ["NewPassword"] = "Another-Pass-55"
        });
        Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(NewClient(), email, "Another-Pass-55")).StatusCode);
    }

    [Fact]
    public async Task Logout_EndsTheSession()
    {
        var client = NewClient();
        await SignedInUserAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/en/Account")).StatusCode);

        var logout = await PostFormAsync(client, "/en/Account", "/en/Account/Logout", new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/en/Account")).StatusCode);
    }

    [Fact]
    public async Task TheSeededAdmin_CanSignIn_AndHasTheAdminRole()
    {
        var client = NewClient();

        var response = await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = Fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await users.FindByEmailAsync(ShopWebFactory.AdminEmail);
        Assert.True(await users.IsInRoleAsync(admin!, Roles.Admin));

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        Assert.True(await roles.RoleExistsAsync(Roles.Admin));
        Assert.True(await roles.RoleExistsAsync(Roles.Customer));
    }

    [Fact]
    public async Task AccountPages_WorkInAllThreeLanguages()
    {
        var client = NewClient();

        foreach (var language in new[] { "hy", "ru", "en" })
        {
            foreach (var page in new[] { "Login", "Register", "ForgotPassword", "ResendConfirmation" })
            {
                var response = await client.GetAsync($"/{language}/Account/{page}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains($"<html lang=\"{language}\"", await BodyAsync(response));
            }
        }
    }
}

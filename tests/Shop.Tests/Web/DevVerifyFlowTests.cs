using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shop.Infrastructure.Data;

namespace Shop.Tests.Web;

// In Development only, a "Verify my account" button confirms a new account without an email.
[Collection("Web")]
public class DevVerifyFlowTests : WebTestBase
{
    public DevVerifyFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    private async Task<bool> IsConfirmedAsync(string email)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        return await db.Users.Where(u => u.Email == email).Select(u => u.EmailConfirmed).SingleAsync();
    }

    private static bool HasVerifyForm(string html) =>
        Regex.IsMatch(html, "<form[^>]*action=\"/en/Account/DevVerify\"") && html.Contains("Verify my account");

    private Task<HttpResponseMessage> VerifyAsync(HttpClient client, string email, string formPage = "/en/Account/Login") =>
        PostFormAsync(client, formPage, "/en/Account/DevVerify", new Dictionary<string, string> { ["email"] = email });

    [Fact]
    public async Task AfterSignUp_TheVerifyButtonConfirmsTheAccount_AndTheCustomerCanSignIn()
    {
        var client = NewClient();
        var email = NewEmail();

        var register = await RegisterAsync(client, email);
        Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);

        // The page after sign-up still says "check your email" and now also offers the button.
        var page = await BodyAsync(await client.GetAsync(register.Headers.Location!));
        Assert.True(HasVerifyForm(page), "No verify button after sign-up");
        Assert.Contains($"name=\"email\" value=\"{email}\"", page);
        Assert.False(await IsConfirmedAsync(email));

        // Until then the password does not get the customer in.
        var early = await LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, early.StatusCode);
        Assert.Contains("name=\"email\"", await BodyAsync(early));

        // The button.
        var verified = await VerifyAsync(client, email, register.Headers.Location!.ToString());
        var text = await BodyAsync(verified);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        Assert.Contains("/en/Account/Login", text);
        Assert.False(HasVerifyForm(text));
        Assert.True(await IsConfirmedAsync(email));

        var signIn = await LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
    }

    [Fact]
    public async Task TheButtonIsAlsoOnTheNotConfirmedSignInMessage_ForAnAccountMadeEarlier()
    {
        var client = NewClient();
        var email = NewEmail();
        await RegisterAsync(client, email);

        var attempt = await LoginAsync(client, email, Password);
        var html = await BodyAsync(attempt);

        Assert.Equal(HttpStatusCode.OK, attempt.StatusCode);
        Assert.True(HasVerifyForm(html));
        Assert.Contains("Resend", html, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(client, email)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, email, Password)).StatusCode);
    }

    [Fact]
    public async Task TheEmailIsStillSent_SoTheOldWayKeepsWorking()
    {
        var client = NewClient();
        var email = NewEmail();
        await RegisterAsync(client, email);

        await ConfirmEmailAsync(client, email);

        Assert.True(await IsConfirmedAsync(email));
    }

    [Fact]
    public async Task NoButton_ForAnAccountThatIsAlreadyConfirmed_OrOneThatDoesNotExist()
    {
        var client = NewClient();
        var email = NewEmail();
        await RegisterAsync(client, email);
        await ConfirmEmailAsync(client, email);

        Assert.False(HasVerifyForm(await client.GetStringAsync($"/en/Account/RegisterConfirmation?email={Uri.EscapeDataString(email)}")));
        Assert.False(HasVerifyForm(await client.GetStringAsync($"/en/Account/RegisterConfirmation?email={Uri.EscapeDataString(NewEmail())}")));
        Assert.False(HasVerifyForm(await client.GetStringAsync("/en/Account/RegisterConfirmation")));
    }

    [Fact]
    public async Task VerifyingTwice_OrAnUnknownAddress_IsHarmless()
    {
        var client = NewClient();
        var email = NewEmail();
        await RegisterAsync(client, email);

        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(client, email)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(client, email)).StatusCode);
        Assert.True(await IsConfirmedAsync(email));

        var unknown = NewEmail();
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(client, unknown)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(client, "")).StatusCode);

        using var scope = Fixture.Factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Users.AnyAsync(u => u.Email == unknown)); // no account appears
    }

    [Fact]
    public async Task TheVerifyAction_NeedsTheFormToken()
    {
        var client = NewClient();
        var email = NewEmail();
        await RegisterAsync(client, email);

        var forged = await client.PostAsync("/en/Account/DevVerify", new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email }));

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.False(await IsConfirmedAsync(email));
    }

    [Theory]
    [InlineData("hy", "Հաստատել իմ հաշիվը")]
    [InlineData("ru", "Подтвердить мой аккаунт")]
    [InlineData("en", "Verify my account")]
    public async Task TheButton_IsTranslated(string culture, string label)
    {
        var client = NewClient();
        var email = NewEmail();
        await PostFormAsync(client, $"/{culture}/Account/Register", $"/{culture}/Account/Register", new Dictionary<string, string>
        {
            ["FullName"] = "Test User", ["Email"] = email, ["Phone"] = "091 123456", ["Password"] = Password, ["AcceptTerms"] = "true"
        });

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/{culture}/Account/RegisterConfirmation?email={Uri.EscapeDataString(email)}"));

        Assert.Contains(label, html);
        Assert.DoesNotContain("Dev_Verify", html);
    }

    // ---- Outside Development nothing of this exists ----

    [Fact]
    public async Task InProduction_ThereIsNoButton_AndTheVerifyAddressIsNotFound()
    {
        using var uploads = new TempFolder();
        using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Shop:BaseUrl"] = "https://shop.example.am",
                ["Shop:Email"] = "info@shop.example.am",
                ["Email:Provider"] = "Smtp",
                ["Email:FromAddress"] = "no-reply@shop.example.am",
                ["Email:Smtp:Host"] = "smtp.invalid",
                ["Payments:Provider"] = "",
                ["Uploads:Folder"] = uploads.Path
            }));
        });

        // https, because in Production the cookies are "secure" and are only sent back over https.
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var email = NewEmail();
        var register = await RegisterAsync(client, email);
        Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);

        var page = await BodyAsync(await client.GetAsync(register.Headers.Location!));
        Assert.False(HasVerifyForm(page));
        Assert.DoesNotContain("DevVerify", page);

        var attempt = await LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, attempt.StatusCode);
        Assert.DoesNotContain("DevVerify", await BodyAsync(attempt));

        var verify = await VerifyAsync(client, email);
        Assert.Equal(HttpStatusCode.NotFound, verify.StatusCode);
        Assert.False(await IsConfirmedAsync(email)); // still unconfirmed: only the emailed link can confirm it there
    }
}

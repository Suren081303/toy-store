using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Shop.Tests.Web;

// Helpers shared by the web tests: a browser-like client (keeps cookies, does not follow redirects) and form posting with the anti-forgery token.
public abstract class WebTestBase
{
    protected const string Password = "Test-Pass-123";

    protected WebTestBase(ShopWebFixture fixture)
    {
        Fixture = fixture;
    }

    protected ShopWebFixture Fixture { get; }

    protected static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    protected HttpClient NewClient(WebApplicationFactory<Program>? factory = null)
    {
        WebApplicationFactory<Program> site = factory ?? Fixture.Factory;

        return site.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
    }

    // The anti-forgery token of a form on the page.
    protected static async Task<string> FormTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No anti-forgery token on " + url);
        return match.Groups[1].Value;
    }

    protected static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string formUrl, string postUrl, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await FormTokenAsync(client, formUrl);
        return await client.PostAsync(postUrl, new FormUrlEncodedContent(fields));
    }

    // A form with some names repeated (a list of check boxes), sent like a browser does.
    protected static async Task<HttpResponseMessage> PostFormListAsync(
        HttpClient client, string formUrl, string postUrl, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var all = fields.Append(new KeyValuePair<string, string>("__RequestVerificationToken", await FormTokenAsync(client, formUrl)));
        return await client.PostAsync(postUrl, new FormUrlEncodedContent(all));
    }

    // A form with files (an upload), sent like a browser does. Each file is (form field, file name, bytes, content type).
    protected static async Task<HttpResponseMessage> PostMultipartAsync(
        HttpClient client,
        string formUrl,
        string postUrl,
        IEnumerable<KeyValuePair<string, string>> fields,
        params (string Field, string FileName, byte[] Content, string ContentType)[] files)
    {
        var content = new MultipartFormDataContent();
        foreach (var field in fields)
        {
            content.Add(new StringContent(field.Value), field.Key);
        }

        content.Add(new StringContent(await FormTokenAsync(client, formUrl)), "__RequestVerificationToken");

        foreach (var file in files)
        {
            var part = new ByteArrayContent(file.Content);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
            content.Add(part, file.Field, file.FileName);
        }

        return await client.PostAsync(postUrl, content);
    }

    protected static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client, string email, string password = Password, string phone = "091 123456", string name = "Test User")
    {
        return PostFormAsync(client, "/en/Account/Register", "/en/Account/Register", new Dictionary<string, string>
        {
            ["FullName"] = name,
            ["Email"] = email,
            ["Phone"] = phone,
            ["Password"] = password,
            ["AcceptTerms"] = "true"
        });
    }

    protected static Task<HttpResponseMessage> LoginAsync(
        HttpClient client, string email, string password, string returnUrl = "")
    {
        return PostFormAsync(client, "/en/Account/Login", "/en/Account/Login", new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl
        });
    }

    protected async Task ConfirmEmailAsync(HttpClient client, string email)
    {
        var response = await client.GetAsync(Fixture.Factory.Emails.LastLinkFor(email));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // A registered, confirmed customer, signed in on this client.
    protected async Task<string> SignedInUserAsync(HttpClient client)
    {
        var email = NewEmail();
        Assert.Equal(HttpStatusCode.Redirect, (await RegisterAsync(client, email)).StatusCode);
        await ConfirmEmailAsync(client, email);
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, email, Password)).StatusCode);
        return email;
    }

    protected static async Task<string> BodyAsync(HttpResponseMessage response)
    {
        return await response.Content.ReadAsStringAsync();
    }

    // The anti-forgery token that every page carries in a <meta> tag (the page script sends it with add-to-cart and heart requests).
    protected static async Task<string> PageTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/en");
        var match = Regex.Match(html, "name=\"request-verification-token\" content=\"([^\"]+)\"");
        Assert.True(match.Success, "The page has no request-verification-token.");
        return match.Groups[1].Value;
    }

    // A POST like the page script sends: form fields, the token header, and "Accept: application/json".
    protected static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, Dictionary<string, string> fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("RequestVerificationToken", await PageTokenAsync(client));
        request.Headers.Add("Accept", "application/json");
        return await client.SendAsync(request);
    }

    // The value of a cookie that the response sets, or null when it does not set it.
    protected static string? CookieValue(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
        {
            return null;
        }

        var header = headers.FirstOrDefault(h => h.StartsWith(name + "=", StringComparison.Ordinal));
        return header?[(name.Length + 1)..].Split(';')[0];
    }

    // True when the response removes the cookie (an expired date in the past).
    protected static bool CookieCleared(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues("Set-Cookie", out var headers)
            && headers.Any(h => h.StartsWith(name + "=", StringComparison.Ordinal) && h.Contains("1970", StringComparison.Ordinal));
    }

    // The id of a demo product that matches the filter (the site seeds demo products in Development).
    protected async Task<int> FindProductIdAsync(System.Linq.Expressions.Expression<Func<Shop.Core.Entities.Product, bool>> filter)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Shop.Infrastructure.Data.ShopDbContext>();

        return await db.Products
            .Where(p => !p.IsArchived && p.Category.IsActive)
            .Where(filter)
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .FirstAsync();
    }
}

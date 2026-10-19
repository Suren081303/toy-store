using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Shop.Tests.Web;

[Collection("Web")]
public class RateLimitTests : WebTestBase
{
    public RateLimitTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task AccountForms_AreLimitedPerAddress()
    {
        // The same site with a low limit: 3 account form posts per minute.
        var limited = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimiting:AccountPermitLimit"] = "3" })));

        var client = NewClient(limited);
        var email = NewEmail();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var allowed = await LoginAsync(client, email, "Wrong-Pass-999");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var blocked = await LoginAsync(client, email, "Wrong-Pass-999");

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Contains("Too many requests", await BodyAsync(blocked));

        // Reading pages is not limited.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/en/Account/Login")).StatusCode);
    }
}

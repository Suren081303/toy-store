using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shop.Core.Email;
using Shop.Infrastructure.Data;

namespace Shop.Tests.Web;

// Collects the emails the site "sends", so tests can read the links in them.
public class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<(string To, string Subject, string Html)> _sent = new();

    private volatile string? _failWith;

    // While this is set, every send fails with this message (like a mail server that is down). null = sending works again.
    public void FailWith(string? message) => _failWith = message;

    public Task SendAsync(string to, string subject, string htmlBody)
    {
        if (_failWith is not null)
        {
            throw new InvalidOperationException(_failWith);
        }

        _sent.Enqueue((to, subject, htmlBody));
        return Task.CompletedTask;
    }

    // Every email sent to this address: subject and HTML, oldest first.
    public IReadOnlyList<(string Subject, string Html)> To(string to) =>
        _sent.Where(e => e.To == to).Select(e => (e.Subject, e.Html)).ToList();

    public int CountTo(string to) => _sent.Count(e => e.To == to);

    // The HTML of the newest email to this address.
    public string LastHtmlTo(string to) => _sent.Last(e => e.To == to).Html;

    // The address (path and query) of the first link to an account page in the newest email to this person.
    public string LastLinkFor(string to)
    {
        var email = _sent.Last(e => e.To == to);
        var match = Regex.Match(email.Html, "href=\"(http[^\"]*/Account/[^\"]+)\"");
        Assert.True(match.Success, "No account link in the email to " + to);

        return new Uri(WebUtility.HtmlDecode(match.Groups[1].Value)).PathAndQuery;
    }
}

public class ShopWebFactory : WebApplicationFactory<Program>
{
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(WebRoot))
        {
            Directory.Delete(WebRoot, recursive: true);
        }
    }

    public const string ConnectionString =
        "Server=.\\SQLEXPRESS;Database=ShopDb_WebTests;Trusted_Connection=True;TrustServerCertificate=True";

    public const string AdminEmail = "admin-test@example.com";

    public const string AdminPassword = "Admin-Test-Pass1";

    public CapturingEmailSender Emails { get; } = new();

    // The site's web root for these tests: a throw-away folder, so uploaded photos never land in the project's real wwwroot.
    public string WebRoot { get; } = Path.Combine(Path.GetTempPath(), "shop-tests-web-" + Guid.NewGuid().ToString("N"));

    // Sends the queued emails now, like the background job would. Call it before checking what was "sent".
    public async Task FlushEmailsAsync()
    {
        while (true)
        {
            using var scope = Services.CreateScope();
            var handled = await scope.ServiceProvider.GetRequiredService<Shop.Core.Notifications.IEmailOutboxProcessor>().ProcessAsync();
            if (handled == 0)
            {
                return;
            }
        }
    }

    // The file on disk for a web path such as /uploads/products/5/abc.jpg
    public string FileOf(string webPath) => Path.Combine(WebRoot, webPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(WebRoot);
        builder.UseSetting(WebHostDefaults.WebRootKey, WebRoot);
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = ConnectionString,
            ["Admin:Email"] = AdminEmail,
            ["Admin:Password"] = AdminPassword,
            ["RateLimiting:AccountPermitLimit"] = "1000",
            ["Email:Folder"] = Path.Combine(Path.GetTempPath(), "shop-tests-emails"),

            // Emails are queued; the tests send them by hand (FlushEmailsAsync), so the background sender is switched off.
            ["Email:OutboxIntervalSeconds"] = "0",
            ["Shop:BaseUrl"] = "http://localhost"
        }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });
    }
}

// Creates the test database with the real migrations, then starts the site on it (the site seeds roles, the admin and demo products).
public class ShopWebFixture : IAsyncLifetime
{
    public ShopWebFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        using var db = CreateDbContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        Factory = new ShopWebFactory();
    }

    public async Task DisposeAsync()
    {
        Factory?.Dispose();

        using var db = CreateDbContext();
        await db.Database.EnsureDeletedAsync();
    }

    private static ShopDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ShopDbContext>().UseSqlServer(ShopWebFactory.ConnectionString).Options;
        return new ShopDbContext(options);
    }
}

[CollectionDefinition("Web")]
public class WebCollection : ICollectionFixture<ShopWebFixture>
{
}

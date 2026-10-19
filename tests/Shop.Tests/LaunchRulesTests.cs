using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shop.Core.Email;
using Shop.Infrastructure;
using Shop.Infrastructure.Email;
using Shop.Web.Hosting;
using Shop.Web.Security;
using Shop.Web.Seo;
using Shop.Web.Uploads;

namespace Shop.Tests;

public class SecurityHeadersTests
{
    [Fact]
    public void Policy_AllowsOnlyTheSitesOwnScriptsStylesAndFonts()
    {
        var policy = SecurityHeaders.BuildPolicy(null);

        Assert.Contains("default-src 'self'", policy);
        Assert.Contains("script-src 'self'", policy);
        Assert.Contains("style-src 'self'", policy);
        Assert.Contains("font-src 'self'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("object-src 'none'", policy);
        Assert.Contains("base-uri 'self'", policy);
        Assert.Contains("form-action 'self'", policy);

        // No inline scripts, no eval, no outside sources.
        Assert.DoesNotContain("unsafe-eval", policy);
        Assert.DoesNotContain("http", policy);
        var scriptDirective = policy.Split("; ").Single(d => d.StartsWith("script-src ", StringComparison.Ordinal));
        Assert.DoesNotContain("unsafe-inline", scriptDirective);

        // Only inline style="" attributes (which cannot run code) are allowed inline.
        Assert.Contains("style-src-attr 'unsafe-inline'", policy);
        Assert.DoesNotContain("style-src 'self' 'unsafe-inline'", policy);
    }

    [Fact]
    public void ExtraFormActionOrigins_AreAddedAsCleanOrigins()
    {
        var policy = SecurityHeaders.BuildPolicy(["https://pay.bank.example/start?x=1", " https://other.example "]);

        Assert.Contains("form-action 'self' https://pay.bank.example https://other.example;", policy);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    [InlineData("https://a.example; script-src *")]
    [InlineData("")]
    [InlineData("ftp://files.example")]
    public void ExtraFormActionOrigins_ThatAreNotWebOrigins_AreIgnored_SoASettingCannotAddDirectives(string entry)
    {
        var policy = SecurityHeaders.BuildPolicy([entry]);

        Assert.Equal(SecurityHeaders.BuildPolicy(null), policy);
        Assert.DoesNotContain("script-src *", policy);
    }
}

public class ProductionChecksTests
{
    private static string Rooted => Path.Combine(Path.GetTempPath(), "shop-uploads");

    private static Dictionary<string, string?> Good() => new()
    {
        ["ConnectionStrings:DefaultConnection"] = "Server=db;Database=ShopDb",
        ["Shop:BaseUrl"] = "https://shop.example.am",
        ["Shop:Email"] = "info@shop.example.am",
        ["Shop:Phone"] = "+374 10 111111",
        ["Shop:Address"] = "1 Main street, Yerevan",
        ["Shop:LegalName"] = "Toys LLC",
        ["Email:Provider"] = "Smtp",
        ["Email:FromAddress"] = "no-reply@shop.example.am",
        ["Email:Smtp:Host"] = "smtp.example.com",
        ["Admin:Email"] = "owner@shop.example.am",
        ["Admin:Password"] = "a-long-password-1",
        ["AllowedHosts"] = "shop.example.am",
        ["Uploads:Folder"] = Rooted
    };

    private static ProductionChecks.Result Run(Action<Dictionary<string, string?>>? change = null)
    {
        var values = Good();
        change?.Invoke(values);

        return ProductionChecks.Run(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    [Fact]
    public void AGoodConfiguration_HasNoErrorsAndNoWarnings()
    {
        var result = Run();

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://shop.example.am")]            // not https
    [InlineData("https://localhost")]                 // not public
    [InlineData("https://shop.example.am/shop")]      // has a path
    [InlineData("shop.example.am")]                   // not an address
    public void ABadBaseUrl_IsAnError(string baseUrl)
    {
        var result = Run(v => v["Shop:BaseUrl"] = baseUrl);

        Assert.Single(result.Errors);
        Assert.Contains("Shop:BaseUrl", result.Errors[0]);
    }

    [Theory]
    [InlineData("https://shop.example.am")]
    [InlineData("https://shop.example.am/")]
    [InlineData("https://www.shop.example.am:8443")]
    public void AGoodBaseUrl_IsAccepted(string baseUrl)
    {
        Assert.Empty(Run(v => v["Shop:BaseUrl"] = baseUrl).Errors);
    }

    [Fact]
    public void SampleOrMissingAddresses_AreErrors_BecauseEmailsWouldBounce()
    {
        Assert.Contains("Shop:Email", Assert.Single(Run(v => v["Shop:Email"] = "shop@example.com").Errors));
        Assert.Contains("Shop:Email", Assert.Single(Run(v => v.Remove("Shop:Email")).Errors));
        Assert.Contains("Email:FromAddress", Assert.Single(Run(v => v["Email:FromAddress"] = "no-reply@example.com").Errors));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("File")]
    public void WithoutTheSmtpProvider_EmailsWouldOnlyBeFiles_SoItIsAnError(string? provider)
    {
        var result = Run(v => v["Email:Provider"] = provider);

        Assert.Contains("Email:Provider", Assert.Single(result.Errors));
    }

    [Fact]
    public void SmtpSettings_AreChecked()
    {
        Assert.Contains("Email:Smtp:Host", Assert.Single(Run(v => v["Email:Smtp:Host"] = " ").Errors));
        Assert.Contains("Email:Smtp:Port", Assert.Single(Run(v => v["Email:Smtp:Port"] = "0").Errors));
        Assert.Contains("Email:Smtp:Port", Assert.Single(Run(v => v["Email:Smtp:Port"] = "99999").Errors));
        Assert.Contains("Email:Smtp:Port", Assert.Single(Run(v => v["Email:Smtp:Port"] = "abc").Errors));
        Assert.Contains("Email:Smtp:Security", Assert.Single(Run(v => v["Email:Smtp:Security"] = "tls").Errors));
        Assert.Empty(Run(v => { v["Email:Smtp:Security"] = "SslOnConnect"; v["Email:Smtp:Port"] = "465"; }).Errors);
    }

    [Fact]
    public void UnencryptedSmtp_IsAnError_BecauseEmailsCarrySignInLinks()
    {
        var result = Run(v => v["Email:Smtp:Security"] = "None");

        Assert.Contains("unencrypted", Assert.Single(result.Errors));
    }

    [Fact]
    public void TheFakeBank_IsAnErrorInProduction()
    {
        Assert.Contains("Payments:Provider", Assert.Single(Run(v => v["Payments:Provider"] = "Fake").Errors));
        Assert.Empty(Run(v => v["Payments:Provider"] = "").Errors);
    }

    [Fact]
    public void ANonRootedUploadsFolder_IsAnError()
    {
        Assert.Contains("Uploads:Folder", Assert.Single(Run(v => v["Uploads:Folder"] = "uploads").Errors));
    }

    [Fact]
    public void MissingNiceToHaves_AreWarningsNotErrors()
    {
        var result = Run(v =>
        {
            v.Remove("Admin:Password");
            v["AllowedHosts"] = "*";
            v["Shop:Phone"] = "+374 00 000 000";
            v.Remove("Shop:LegalName");
            v.Remove("Uploads:Folder");
        });

        Assert.Empty(result.Errors);
        Assert.Equal(5, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Contains("Admin:Email"));
        Assert.Contains(result.Warnings, w => w.Contains("AllowedHosts"));
        Assert.Contains(result.Warnings, w => w.Contains("Shop:Phone"));
        Assert.Contains(result.Warnings, w => w.Contains("Shop:LegalName"));
        Assert.Contains(result.Warnings, w => w.Contains("Uploads:Folder"));
    }

    [Fact]
    public void AnEmptyConfiguration_ListsEveryProblemAtOnce()
    {
        var result = ProductionChecks.Run(new ConfigurationBuilder().Build());

        Assert.True(result.Errors.Count >= 4);
        Assert.Contains(result.Errors, e => e.Contains("ConnectionStrings:DefaultConnection"));
        Assert.Contains(result.Errors, e => e.Contains("Shop:BaseUrl"));
        Assert.Contains(result.Errors, e => e.Contains("Shop:Email"));
        Assert.Contains(result.Errors, e => e.Contains("Email:Provider"));
    }
}

public class SmtpEmailSenderTests
{
    private static readonly EmailOptions Options = new() { FromName = "Shop", FromAddress = "no-reply@shop.example.am" };

    [Fact]
    public void Message_HasTheSenderRecipientSubjectAndBothBodies()
    {
        var message = SmtpEmailSender.BuildMessage(Options, "anna@example.com", "Ձեր պատվերը", "<p>Hello <b>Anna</b></p>");

        Assert.Equal("Shop", message.From.Mailboxes.Single().Name);
        Assert.Equal("no-reply@shop.example.am", message.From.Mailboxes.Single().Address);
        Assert.Equal("anna@example.com", message.To.Mailboxes.Single().Address);
        Assert.Equal("Ձեր պատվերը", message.Subject);
        Assert.Equal("<p>Hello <b>Anna</b></p>", message.HtmlBody);
        Assert.Equal("Hello Anna\n", message.TextBody.ReplaceLineEndings("\n")); // MimeKit stores lines with CR LF
    }

    [Fact]
    public void ABadRecipient_IsRefused_NotSilentlyDropped()
    {
        Assert.ThrowsAny<Exception>(() => SmtpEmailSender.BuildMessage(Options, "not an address", "s", "<p>b</p>"));
    }

    [Fact]
    public void TextCopy_KeepsLinkAddresses_BreaksBlocksIntoLines_AndDropsStyles()
    {
        var html = "<html><head><style>p{color:red}</style></head><body><h1>Order #10001</h1><p>Open <a href=\"https://shop.example.am/en/Orders/Details?number=10001&amp;key=abc\">your order</a>.</p>" +
                   "<p>Tom &amp; Jerry<br>Total: 12&nbsp;900</p><p><a href=\"https://x.example/\">https://x.example/</a></p></body></html>";

        var text = SmtpEmailSender.ToPlainText(html);

        Assert.DoesNotContain("color:red", text);
        Assert.DoesNotContain("<", text);
        Assert.Contains("Order #10001\n", text);
        Assert.Contains("your order (https://shop.example.am/en/Orders/Details?number=10001&key=abc)", text);
        Assert.Contains("Tom & Jerry\n", text);
        Assert.Contains("https://x.example/", text);
        Assert.DoesNotContain("https://x.example/ (https://x.example/)", text); // a link that is its own label is written once
    }

    [Theory]
    [InlineData(null, MailKit.Security.SecureSocketOptions.StartTls)]
    [InlineData("", MailKit.Security.SecureSocketOptions.StartTls)]
    [InlineData("StartTls", MailKit.Security.SecureSocketOptions.StartTls)]
    [InlineData("starttls", MailKit.Security.SecureSocketOptions.StartTls)]
    [InlineData("SslOnConnect", MailKit.Security.SecureSocketOptions.SslOnConnect)]
    [InlineData("None", MailKit.Security.SecureSocketOptions.None)]
    public void SecurityNames_AreMapped(string? name, MailKit.Security.SecureSocketOptions expected)
    {
        Assert.Equal(expected, SmtpEmailSender.SecurityOf(name));
    }

    [Fact]
    public void AnUnknownSecurityName_FailsLoudly()
    {
        var error = Assert.Throws<InvalidOperationException>(() => SmtpEmailSender.SecurityOf("tls"));

        Assert.Contains("Email:Smtp:Security", error.Message);
    }

    [Theory]
    [InlineData("Smtp", typeof(SmtpEmailSender))]
    [InlineData("smtp", typeof(SmtpEmailSender))]
    [InlineData("File", typeof(FileEmailSender))]
    [InlineData("", typeof(FileEmailSender))]
    public void TheProviderSetting_PicksTheSender(string provider, Type expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Server=.;Database=NotUsed",
            ["Email:Provider"] = provider
        }).Build();

        var services = new ServiceCollection().AddInfrastructure(configuration);

        Assert.Equal(expected, services.Single(d => d.ServiceType == typeof(IEmailSender)).ImplementationType);
    }
}

public class SeoLinksTests
{
    private static HttpRequest Request(string path, string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("internal.local");
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        return context.Request;
    }

    private static IConfiguration Config(string? baseUrl) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Shop:BaseUrl"] = baseUrl }).Build();

    [Fact]
    public void Origin_IsTheBaseUrl_WhenSet_OtherwiseTheRequestAddress()
    {
        var request = Request("/hy");
        var http = request.HttpContext;

        Assert.Equal("https://shop.example.am", SeoLinks.Origin(http, Config("https://shop.example.am/")));
        Assert.Equal("https://internal.local", SeoLinks.Origin(http, Config("")));
        Assert.Equal("https://internal.local", SeoLinks.Origin(http, Config(null)));
        Assert.Equal("https://internal.local", SeoLinks.Origin(http, Config("not a url")));
        Assert.True(SeoLinks.IsBaseUrlSet(Config("https://shop.example.am")));
        Assert.False(SeoLinks.IsBaseUrlSet(Config("")));
    }

    [Fact]
    public void Canonical_LeavesOutFiltersSortingPagingAndSearch_ButKeepsCategoryAndAge()
    {
        const string origin = "https://shop.example.am";

        Assert.Equal(origin + "/en/Products?category=3&age=2",
            SeoLinks.Canonical(origin, Request("/en/Products", "?sort=PriceAsc&page=3&q=bear&age=2&category=3&priceFrom=100"), "Products", "Index"));
        Assert.Equal(origin + "/en/Products",
            SeoLinks.Canonical(origin, Request("/en/Products", "?category=abc&age=-1&sort=Newest"), "Products", "Index"));
        Assert.Equal(origin + "/en/Products/Details/5",
            SeoLinks.Canonical(origin, Request("/en/Products/Details/5", "?utm_source=x"), "Products", "Details"));
    }

    [Theory]
    [InlineData("Cart", "Index", false, true)]
    [InlineData("Checkout", "Index", false, true)]
    [InlineData("Account", "Login", false, true)]
    [InlineData("Orders", "Details", false, true)]
    [InlineData("Favorites", "Index", false, true)]
    [InlineData("Home", "StatusPage", false, true)]
    [InlineData("Home", "Error", false, true)]
    [InlineData("Gifts", "Index", true, true)]    // a gift search with answers
    [InlineData("Gifts", "Index", false, false)]  // the empty gift finder is a normal page
    [InlineData("Home", "Index", false, false)]
    [InlineData("Products", "Details", false, false)]
    [InlineData("Legal", "Terms", false, false)]
    public void PrivatePages_AreNotMeantForSearchResults(string controller, string action, bool hasQuery, bool expected)
    {
        Assert.Equal(expected, SeoLinks.IsPrivate(controller, action, hasQuery));
    }

    [Fact]
    public void Summary_IsOneShortLine_CutAtAWord()
    {
        Assert.Equal("One line two", SeoLinks.Summary("One   line\r\n\ttwo"));
        Assert.Equal(string.Empty, SeoLinks.Summary(null));

        var text = string.Join(' ', Enumerable.Repeat("word", 100));
        var summary = SeoLinks.Summary(text, 50);

        Assert.True(summary.Length <= 51);
        Assert.EndsWith("…", summary);
        Assert.DoesNotContain("wor…", summary);
    }

    [Theory]
    [InlineData("hy", "hy_AM")]
    [InlineData("ru", "ru_RU")]
    [InlineData("en", "en_US")]
    public void OgLocale_FollowsTheLanguage(string culture, string expected)
    {
        Assert.Equal(expected, SeoLinks.OgLocale(culture));
    }
}

public class UploadsFolderTests : IDisposable
{
    private readonly TempWebRoot _root = new();
    private readonly string _custom = Path.Combine(Path.GetTempPath(), "shop-tests-uploads-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _root.Dispose();

        if (Directory.Exists(_custom))
        {
            Directory.Delete(_custom, recursive: true);
        }
    }

    private static IConfiguration Config(string? folder) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Uploads:Folder"] = folder }).Build();

    [Fact]
    public void WithoutTheSetting_PhotosGoInWwwrootUploads()
    {
        Assert.Equal(Path.Combine(_root.WebRootPath, "uploads"), ImageStore.UploadsRoot(_root, Config(null)));
        Assert.Equal(Path.Combine(_root.WebRootPath, "uploads"), ImageStore.UploadsRoot(_root, Config("  ")));
    }

    [Fact]
    public async Task WithTheSetting_PhotosAreSavedAndDeletedInThatFolder_UnderTheSameWebAddress()
    {
        var store = new ImageStore(_root, Config(_custom));
        var bytes = TestImages.Jpeg(800, 600);

        var result = await store.SaveAsync(new MemoryStream(bytes), bytes.Length, "products/7", makeThumb: true);

        Assert.Null(result.ErrorKey);
        var image = result.Image!;
        Assert.StartsWith("/uploads/products/7/", image.Path);

        var onDisk = Path.Combine(_custom, image.Path["/uploads/".Length..].Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(onDisk));
        Assert.True(File.Exists(Path.Combine(_custom, image.ThumbPath!["/uploads/".Length..].Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(Directory.Exists(Path.Combine(_root.WebRootPath, "uploads")));

        store.Delete(image.Path, image.ThumbPath);

        Assert.False(File.Exists(onDisk));
    }

    [Fact]
    public async Task WithTheSetting_DeleteStillRefusesPathsOutsideTheFolder()
    {
        Directory.CreateDirectory(_custom);
        var outside = Path.Combine(Path.GetDirectoryName(_custom)!, "shop-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outside, "keep me");

        try
        {
            var store = new ImageStore(_root, Config(_custom));

            store.Delete("/uploads/../" + Path.GetFileName(outside), "/uploads/../../" + Path.GetFileName(outside), "/other/" + Path.GetFileName(outside));

            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }
}

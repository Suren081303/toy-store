using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shop.Core.Catalog;
using Shop.Core.Orders;

namespace Shop.Tests.Web;

// A catalog that always fails, like a database that is down.
public class ThrowingCatalog : ICatalogService
{
    private static Exception Down() => new InvalidOperationException("catalog is down: secret-detail-123");

    public Task<IReadOnlyList<CategoryItem>> GetCategoriesAsync(string culture) => throw Down();

    public Task<IReadOnlyList<AgeGroupItem>> GetAgeGroupsAsync(string culture) => throw Down();

    public Task<PagedResult<ProductListItem>> GetProductsAsync(ProductQuery query) => throw Down();

    public Task<ProductDetails?> GetProductAsync(int id, string culture) => throw Down();

    public Task<IReadOnlyList<ProductListItem>> GetNewArrivalsAsync(string culture, int count) => throw Down();

    public Task<SitemapData> GetSitemapDataAsync() => throw Down();

    public Task<IReadOnlyList<ProductListItem>> FindAsync(ProductQuery query, int count) => throw Down();

    public Task<IReadOnlyList<ProductListItem>> GetProductsByIdsAsync(IReadOnlyCollection<int> ids, string culture) => throw Down();
}

[Collection("Web")]
public class LaunchFlowTests : CheckoutTestBase
{
    public LaunchFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    private static readonly CultureInfo English = new("en");

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword)).StatusCode);
        return client;
    }

    // The same site with other settings (and, for the error tests, another environment or a broken catalog).
    private WebApplicationFactory<Program> Derived(
        Dictionary<string, string?> settings, string environment = "Development", Action<IServiceCollection>? services = null)
    {
        return Fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));

            if (services is not null)
            {
                builder.ConfigureTestServices(services);
            }
        });
    }

    private static Dictionary<string, string?> ProductionSettings(string uploads) => new()
    {
        ["Shop:BaseUrl"] = "https://shop.example.am",
        ["Shop:Email"] = "info@shop.example.am",
        ["Email:Provider"] = "Smtp",
        ["Email:FromAddress"] = "no-reply@shop.example.am",
        ["Email:Smtp:Host"] = "smtp.invalid",
        ["Payments:Provider"] = "",
        ["Uploads:Folder"] = uploads
    };

    private async Task<string> GetAsync(string url, HttpClient? client = null)
    {
        var response = await (client ?? NewClient()).GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BodyAsync(response);
    }

    private static string VisibleText(string html) =>
        WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<script[\\s\\S]*?</script>", " "), "<[^>]+>", " "));

    // ---- Security headers and the Content-Security-Policy ----

    [Theory]
    [InlineData("/en")]
    [InlineData("/ru/Products")]
    [InlineData("/hy/Account/Login")]
    [InlineData("/css/base.css")]
    [InlineData("/robots.txt")]
    [InlineData("/en/does-not-exist")]
    public async Task EveryResponse_CarriesTheSecurityHeaders(string url)
    {
        var response = await NewClient().GetAsync(url);
        var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(h => h.Key, h => string.Join(" ", h.Value), StringComparer.OrdinalIgnoreCase);

        Assert.Contains("script-src 'self'", headers["Content-Security-Policy"]);
        Assert.Contains("frame-ancestors 'none'", headers["Content-Security-Policy"]);
        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", headers["X-Frame-Options"]);
        Assert.Equal("strict-origin-when-cross-origin", headers["Referrer-Policy"]);
        Assert.Contains("camera=()", headers["Permissions-Policy"]);
        Assert.Equal("same-origin", headers["Cross-Origin-Opener-Policy"]);
    }

    [Fact]
    public async Task ExtraFormActionOrigins_ComeFromSettings()
    {
        using var factory = Derived(new() { ["Security:ExtraFormActionOrigins:0"] = "https://pay.bank.example/start" });

        var response = await NewClient(factory).GetAsync("/en");

        Assert.Contains("form-action 'self' https://pay.bank.example;", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    // The policy blocks inline scripts, so no page may need one: the browser would silently refuse it.
    private static readonly Regex InlineScript = new(@"<script(?![^>]*\bsrc=)(?![^>]*type=""application/ld(?:\+|&#x2B;)json"")[^>]*>", RegexOptions.IgnoreCase);

    // Razor writes the + of application/ld+json as &#x2B;; a browser reads both the same.
    private static readonly Regex StructuredData = new(@"<script type=""application/ld(?:\+|&#x2B;)json"">(.*?)</script>", RegexOptions.Singleline);
    private static readonly Regex EventHandler = new(@"\s(on[a-z]+)\s*=\s*""", RegexOptions.IgnoreCase);
    private static readonly Regex ScriptSource = new(@"<script[^>]*\bsrc=""([^""]+)""", RegexOptions.IgnoreCase);

    private static void AssertNoInlineCode(string html, string url)
    {
        Assert.False(InlineScript.IsMatch(html), "Inline script on " + url);
        Assert.False(EventHandler.IsMatch(html), "Inline event handler on " + url);
        Assert.All(ScriptSource.Matches(html).Select(m => m.Groups[1].Value), src => Assert.StartsWith("/", src));
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.False(Regex.IsMatch(html, "<link[^>]+href=\"https?://[^\"]*\\.css", RegexOptions.IgnoreCase), "Outside stylesheet on " + url);
    }

    [Fact]
    public async Task NoPublicPage_NeedsAnInlineScript_OrLoadsOutsideCode()
    {
        var productId = await FindProductIdAsync(_ => true);

        foreach (var url in new[]
        {
            "/hy", "/ru", "/en", "/en/Products", $"/en/Products/Details/{productId}", "/en/Categories", "/en/Gifts?find=true",
            "/en/Cart", "/en/Favorites", "/en/Account/Login", "/en/Account/Register", "/en/Orders/Lookup",
            "/en/Legal/Delivery", "/en/Legal/Terms", "/en/Home/Privacy"
        })
        {
            AssertNoInlineCode(await GetAsync(url), url);
        }

        var notFound = await NewClient().GetAsync("/en/no-such-page");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        AssertNoInlineCode(await BodyAsync(notFound), "404 page");
    }

    [Fact]
    public async Task NoAdminPage_NeedsAnInlineScript_OrLoadsOutsideCode()
    {
        var admin = await AdminClientAsync();

        foreach (var url in new[]
        {
            "/en/Admin", "/en/Admin/Orders", "/en/Admin/Returns", "/en/Admin/Products", "/en/Admin/Products/Edit", "/en/Admin/Categories",
            "/en/Admin/AgeGroups", "/en/Admin/Admins", "/en/Admin/Audit", "/en/Admin/Emails", "/en/Admin/Notifications",
            "/en/Admin/Analytics", "/en/Admin/Settings"
        })
        {
            AssertNoInlineCode(await GetAsync(url, admin), url);
        }
    }

    // ---- robots.txt and sitemap.xml ----

    [Fact]
    public async Task Robots_KeepsTheAdminAndVisitorPagesOut_AndPointsToTheSitemap()
    {
        var response = await NewClient().GetAsync("/robots.txt");
        var text = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("User-agent: *", text);

        foreach (var culture in new[] { "hy", "ru", "en" })
        {
            foreach (var path in new[] { "Admin", "Cart", "Checkout", "Account", "Orders", "Payments", "Favorites", "FakeBank" })
            {
                Assert.Contains($"Disallow: /{culture}/{path}\n", text);
            }
        }

        Assert.Contains("Disallow: /not-found", text);
        Assert.Contains("Sitemap: http://localhost/sitemap.xml", text);
        Assert.DoesNotContain("Disallow: /uploads", text); // product photos may be indexed
    }

    [Fact]
    public async Task Robots_WithoutABaseUrl_HasNoSitemapLine()
    {
        using var factory = Derived(new() { ["Shop:BaseUrl"] = "" });

        var text = await NewClient(factory).GetStringAsync("/robots.txt");

        Assert.DoesNotContain("Sitemap:", text);
        Assert.Contains("Disallow: /en/Admin", text);
    }

    [Fact]
    public async Task Sitemap_ListsEveryPublicPage_InThreeLanguages_WithTheirTranslations()
    {
        var response = await NewClient().GetAsync("/sitemap.xml");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/xml", response.Content.Headers.ContentType!.MediaType);

        var document = XDocument.Parse(await BodyAsync(response));
        XNamespace sm = "http://www.sitemaps.org/schemas/sitemap/0.9";
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        var urls = document.Root!.Elements(sm + "url").ToList();
        var locs = urls.Select(u => u.Element(sm + "loc")!.Value).ToList();

        using var scope = Fixture.Factory.Services.CreateScope();
        var data = await scope.ServiceProvider.GetRequiredService<ICatalogService>().GetSitemapDataAsync();

        // 7 fixed pages + each category + each product, in 3 languages.
        Assert.Equal((7 + data.CategoryIds.Count + data.ProductIds.Count) * 3, urls.Count);
        Assert.Equal(locs.Count, locs.Distinct().Count());
        Assert.All(locs, loc => Assert.StartsWith("http://localhost/", loc));

        foreach (var culture in new[] { "hy", "ru", "en" })
        {
            Assert.Contains($"http://localhost/{culture}", locs);
            Assert.Contains($"http://localhost/{culture}/Products", locs);
            Assert.Contains($"http://localhost/{culture}/Legal/Terms", locs);
            Assert.Contains($"http://localhost/{culture}/Legal/Delivery", locs);
            Assert.Contains($"http://localhost/{culture}/Home/Privacy", locs);
            Assert.Contains($"http://localhost/{culture}/Products/Details/{data.ProductIds[0]}", locs);
            Assert.Contains($"http://localhost/{culture}/Products?category={data.CategoryIds[0]}", locs);
        }

        // Each address lists all three versions of itself.
        var first = urls.First(u => u.Element(sm + "loc")!.Value == "http://localhost/ru/Products");
        var alternates = first.Elements(xhtml + "link").ToDictionary(l => l.Attribute("hreflang")!.Value, l => l.Attribute("href")!.Value);
        Assert.Equal(["hy", "ru", "en"], alternates.Keys.ToArray());
        Assert.Equal("http://localhost/en/Products", alternates["en"]);

        // Nothing private is listed.
        Assert.DoesNotContain(locs, loc => loc.Contains("/Admin") || loc.Contains("/Cart") || loc.Contains("/Account") || loc.Contains("/Checkout"));
    }

    [Fact]
    public async Task Sitemap_LeavesOutArchivedProducts()
    {
        int archivedId;
        using (var scope = Fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Shop.Infrastructure.Data.ShopDbContext>();
            var product = db.Products.OrderBy(p => p.Id).First(p => !p.IsArchived);
            product.IsArchived = true;
            await db.SaveChangesAsync();
            archivedId = product.Id;
        }

        try
        {
            var text = await NewClient().GetStringAsync("/sitemap.xml");

            Assert.DoesNotContain($"/en/Products/Details/{archivedId}<", text);
        }
        finally
        {
            using var scope = Fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Shop.Infrastructure.Data.ShopDbContext>();
            (await db.Products.FindAsync(archivedId))!.IsArchived = false;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Sitemap_WithoutABaseUrl_IsNotServed()
    {
        using var factory = Derived(new() { ["Shop:BaseUrl"] = "" });

        Assert.Equal(HttpStatusCode.NotFound, (await NewClient(factory).GetAsync("/sitemap.xml")).StatusCode);
    }

    // ---- Page tags for search engines and link previews ----

    private static string Meta(string html, string attribute, string name)
    {
        var match = Regex.Match(html, $"<meta {attribute}=\"{Regex.Escape(name)}\" content=\"([^\"]*)\"");
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : string.Empty;
    }

    private static string Canonical(string html)
    {
        var match = Regex.Match(html, "<link rel=\"canonical\" href=\"([^\"]*)\"");
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : string.Empty;
    }

    [Fact]
    public async Task Home_HasACanonicalAddress_OpenGraphTags_AndTranslations()
    {
        var html = await GetAsync("/ru");

        Assert.Equal("http://localhost/ru", Canonical(html));
        Assert.Equal("http://localhost/ru", Meta(html, "property", "og:url"));
        Assert.Equal("website", Meta(html, "property", "og:type"));
        Assert.Equal("ru_RU", Meta(html, "property", "og:locale"));
        Assert.NotEmpty(Meta(html, "property", "og:title"));
        Assert.NotEmpty(Meta(html, "property", "og:description"));
        Assert.DoesNotContain("noindex", html);
        Assert.Contains("hreflang=\"hy\" href=\"http://localhost/hy\"", html);
        Assert.Contains("hreflang=\"en\" href=\"http://localhost/en\"", html);
    }

    // Gives a product a real photo for a moment (the sample products only have SVG placeholders).
    private async Task<int> AddPhotoAsync(string path)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Shop.Infrastructure.Data.ShopDbContext>();
        var product = db.Products.OrderBy(p => p.Id).First(p => !p.IsArchived);
        db.Set<Shop.Core.Entities.ProductImage>().Add(new Shop.Core.Entities.ProductImage { ProductId = product.Id, Path = path, ThumbPath = path, SortOrder = -1 });
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task RemovePhotoAsync(string path)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Shop.Infrastructure.Data.ShopDbContext>();
        db.Set<Shop.Core.Entities.ProductImage>().RemoveRange(db.Set<Shop.Core.Entities.ProductImage>().Where(i => i.Path == path));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AProductPage_HasADescription_APreviewImage_AndProductData()
    {
        const string photo = "/uploads/products/1/launch-test.jpg";
        var productId = await AddPhotoAsync(photo);

        try
        {
            var html = await GetAsync($"/en/Products/Details/{productId}");

            Assert.Equal($"http://localhost/en/Products/Details/{productId}", Canonical(html));
            Assert.Equal("product", Meta(html, "property", "og:type"));

            var description = Meta(html, "name", "description");
            Assert.NotEmpty(description);
            Assert.True(description.Length <= 161);
            Assert.NotEqual("Toys for children and collectors. Delivery across Armenia.", description);

            Assert.Equal("http://localhost" + photo, Meta(html, "property", "og:image"));
            Assert.Equal("summary_large_image", Meta(html, "name", "twitter:card"));

            var json = StructuredData.Match(html).Groups[1].Value;
            using var data = JsonDocument.Parse(json);
            var root = data.RootElement;
            Assert.Equal("https://schema.org", root.GetProperty("@context").GetString());
            Assert.Equal("Product", root.GetProperty("@type").GetString());
            Assert.False(string.IsNullOrEmpty(root.GetProperty("sku").GetString()));
            Assert.Equal("AMD", root.GetProperty("offers").GetProperty("priceCurrency").GetString());
            Assert.Matches("^[0-9]+$", root.GetProperty("offers").GetProperty("price").GetString()!);
            Assert.Contains(root.GetProperty("offers").GetProperty("availability").GetString(), new[] { "https://schema.org/InStock", "https://schema.org/OutOfStock" });
            Assert.Equal("http://localhost" + photo, root.GetProperty("image")[0].GetString());
        }
        finally
        {
            await RemovePhotoAsync(photo);
        }
    }

    [Fact]
    public async Task AProductWithOnlyAPlaceholderPicture_IsSharedWithTheShopLogo()
    {
        var productId = await FindProductIdAsync(p => p.Images.Any() && p.Images.All(i => i.Path.EndsWith(".svg")));

        var html = await GetAsync($"/en/Products/Details/{productId}");

        Assert.Equal("http://localhost/images/logo/og-image.jpg", Meta(html, "property", "og:image"));
        Assert.DoesNotContain("\"image\"", StructuredData.Match(html).Groups[1].Value);
    }

    [Fact]
    public async Task TheStructuredData_CannotBeBrokenOutOf_ByAProductName()
    {
        int id;
        using (var scope = Fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Shop.Infrastructure.Data.ShopDbContext>();
            var product = db.Products.OrderBy(p => p.Id).First(p => !p.IsArchived);
            id = product.Id;
            var translation = db.Set<Shop.Core.Entities.ProductTranslation>().First(t => t.ProductId == id && t.Culture == "en");
            translation.Name = "Bear</script><script>alert(1)</script>";
            translation.Description = "A \"quoted\" </script> description";
            await db.SaveChangesAsync();
        }

        try
        {
            var html = await GetAsync($"/en/Products/Details/{id}");

            Assert.False(InlineScript.IsMatch(html));
            var json = StructuredData.Match(html).Groups[1].Value;
            using var data = JsonDocument.Parse(json);
            Assert.Equal("Bear</script><script>alert(1)</script>", data.RootElement.GetProperty("name").GetString());
            Assert.DoesNotContain("</script><script>", html);
        }
        finally
        {
            using var scope = Fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Shop.Infrastructure.Data.ShopDbContext>();
            var translation = db.Set<Shop.Core.Entities.ProductTranslation>().First(t => t.ProductId == id && t.Culture == "en");
            translation.Name = "Restored";
            translation.Description = "Restored";
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task TheCanonicalAddress_IgnoresSortingPagingAndSearch_ButKeepsTheCategory()
    {
        Assert.Equal("http://localhost/en/Products", Canonical(await GetAsync("/en/Products?sort=PriceAsc&page=1&q=bear&priceFrom=100")));
        Assert.Equal("http://localhost/en/Products?category=1", Canonical(await GetAsync("/en/Products?category=1&sort=Newest&isNew=true")));
    }

    [Theory]
    [InlineData("/en/Cart")]
    [InlineData("/en/Favorites")]
    [InlineData("/en/Account/Login")]
    [InlineData("/en/Orders/Lookup")]
    [InlineData("/en/Gifts?find=true")]
    public async Task VisitorPages_AreMarkedNoindex_AndHaveNoCanonical(string url)
    {
        var html = await GetAsync(url);

        Assert.Contains("<meta name=\"robots\" content=\"noindex, follow\"", html);
        Assert.Equal(string.Empty, Canonical(html));
    }

    [Fact]
    public async Task TheNotFoundPage_IsNoindex()
    {
        var response = await NewClient().GetAsync("/en/no-such-page");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("noindex", await BodyAsync(response));
    }

    [Fact]
    public async Task ThePlainGiftFinder_IsIndexable()
    {
        var html = await GetAsync("/en/Gifts");

        Assert.DoesNotContain("noindex", html);
        Assert.Equal("http://localhost/en/Gifts", Canonical(html));
    }

    // ---- Legal pages ----

    [Theory]
    [InlineData("hy")]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task TheLegalPages_Open_InEveryLanguage_WithoutUntranslatedKeys_OrTheDraftNote(string culture)
    {
        foreach (var path in new[] { "Legal/Delivery", "Legal/Terms", "Home/Privacy" })
        {
            var html = await GetAsync($"/{culture}/{path}");
            var text = VisibleText(html);

            Assert.Empty(Regex.Matches(text, @"\b(Legal|Gift|Admin|Val|Msg|Error|Nav)_[A-Za-z_]+\b").Select(m => m.Value).Distinct());
            Assert.Contains("<h2>", html);
            Assert.Contains("shop@example.com", html);                 // the contact block
            Assert.DoesNotContain("DRAFT", html);                       // the note for the owner stays in the source
            Assert.DoesNotContain("ЧЕРНОВИК", html);
            Assert.DoesNotContain("ՆԱԽԱԳԻԾ", html);
            Assert.DoesNotContain("@Model", html);                      // no template left unfilled
            Assert.NotEmpty(Meta(html, "name", "description"));
            Assert.NotEqual(Meta(await GetAsync($"/{culture}"), "name", "description"), Meta(html, "name", "description"));
        }
    }

    [Fact]
    public async Task TheDeliveryPage_SaysWhatTheShopReallyDoes()
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IStoreSettingsService>().GetAsync();

        var text = VisibleText(await GetAsync("/en/Legal/Delivery")).Replace(' ', ' ');

        Assert.Contains(settings.DeliveryFee.ToString("N0", English), text);
        Assert.Contains(settings.FreeDeliveryFrom.ToString("N0", English), text);
        Assert.Contains($"{settings.ReturnWindowDays} days after delivery", text);
        Assert.Contains($"within the next {settings.DeliveryDaysAhead} days", text);
        Assert.Contains("30 minutes", text);       // the card payment time limit
        Assert.Contains("3 days", text);           // the bank transfer time limit
        Assert.Contains("Online by card", text);   // the test site has the fake bank on
    }

    [Fact]
    public async Task TheDeliveryPage_DoesNotOfferCardPayment_WhenNoBankIsConnected()
    {
        using var factory = Derived(new(), services: services => services.RemoveAll<Shop.Core.Payments.IPaymentProvider>());

        var text = VisibleText(await GetAsync("/en/Legal/Delivery", NewClient(factory)));

        Assert.DoesNotContain("Online by card", text);
        Assert.Contains("Cash on delivery", text);
        Assert.Contains("Bank transfer", text);
    }

    [Fact]
    public async Task TheTermsPage_NamesTheSeller_AndTheCustomerDiscount()
    {
        using var factory = Derived(new() { ["Shop:LegalName"] = "Toys Armenia LLC", ["Shop:TaxId"] = "01234567" });

        var text = VisibleText(await GetAsync("/en/Legal/Terms", NewClient(factory)));

        Assert.Contains("run by Toys Armenia LLC", text);
        Assert.Contains("Tax ID 01234567", text);
        Assert.Contains($"{Shop.Core.Catalog.ProductRules.CustomerDiscountPercent}% off the regular price", text);
    }

    [Fact]
    public async Task TheFooter_LinksToTheRealLegalPages_AndTheOldComingSoonPageIsGone()
    {
        var home = await GetAsync("/en");

        Assert.Contains("href=\"/en/Legal/Delivery\"", home);
        Assert.Contains("href=\"/en/Legal/Terms\"", home);
        Assert.Contains("href=\"/en/Home/Privacy\"", home);
        Assert.DoesNotContain("Home/Soon", home);
        Assert.Equal(HttpStatusCode.NotFound, (await NewClient().GetAsync("/en/Home/Soon")).StatusCode);
    }

    [Fact]
    public async Task RegisterAndCheckout_LinkToTheTerms_InANewTab_SoTheFormIsNotLost()
    {
        var register = await GetAsync("/en/Account/Register");
        Assert.Contains("href=\"/en/Legal/Terms\" target=\"_blank\" rel=\"noopener\"", register);
        Assert.Contains("href=\"/en/Home/Privacy\" target=\"_blank\" rel=\"noopener\"", register);

        var guest = NewClient();
        await AddToCartAsync(guest, await PlainProductAsync());
        var checkout = await GetAsync("/en/Checkout", guest);
        Assert.Contains("href=\"/en/Legal/Terms\" target=\"_blank\" rel=\"noopener\"", checkout);
        Assert.DoesNotContain("Home/Soon", checkout);
    }

    // ---- The error page ----

    [Theory]
    [InlineData("/ru/error", "ru", "Ошибка")]
    [InlineData("/en/error", "en", "Error")]
    [InlineData("/hy/error", "hy", "Սխալ")]
    [InlineData("/error", "hy", "Սխալ")]
    public async Task TheErrorPage_IsInTheVisitorsLanguage_AndStandsAlone(string url, string culture, string title)
    {
        var response = await NewClient().GetAsync(url);
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains($"<html lang=\"{culture}\"", html);
        Assert.Contains($"<h1 class=\"page-title\">{title}</h1>", html);
        Assert.Contains($"href=\"/{culture}\"", html);                      // way back home
        Assert.Contains("noindex", html);
        Assert.DoesNotContain("category-nav", html);                         // no shop layout (it needs the database)
        Assert.DoesNotContain("Development Mode", html);                     // the old template text is gone
        Assert.Matches("<code>[^<]+</code>", html);                          // a reference code for support
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
    }

    [Theory]
    [InlineData("/ru/Products", "ru", "Ошибка")]
    [InlineData("/en/Products", "en", "Error")]
    [InlineData("/hy/Products", "hy", "Սխալ")]
    public async Task InProduction_AFailingPage_ShowsTheErrorPage_InTheSameLanguage_WithoutLeakingDetails(string url, string culture, string title)
    {
        using var uploads = new TempFolder();
        using var factory = Derived(ProductionSettings(uploads.Path), "Production", services => services.AddScoped<ICatalogService, ThrowingCatalog>());

        var response = await NewClient(factory).GetAsync(url);
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains($"<html lang=\"{culture}\"", html);
        Assert.Contains($"<h1 class=\"page-title\">{title}</h1>", html);
        Assert.DoesNotContain("secret-detail-123", html);
        Assert.DoesNotContain("InvalidOperationException", html);
        Assert.DoesNotContain("category-nav", html);
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
    }

    // ---- Start-up checks ----

    [Fact]
    public async Task InProduction_TheSiteStarts_WhenTheSettingsAreRight()
    {
        using var uploads = new TempFolder();
        using var factory = Derived(ProductionSettings(uploads.Path), "Production");

        var response = await NewClient(factory).GetAsync("/en/Legal/Terms");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("Shop:BaseUrl", "")]
    [InlineData("Shop:BaseUrl", "http://shop.example.am")]
    [InlineData("Email:Provider", "File")]
    [InlineData("Email:Smtp:Security", "None")]
    [InlineData("Payments:Provider", "Fake")]
    [InlineData("Shop:Email", "shop@example.com")]
    public void InProduction_ABadSetting_StopsTheSite_WithAMessageNamingIt(string key, string value)
    {
        using var uploads = new TempFolder();
        var settings = ProductionSettings(uploads.Path);
        settings[key] = value;
        using var factory = Derived(settings, "Production");

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("cannot start in Production", error.ToString());
        Assert.Contains(key, error.ToString());
    }

    [Fact]
    public void InDevelopment_NoProductionCheckApplies()
    {
        using var factory = Derived(new() { ["Shop:BaseUrl"] = "", ["Email:Provider"] = "File" });

        using var client = NewClient(factory);

        Assert.NotNull(client);
    }

    // ---- Uploads folder ----

    [Fact]
    public async Task WithUploadsFolder_PhotosAreServedFromThatFolder_AtTheSameAddress()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, "products", "9"));
        var bytes = TestImages.Jpeg(300, 200);
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, "products", "9", "photo.jpg"), bytes);
        using var factory = Derived(new() { ["Uploads:Folder"] = folder.Path });
        var client = NewClient(factory);

        var response = await client.GetAsync("/uploads/products/9/photo.jpg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/uploads/products/9/missing.jpg")).StatusCode);
    }
}

// A throw-away folder.
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shop-tests-folder-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

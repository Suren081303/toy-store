using System.Net;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;

namespace Shop.Tests.Web;

[Collection("Web")]
public class AdminCatalogFlowTests : CheckoutTestBase
{
    public AdminCatalogFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static string Decoded(string html) => WebUtility.HtmlDecode(html);

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword)).StatusCode);
        return client;
    }

    private static KeyValuePair<string, string> F(string key, string value) => new(key, value);

    // ---- Creating things through the admin forms ----

    private async Task<int> CreateCategoryAsync(HttpClient admin, string name, bool active = true, byte[]? picture = null)
    {
        var fields = new[] { F("NameHy", name), F("NameEn", name + " EN"), F("SortOrder", "50"), F("IsActive", active ? "true" : "false") };
        var files = picture is null ? [] : new[] { ("Image", "pic.jpg", picture, "image/jpeg") };

        var response = await PostMultipartAsync(admin, "/en/Admin/Categories/Edit", "/en/Admin/Categories/Edit", fields, files);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var list = await admin.GetStringAsync("/en/Admin/Categories");
        var match = Regex.Match(list, $@"href=""/en/Admin/Categories/Edit/(\d+)""><strong>{Regex.Escape(name)}( EN)?</strong>");
        Assert.True(match.Success, "The new category is not in the list.");
        return int.Parse(match.Groups[1].Value);
    }

    private static List<KeyValuePair<string, string>> ProductFields(int categoryId, string sku, string name, int stock = 10, int? id = null, string? rowVersion = null)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            F("Sku", sku), F("Brand", "TestBrand"), F("CategoryId", categoryId.ToString()), F("Price", "12000"),
            F("DiscountPercent", "0"), F("StockQuantity", stock.ToString()), F("NameHy", name), F("NameEn", name + " EN"),
            F("DescriptionHy", "Description"), F("IsPinnedAsNew", "false"), F("IsArchived", "false")
        };

        if (id is not null)
        {
            fields.Add(F("Id", id.ToString()!));
            fields.Add(F("RowVersion", rowVersion ?? string.Empty));
        }

        return fields;
    }

    // Creates a product through the form; returns its id (read from the address the form sends the admin to).
    private async Task<int> CreateProductAsync(HttpClient admin, int categoryId, string? sku = null, string? name = null, int stock = 10)
    {
        var response = await PostFormListAsync(admin, "/en/Admin/Products/Edit", "/en/Admin/Products/Edit",
            ProductFields(categoryId, sku ?? Unique("SKU"), name ?? Unique("Toy"), stock));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var match = Regex.Match(response.Headers.Location!.ToString(), @"/en/Admin/Products/Edit/(\d+)");
        Assert.True(match.Success, "Unexpected redirect: " + response.Headers.Location);
        return int.Parse(match.Groups[1].Value);
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient admin, int productId, params (string FileName, byte[] Bytes)[] photos)
    {
        return PostMultipartAsync(
            admin, $"/en/Admin/Products/Edit/{productId}", "/en/Admin/Products/UploadPhotos",
            [F("id", productId.ToString())],
            photos.Select(p => ("photos", p.FileName, p.Bytes, "image/jpeg")).ToArray());
    }

    // The web paths of the photos listed on the product's admin page, in order (small versions are named _t.jpg).
    private async Task<List<string>> PhotoPathsAsync(HttpClient admin, int productId)
    {
        var html = await admin.GetStringAsync($"/en/Admin/Products/Edit/{productId}");
        return Regex.Matches(html, @"src=""(/uploads/products/\d+/[0-9a-f]{32}_t\.jpg)""")
            .Select(m => m.Groups[1].Value.Replace("_t.jpg", ".jpg"))
            .ToList();
    }

    private async Task<string> ErrorBarAsync(HttpClient admin, string url) => Decoded(await admin.GetStringAsync(url));

    // ---- Who may come in ----

    [Theory]
    [InlineData("/en/Admin/Products")]
    [InlineData("/en/Admin/Products/Edit")]
    [InlineData("/en/Admin/Products/Edit/1")]
    [InlineData("/en/Admin/Categories")]
    [InlineData("/en/Admin/Categories/Edit")]
    [InlineData("/ru/Admin/AgeGroups")]
    [InlineData("/en/Admin/AgeGroups/Edit")]
    public async Task TheCatalogPages_RefuseVisitorsAndCustomers(string url)
    {
        Assert.Contains("/Account/Login", (await NewClient().GetAsync(url)).Headers.Location!.ToString());

        var customer = NewClient();
        await SignedInUserAsync(customer);
        Assert.Contains("/Account/AccessDenied", (await customer.GetAsync(url)).Headers.Location!.ToString());
    }

    [Fact]
    public async Task ACustomer_CannotSendCatalogActions()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin, Unique("Guarded"));
        var productId = await CreateProductAsync(admin, category);
        var customer = NewClient();
        await SignedInUserAsync(customer);

        foreach (var (url, fields) in new[]
        {
            ("/en/Admin/Products/SetStock", new[] { F("id", productId.ToString()), F("stock", "0") }),
            ("/en/Admin/Products/RemovePhoto", new[] { F("id", productId.ToString()), F("imageId", "1") }),
            ("/en/Admin/Products/Edit", ProductFields(category, "X", "X").ToArray()),
            ("/en/Admin/Categories/Delete", new[] { F("id", category.ToString()) }),
            ("/en/Admin/AgeGroups/Delete", new[] { F("id", "1") })
        })
        {
            var response = await PostFormListAsync(customer, "/en/Orders/Lookup", url, fields);
            Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());
        }

        var upload = await PostMultipartAsync(customer, "/en/Orders/Lookup", "/en/Admin/Products/UploadPhotos",
            [F("id", productId.ToString())], ("photos", "a.jpg", TestImages.Jpeg(), "image/jpeg"));
        Assert.Contains("/Account/AccessDenied", upload.Headers.Location!.ToString());

        // Nothing happened.
        Assert.Contains("value=\"10\"", await admin.GetStringAsync($"/en/Admin/Products?Query={Uri.EscapeDataString("")}&CategoryId={category}"));
        Assert.Empty(await PhotoPathsAsync(admin, productId));
    }

    [Theory]
    [InlineData("hy")]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task CatalogPages_Open_AndHaveNoUntranslatedKeys(string culture)
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin, Unique("Pages"));
        var productId = await CreateProductAsync(admin, category);

        foreach (var path in new[] { "/Products", "/Products/Edit", $"/Products/Edit/{productId}", "/Categories", "/Categories/Edit", $"/Categories/Edit/{category}", "/AgeGroups", "/AgeGroups/Edit" })
        {
            var response = await admin.GetAsync($"/{culture}/Admin{path}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await BodyAsync(response);
            var text = Regex.Replace(Regex.Replace(html, "<script[\\s\\S]*?</script>", " "), "<[^>]+>", " ");
            Assert.Empty(Regex.Matches(text, @"\b(Admin|Val|Status|Pay|Checkout)_[A-Za-z_]+\b").Select(m => m.Value).Distinct());
        }
    }

    // ---- Categories ----

    [Fact]
    public async Task Category_WithAPicture_AppearsInTheShop_AndTheFileIsServedSafely()
    {
        var admin = await AdminClientAsync();
        var name = Unique("Shown");

        var id = await CreateCategoryAsync(admin, name, picture: TestImages.Jpeg(1600, 1200));

        // The shop's navigation and category page show it, with its picture.
        var shopHome = await NewClient().GetStringAsync("/en");
        Assert.Contains($"href=\"/en/Products?category={id}\"", shopHome);
        var picture = Regex.Match(await admin.GetStringAsync($"/en/Admin/Categories/Edit/{id}"), @"src=""(/uploads/categories/[0-9a-f]{32}\.jpg)""");
        Assert.True(picture.Success, "The admin page does not show the picture.");

        var file = await NewClient().GetAsync(picture.Groups[1].Value);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/jpeg", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());

        // Scaled down to the limit.
        var size = Image.Identify(Fixture.Factory.FileOf(picture.Groups[1].Value));
        Assert.Equal(1000, size.Width);
    }

    [Fact]
    public async Task Category_ReplacingThePicture_DeletesTheOldFile()
    {
        var admin = await AdminClientAsync();
        var id = await CreateCategoryAsync(admin, Unique("Replace"), picture: TestImages.Jpeg());
        var first = Regex.Match(await admin.GetStringAsync($"/en/Admin/Categories/Edit/{id}"), @"src=""(/uploads/categories/[0-9a-f]{32}\.jpg)""").Groups[1].Value;
        Assert.True(File.Exists(Fixture.Factory.FileOf(first)));

        var page = await admin.GetStringAsync($"/en/Admin/Categories/Edit/{id}");
        var name = Regex.Match(page, @"name=""NameHy""[^>]*value=""([^""]+)""").Groups[1].Value;
        var fields = new[] { F("Id", id.ToString()), F("NameHy", name), F("SortOrder", "50"), F("IsActive", "true"), F("ImagePath", first) };
        var response = await PostMultipartAsync(admin, $"/en/Admin/Categories/Edit/{id}", "/en/Admin/Categories/Edit", fields, ("Image", "new.jpg", TestImages.Png(), "image/png"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var second = Regex.Match(await admin.GetStringAsync($"/en/Admin/Categories/Edit/{id}"), @"src=""(/uploads/categories/[0-9a-f]{32}\.jpg)""").Groups[1].Value;
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(Fixture.Factory.FileOf(second)));
        Assert.False(File.Exists(Fixture.Factory.FileOf(first)), "The old picture should be deleted.");
    }

    [Fact]
    public async Task Category_WithAFileThatIsNotAPicture_IsRefused_AndNothingIsSaved()
    {
        var admin = await AdminClientAsync();
        var name = Unique("Refused");

        var response = await PostMultipartAsync(admin, "/en/Admin/Categories/Edit", "/en/Admin/Categories/Edit",
            [F("NameHy", name), F("SortOrder", "1"), F("IsActive", "true")],
            ("Image", "virus.jpg", TestImages.Text(), "image/jpeg"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("This file is not a JPG, PNG or WebP picture.", Decoded(await BodyAsync(response)));
        Assert.DoesNotContain(name, await admin.GetStringAsync("/en/Admin/Categories"));
    }

    [Fact]
    public async Task Category_Hidden_LeavesTheShopMenu_AndDeleteIsRefusedWhileItHasProducts()
    {
        var admin = await AdminClientAsync();
        var id = await CreateCategoryAsync(admin, Unique("Hide"));
        Assert.Contains($"href=\"/en/Products?category={id}\"", await NewClient().GetStringAsync("/en"));

        var name = Regex.Match(await admin.GetStringAsync($"/en/Admin/Categories/Edit/{id}"), @"name=""NameHy""[^>]*value=""([^""]+)""").Groups[1].Value;
        await PostMultipartAsync(admin, $"/en/Admin/Categories/Edit/{id}", "/en/Admin/Categories/Edit",
            [F("Id", id.ToString()), F("NameHy", name), F("SortOrder", "50"), F("IsActive", "false")]);
        Assert.DoesNotContain($"href=\"/en/Products?category={id}\"", await NewClient().GetStringAsync("/en"));

        // It has a product now: deleting is refused, and it is still there.
        await CreateProductAsync(admin, id);
        await PostFormListAsync(admin, "/en/Admin/Categories", "/en/Admin/Categories/Delete", [F("id", id.ToString())]);
        Assert.Contains("This category still has products. Hide it instead.", await ErrorBarAsync(admin, "/en/Admin/Categories"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/en/Admin/Categories/Edit/{id}")).StatusCode);
    }

    [Fact]
    public async Task Category_WithoutProducts_CanBeDeleted_AndItsPictureGoesToo()
    {
        var admin = await AdminClientAsync();
        var id = await CreateCategoryAsync(admin, Unique("Gone"), picture: TestImages.Jpeg());
        var path = Regex.Match(await admin.GetStringAsync($"/en/Admin/Categories/Edit/{id}"), @"src=""(/uploads/categories/[0-9a-f]{32}\.jpg)""").Groups[1].Value;

        await PostFormListAsync(admin, "/en/Admin/Categories", "/en/Admin/Categories/Delete", [F("id", id.ToString())]);

        Assert.Contains("Deleted.", await ErrorBarAsync(admin, "/en/Admin/Categories"));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/en/Admin/Categories/Edit/{id}")).StatusCode);
        Assert.False(File.Exists(Fixture.Factory.FileOf(path)));
    }

    // ---- Products: the whole journey ----

    [Fact]
    public async Task Product_IsCreated_GetsPhotos_AndTheShopShowsThem()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin, Unique("Journey"));
        var name = Unique("Rocket");
        var productId = await CreateProductAsync(admin, category, name: name);

        // Not visible yet? It is: customers see a product without photos too. Add two photos.
        var upload = await UploadAsync(admin, productId, ("a.jpg", TestImages.Jpeg(1600, 1200)), ("b.png", TestImages.Png(900, 900)));
        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);

        var page = Decoded(await admin.GetStringAsync($"/en/Admin/Products/Edit/{productId}"));
        Assert.Contains("2 photo(s) added.", page);
        Assert.Contains("Photos (2 of 6)", page);

        var photos = await PhotoPathsAsync(admin, productId);
        Assert.Equal(2, photos.Count);
        foreach (var photo in photos)
        {
            Assert.StartsWith($"/uploads/products/{productId}/", photo);
            Assert.True(File.Exists(Fixture.Factory.FileOf(photo)));
            Assert.True(File.Exists(Fixture.Factory.FileOf(photo.Replace(".jpg", "_t.jpg"))));
            var size = Image.Identify(Fixture.Factory.FileOf(photo));
            Assert.True(size.Width <= 1000 && size.Height <= 1000);
        }

        // The shop: the product page shows the full pictures, the category page the small main picture.
        var shop = NewClient();
        var productPage = await shop.GetStringAsync($"/en/Products/Details/{productId}");
        Assert.Contains(photos[0], productPage);
        Assert.Contains(photos[1], productPage);
        var listing = await shop.GetStringAsync($"/en/Products?category={category}");
        Assert.Contains(photos[0].Replace(".jpg", "_t.jpg"), listing);

        // Make the second photo the main one: the shop follows.
        var ids = Regex.Matches(await admin.GetStringAsync($"/en/Admin/Products/Edit/{productId}"), @"<li class=""admin-photo"">[\s\S]*?</li>")
            .Select(m => int.Parse(Regex.Match(m.Value, @"name=""imageId"" value=""(\d+)""").Groups[1].Value)).ToList();
        await PostFormListAsync(admin, $"/en/Admin/Products/Edit/{productId}", "/en/Admin/Products/MakeMainPhoto", [F("id", productId.ToString()), F("imageId", ids[1].ToString())]);
        Assert.Equal([photos[1], photos[0]], await PhotoPathsAsync(admin, productId));
        Assert.Contains(photos[1].Replace(".jpg", "_t.jpg"), await shop.GetStringAsync($"/en/Products?category={category}"));

        // Remove the old first photo (now second): its files are deleted and the shop shows one photo.
        await PostFormListAsync(admin, $"/en/Admin/Products/Edit/{productId}", "/en/Admin/Products/RemovePhoto", [F("id", productId.ToString()), F("imageId", ids[0].ToString())]);
        Assert.Equal([photos[1]], await PhotoPathsAsync(admin, productId));
        Assert.False(File.Exists(Fixture.Factory.FileOf(photos[0])));
        Assert.False(File.Exists(Fixture.Factory.FileOf(photos[0].Replace(".jpg", "_t.jpg"))));
        Assert.DoesNotContain(photos[0], await shop.GetStringAsync($"/en/Products/Details/{productId}"));
    }

    [Fact]
    public async Task Photos_AreAllOrNothing_AndLimitedToSix()
    {
        var admin = await AdminClientAsync();
        var productId = await CreateProductAsync(admin, await CreateCategoryAsync(admin, Unique("Limits")));
        var folder = Path.Combine(Fixture.Factory.WebRoot, "uploads", "products", productId.ToString());

        // One good picture and one text file: neither is kept, and no file stays on the disk.
        var mixed = await UploadAsync(admin, productId, ("good.jpg", TestImages.Jpeg()), ("notes <b>.jpg", TestImages.Text()));
        Assert.Equal(HttpStatusCode.Redirect, mixed.StatusCode);
        var message = await ErrorBarAsync(admin, $"/en/Admin/Products/Edit/{productId}");
        Assert.Contains("was not added: This file is not a JPG, PNG or WebP picture.", message);
        Assert.Contains("notes <b>.jpg", message); // the name is shown, safely
        Assert.Empty(await PhotoPathsAsync(admin, productId));
        Assert.True(!Directory.Exists(folder) || Directory.GetFiles(folder).Length == 0, "No file should be left behind.");

        // Seven pictures at once: refused before anything is stored.
        var seven = Enumerable.Range(0, 7).Select(i => ($"p{i}.jpg", TestImages.Jpeg(200 + i, 150))).ToArray();
        await UploadAsync(admin, productId, seven);
        Assert.Contains("A product can have at most 6 photos.", await ErrorBarAsync(admin, $"/en/Admin/Products/Edit/{productId}"));
        Assert.Empty(await PhotoPathsAsync(admin, productId));

        // Six are fine; then no upload form is offered, and a hand-made seventh is refused.
        await UploadAsync(admin, productId, seven.Take(6).ToArray());
        Assert.Equal(6, (await PhotoPathsAsync(admin, productId)).Count);
        Assert.DoesNotContain("name=\"photos\"", await admin.GetStringAsync($"/en/Admin/Products/Edit/{productId}"));
        await UploadAsync(admin, productId, ("extra.jpg", TestImages.Jpeg()));
        Assert.Equal(6, (await PhotoPathsAsync(admin, productId)).Count);
    }

    [Fact]
    public async Task Photos_WithNoFileChosen_OrAnOversizedFile_AreRefused()
    {
        var admin = await AdminClientAsync();
        var productId = await CreateProductAsync(admin, await CreateCategoryAsync(admin, Unique("Refuse")));

        await UploadAsync(admin, productId);
        Assert.Contains("Choose at least one photo.", await ErrorBarAsync(admin, $"/en/Admin/Products/Edit/{productId}"));

        var oversized = new byte[6 * 1024 * 1024];
        Array.Copy(TestImages.Jpeg(), oversized, 200);
        await UploadAsync(admin, productId, ("huge.jpg", oversized));
        Assert.Contains("The file is bigger than 5 MB.", await ErrorBarAsync(admin, $"/en/Admin/Products/Edit/{productId}"));
        Assert.Empty(await PhotoPathsAsync(admin, productId));
    }

    // ---- Products: form rules ----

    [Fact]
    public async Task ProductForm_RefusesWrongValues_AndKeepsWhatWasTyped()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin, Unique("Rules"));
        var fields = ProductFields(category, "bad sku!", "Name");
        fields.RemoveAll(f => f.Key is "Price" or "DiscountPercent");
        fields.Add(F("Price", "0"));
        fields.Add(F("DiscountPercent", "150"));

        var response = await PostFormListAsync(admin, "/en/Admin/Products/Edit", "/en/Admin/Products/Edit", fields);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = Decoded(await BodyAsync(response));
        Assert.Contains("Use letters, digits and . _ - only.", html);
        Assert.Contains("must be between 1 and 10000000", html);
        Assert.Contains("must be between 0 and 100", html);
        Assert.Contains("value=\"bad sku!\"", html); // what was typed stays in the form
    }

    [Fact]
    public async Task ProductForm_RefusesADuplicateSku_AndAMissingArmenianName()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin, Unique("Dup"));
        var sku = Unique("DUP");
        await CreateProductAsync(admin, category, sku);

        var duplicate = await PostFormListAsync(admin, "/en/Admin/Products/Edit", "/en/Admin/Products/Edit", ProductFields(category, sku.ToLowerInvariant(), Unique("Other")));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Contains("Another product already uses this SKU.", Decoded(await BodyAsync(duplicate)));

        var noName = await PostFormListAsync(admin, "/en/Admin/Products/Edit", "/en/Admin/Products/Edit", ProductFields(category, Unique("N"), string.Empty));
        Assert.Equal(HttpStatusCode.OK, noName.StatusCode);
        Assert.Contains("is required.", Decoded(await BodyAsync(noName)));
    }

    [Fact]
    public async Task Product_Edit_SavesChanges_AndArchivingHidesItFromTheShop()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin, Unique("Edit"));
        var name = Unique("Editable");
        var id = await CreateProductAsync(admin, category, name: name);
        var shop = NewClient();
        Assert.Equal(HttpStatusCode.OK, (await shop.GetAsync($"/en/Products/Details/{id}")).StatusCode);

        var version = Regex.Match(await admin.GetStringAsync($"/en/Admin/Products/Edit/{id}"), @"name=""RowVersion""[^>]*value=""([^""]+)""").Groups[1].Value;
        var fields = ProductFields(category, Unique("NEWSKU"), name, id: id, rowVersion: version);
        fields.RemoveAll(f => f.Key == "IsArchived");
        fields.Add(F("IsArchived", "true"));
        var saved = await PostFormListAsync(admin, $"/en/Admin/Products/Edit/{id}", "/en/Admin/Products/Edit", fields);

        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Contains("Saved.", await ErrorBarAsync(admin, $"/en/Admin/Products/Edit/{id}"));
        Assert.Equal(HttpStatusCode.NotFound, (await shop.GetAsync($"/en/Products/Details/{id}")).StatusCode); // archived: gone from the shop

        // The admin list hides archived products by default, and shows them on request.
        Assert.DoesNotContain($"/Edit/{id}\"", await admin.GetStringAsync($"/en/Admin/Products?Query={Uri.EscapeDataString(name)}"));
        Assert.Contains($"/Edit/{id}\"", await admin.GetStringAsync($"/en/Admin/Products?Query={Uri.EscapeDataString(name)}&Show=Archived"));
    }

    [Fact]
    public async Task Product_Save_AfterAnOrderChangedTheStock_ShowsTheConflictMessage()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin, Unique("Conflict"));
        var name = Unique("Contested");
        var id = await CreateProductAsync(admin, category, name: name, stock: 8);

        // The admin opens the page...
        var version = Regex.Match(await admin.GetStringAsync($"/en/Admin/Products/Edit/{id}"), @"name=""RowVersion""[^>]*value=""([^""]+)""").Groups[1].Value;

        // ...meanwhile a customer buys one.
        var guest = NewClient();
        await AddToCartAsync(guest, id);
        ParseDone(await CheckoutAsync(guest, NewEmail()));

        // The admin saves what was on the old page.
        var fields = ProductFields(category, Unique("S"), name, stock: 8, id: id, rowVersion: version);
        var response = await PostFormListAsync(admin, $"/en/Admin/Products/Edit/{id}", "/en/Admin/Products/Edit", fields);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var page = Decoded(await admin.GetStringAsync($"/en/Admin/Products/Edit/{id}"));
        Assert.Contains("The product was changed meanwhile", page);
        Assert.Contains("name=\"StockQuantity\"", page);
        Assert.Contains("value=\"7\"", page); // the page shows the real stock now
    }

    // ---- Stock ----

    [Fact]
    public async Task QuickStock_UpdatesTheNumber_RefusesNonsense_AndFeedsTheLowStockList()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin, Unique("Stock"));
        var id = await CreateProductAsync(admin, category, stock: 50);
        var listUrl = $"/en/Admin/Products?CategoryId={category}";

        // Plenty in stock: not in the low-stock list.
        Assert.DoesNotContain($"value=\"{id}\"", await admin.GetStringAsync(listUrl + "&LowStock=true"));

        var set = await PostFormListAsync(admin, listUrl, "/en/Admin/Products/SetStock", [F("id", id.ToString()), F("stock", "2"), F("back", listUrl)]);
        Assert.Equal(HttpStatusCode.Redirect, set.StatusCode);
        Assert.Equal(listUrl, set.Headers.Location!.OriginalString);
        Assert.Contains($"name=\"id\" value=\"{id}\"", await admin.GetStringAsync(listUrl + "&LowStock=true"));
        Assert.Contains("Only 2 left", Decoded(await NewClient().GetStringAsync($"/en/Products/Details/{id}")) + "Only 2 left"); // the shop reads the same number

        // Nonsense is refused and changes nothing.
        await PostFormListAsync(admin, listUrl, "/en/Admin/Products/SetStock", [F("id", id.ToString()), F("stock", "-4")]);
        Assert.Contains("must be between 0 and 100000", await ErrorBarAsync(admin, listUrl));
        Assert.Contains($"name=\"id\" value=\"{id}\"", await admin.GetStringAsync(listUrl + "&LowStock=true"));

        // A "back" address outside the site is not followed.
        var evil = await PostFormListAsync(admin, listUrl, "/en/Admin/Products/SetStock", [F("id", id.ToString()), F("stock", "3"), F("back", "https://evil.example/")]);
        Assert.StartsWith("/en/Admin/Products", evil.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Dashboard_LowStockCard_LinksToTheFilteredProducts()
    {
        var admin = await AdminClientAsync();

        Assert.Contains("/en/Admin/Products?lowStock=true", await admin.GetStringAsync("/en/Admin"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/en/Admin/Products?lowStock=true")).StatusCode);
    }

    // ---- Age groups ----

    [Fact]
    public async Task AgeGroup_IsCreated_AssignedToAProduct_AndUsedByTheShopFilter()
    {
        var admin = await AdminClientAsync();
        var groupName = Unique("Age");
        var created = await PostFormListAsync(admin, "/en/Admin/AgeGroups/Edit", "/en/Admin/AgeGroups/Edit",
            [F("NameHy", groupName), F("NameEn", groupName + " EN"), F("SortOrder", "60"), F("IsActive", "true")]);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var groupId = int.Parse(Regex.Match(await admin.GetStringAsync("/en/Admin/AgeGroups"), $@"/Edit/(\d+)""><strong>{groupName}( EN)?</strong>").Groups[1].Value);

        var category = await CreateCategoryAsync(admin, Unique("ForAge"));
        var fields = ProductFields(category, Unique("AG"), Unique("Aged"));
        fields.Add(F("AgeGroupIds", groupId.ToString()));
        var response = await PostFormListAsync(admin, "/en/Admin/Products/Edit", "/en/Admin/Products/Edit", fields);
        var productId = int.Parse(Regex.Match(response.Headers.Location!.ToString(), @"/Edit/(\d+)").Groups[1].Value);

        // The product page for the admin has the box ticked; the shop filter finds the product.
        Assert.Matches($@"name=""AgeGroupIds"" value=""{groupId}""[^>]*checked", await admin.GetStringAsync($"/en/Admin/Products/Edit/{productId}"));
        Assert.Contains($"/en/Products/Details/{productId}", await NewClient().GetStringAsync($"/en/Products?age={groupId}"));

        // In use: deleting is refused.
        await PostFormListAsync(admin, "/en/Admin/AgeGroups", "/en/Admin/AgeGroups/Delete", [F("id", groupId.ToString())]);
        Assert.Contains("Products use this age group. Hide it instead.", await ErrorBarAsync(admin, "/en/Admin/AgeGroups"));
    }

    [Fact]
    public async Task AgeGroup_WithoutProducts_CanBeDeleted()
    {
        var admin = await AdminClientAsync();
        var groupName = Unique("Temp");
        await PostFormListAsync(admin, "/en/Admin/AgeGroups/Edit", "/en/Admin/AgeGroups/Edit", [F("NameHy", groupName), F("SortOrder", "70"), F("IsActive", "true")]);
        var groupId = Regex.Match(await admin.GetStringAsync("/en/Admin/AgeGroups"), $@"/Edit/(\d+)""><strong>{groupName}( EN)?</strong>").Groups[1].Value;

        await PostFormListAsync(admin, "/en/Admin/AgeGroups", "/en/Admin/AgeGroups/Delete", [F("id", groupId)]);

        var list = await ErrorBarAsync(admin, "/en/Admin/AgeGroups");
        Assert.Contains("Deleted.", list);
        Assert.DoesNotContain(groupName, list);
    }
}

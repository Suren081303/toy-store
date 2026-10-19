using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Core.Admin;
using Shop.Infrastructure.Data;

namespace Shop.Tests.Web;

// The catalog tools of the admin as a browser uses them: tick boxes and bulk change, CSV export and import, duplicate with photos.
[Collection("Web")]
public class CatalogToolsFlowTests : CheckoutTestBase
{
    public CatalogToolsFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    private static KeyValuePair<string, string> F(string key, string value) => new(key, value);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static string Decoded(string html) => WebUtility.HtmlDecode(html);

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, ShopWebFactory.AdminEmail, ShopWebFactory.AdminPassword)).StatusCode);
        return client;
    }

    private async Task<T> InDbAsync<T>(Func<ShopDbContext, Task<T>> action)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ShopDbContext>());
    }

    private async Task<int> CreateCategoryAsync(HttpClient admin)
    {
        var name = Unique("Cat");
        var response = await PostMultipartAsync(admin, "/en/Admin/Categories/Edit", "/en/Admin/Categories/Edit",
            [F("NameHy", name), F("NameEn", name + " EN"), F("SortOrder", "50"), F("IsActive", "true")]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var list = await admin.GetStringAsync("/en/Admin/Categories");
        var match = Regex.Match(list, $@"href=""/en/Admin/Categories/Edit/(\d+)""><strong>{Regex.Escape(name)}( EN)?</strong>");
        Assert.True(match.Success, "The new category is not in the list.");
        return int.Parse(match.Groups[1].Value);
    }

    private async Task<(int Id, string Sku)> CreateProductAsync(HttpClient admin, int categoryId, int stock = 10, string? sku = null, decimal price = 12000)
    {
        sku ??= Unique("SKU");
        var fields = new List<KeyValuePair<string, string>>
        {
            F("Sku", sku), F("Brand", "TestBrand"), F("CategoryId", categoryId.ToString()), F("Price", price.ToString("0")),
            F("DiscountPercent", "0"), F("StockQuantity", stock.ToString()), F("NameHy", Unique("Toy")), F("NameEn", "Toy EN"),
            F("DescriptionHy", "Description"), F("IsPinnedAsNew", "false"), F("IsArchived", "false")
        };

        var response = await PostFormListAsync(admin, "/en/Admin/Products/Edit", "/en/Admin/Products/Edit", fields);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var match = Regex.Match(response.Headers.Location!.ToString(), @"/en/Admin/Products/Edit/(\d+)");
        Assert.True(match.Success, "Unexpected redirect: " + response.Headers.Location);
        return (int.Parse(match.Groups[1].Value), sku);
    }

    private static string ListUrl(int categoryId) => $"/en/Admin/Products?CategoryId={categoryId}";

    private Task<HttpResponseMessage> BulkAsync(HttpClient admin, string action, int categoryId, IEnumerable<KeyValuePair<string, string>> fields) =>
        PostFormListAsync(admin, ListUrl(categoryId), $"/en/Admin/Products/{action}", fields);

    private static List<KeyValuePair<string, string>> BulkFields(string actionName, string? value, IEnumerable<int> ids, string back, string scope = "selected")
    {
        var fields = new List<KeyValuePair<string, string>> { F("Scope", scope), F("Action", actionName), F("Back", back) };
        if (value is not null)
        {
            fields.Add(F("Value", value));
        }

        fields.AddRange(ids.Select(id => F("Ids", id.ToString())));
        return fields;
    }

    private Task<HttpResponseMessage> UploadCsvAsync(HttpClient admin, string text, string name = "products.csv") =>
        PostMultipartAsync(admin, "/en/Admin/Products/Import", "/en/Admin/Products/ImportPreview", [], ("file", name, Encoding.UTF8.GetBytes(text), "text/csv"));

    // The file as the preview page sends it back to be applied.
    private static string CsvOfPreview(string html)
    {
        var match = Regex.Match(html, "<textarea name=\"csv\" hidden>(.*?)</textarea>", RegexOptions.Singleline);
        Assert.True(match.Success, "The preview page has no Apply form.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private const string Header = "Sku,Brand,CategoryId,Price,DiscountPercent,Stock,Archived,NewArrival,NameHy,NameRu,NameEn,DescriptionHy,DescriptionRu,DescriptionEn,AgeGroupIds";

    // ---- Who may use it ----

    [Fact]
    public async Task VisitorsAndCustomers_AreRefused_OnEveryPageAndAction()
    {
        var customer = NewClient();
        await SignedInUserAsync(customer);

        foreach (var url in new[] { "/en/Admin/Products/Import", "/en/Admin/Products/ImportTemplate" })
        {
            Assert.Contains("/Account/Login", (await NewClient().GetAsync(url)).Headers.Location!.ToString());
            Assert.Contains("/Account/AccessDenied", (await customer.GetAsync(url)).Headers.Location!.ToString());
        }

        foreach (var action in new[] { "BulkPreview", "BulkApply", "ExportCsv", "ImportApply", "Duplicate" })
        {
            var response = await PostFormListAsync(customer, "/en/Orders/Lookup", "/en/Admin/Products/" + action, [F("Ids", "1"), F("Action", "SetStock"), F("Value", "1"), F("id", "1"), F("sku", "X")]);
            Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());
        }

        var upload = await PostMultipartAsync(customer, "/en/Orders/Lookup", "/en/Admin/Products/ImportPreview", [], ("file", "p.csv", Encoding.UTF8.GetBytes(Header), "text/csv"));
        Assert.Contains("/Account/AccessDenied", upload.Headers.Location!.ToString());
    }

    // ---- The list ----

    [Fact]
    public async Task TheList_HasTickBoxes_TheBulkBar_AndTheImportExportButtons()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin);
        var (id, _) = await CreateProductAsync(admin, category);

        var html = await admin.GetStringAsync(ListUrl(category));

        Assert.Contains("id=\"bulk-form\"", html);
        Assert.Contains("action=\"/en/Admin/Products/BulkPreview\"", html);
        Assert.Contains($"name=\"ids\" value=\"{id}\" form=\"bulk-form\"", html);   // the box belongs to the bulk form, not to the stock forms in the rows
        Assert.Contains("data-select-all", html);
        Assert.Contains("href=\"/en/Admin/Products/Import\"", html);
        Assert.Contains("action=\"/en/Admin/Products/ExportCsv\"", html);
        Assert.DoesNotContain("value=\"filter\"", html);                              // one page only: no "all that match" choice
        foreach (var action in Enum.GetValues<BulkAction>())
        {
            Assert.Contains($"<option value=\"{action}\">", html);
        }

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/js/admin.js")).StatusCode);
        Assert.Contains("data-select-all", await admin.GetStringAsync("/js/admin.js"));
    }

    [Fact]
    public async Task WhenThereAreSeveralPages_TheBarOffersAllProductsThatMatchTheFilter()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin);
        for (var i = 0; i < 22; i++)
        {
            await CreateProductAsync(admin, category);
        }

        var html = Decoded(await admin.GetStringAsync(ListUrl(category)));

        Assert.Contains("value=\"filter\"", html);
        Assert.Contains("All 22 products that match the filter", html);
    }

    // ---- Change many ----

    [Fact]
    public async Task Bulk_PreviewThenApply_ChangesTheTickedProducts_AndComesBackToTheList()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin);
        var (a, skuA) = await CreateProductAsync(admin, category);
        var (b, skuB) = await CreateProductAsync(admin, category);
        var (c, _) = await CreateProductAsync(admin, category);
        var back = ListUrl(category);

        var preview = await BulkAsync(admin, "BulkPreview", category, BulkFields("SetDiscount", "25", [a, b], back));
        var html = Decoded(await BodyAsync(preview));

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Contains(skuA, html);
        Assert.Contains(skuB, html);
        Assert.Contains("Nothing is saved yet. 2 products will change; 0 are already like that.", html);
        Assert.Contains("Apply to 2 products", html);
        Assert.Equal(0, await InDbAsync(db => db.Products.Where(p => p.Id == a).Select(p => p.DiscountPercent).SingleAsync())); // the preview saved nothing

        // The page sends back exactly the ids it showed.
        var applyFields = BulkFields("SetDiscount", "25", Regex.Matches(html, "name=\"Ids\" value=\"(\\d+)\"").Select(m => int.Parse(m.Groups[1].Value)), back);
        Assert.Equal([a, b], applyFields.Where(f => f.Key == "Ids").Select(f => int.Parse(f.Value)).Order());

        var apply = await BulkAsync(admin, "BulkApply", category, applyFields);

        Assert.Equal(HttpStatusCode.Redirect, apply.StatusCode);
        Assert.Equal(back, apply.Headers.Location!.OriginalString);
        Assert.Contains("2 products changed (0 were already like that).", Decoded(await admin.GetStringAsync(back)));
        Assert.Equal(25, await InDbAsync(db => db.Products.Where(p => p.Id == a).Select(p => p.DiscountPercent).SingleAsync()));
        Assert.Equal(25, await InDbAsync(db => db.Products.Where(p => p.Id == b).Select(p => p.DiscountPercent).SingleAsync()));
        Assert.Equal(0, await InDbAsync(db => db.Products.Where(p => p.Id == c).Select(p => p.DiscountPercent).SingleAsync())); // not ticked

        var audit = await admin.GetStringAsync("/en/Admin/Audit?Query=Product.Bulk");
        Assert.Contains("SetDiscount: 0 -&gt; 25", audit);
    }

    [Fact]
    public async Task Bulk_AllProductsOfAFilter_AreChangedEvenOnOtherPages()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin);
        var ids = new List<int>();
        for (var i = 0; i < 23; i++)
        {
            ids.Add((await CreateProductAsync(admin, category, stock: 5)).Id);
        }

        var back = ListUrl(category);
        var fields = BulkFields("SetStock", "99", [], back, scope: "filter");
        fields.Add(F("CategoryId", category.ToString()));
        fields.Add(F("Show", "Active"));

        var preview = await BulkAsync(admin, "BulkPreview", category, fields);
        var html = Decoded(await BodyAsync(preview));
        Assert.Contains("23 products will change", html);

        var shown = Regex.Matches(html, "name=\"Ids\" value=\"(\\d+)\"").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(ids.Order(), shown.Order());

        await BulkAsync(admin, "BulkApply", category, BulkFields("SetStock", "99", shown, back));

        Assert.Equal(23, await InDbAsync(db => db.Products.CountAsync(p => ids.Contains(p.Id) && p.StockQuantity == 99)));
    }

    [Fact]
    public async Task Bulk_AWrongValue_NothingTicked_APriceProblem_AndNoChange_AreAllSaidClearly()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin);
        var (cheap, _) = await CreateProductAsync(admin, category, price: 4);
        var (normal, _) = await CreateProductAsync(admin, category);
        var back = ListUrl(category);

        // A value that is not allowed: back to the list with a red bar, nothing saved.
        await BulkAsync(admin, "BulkPreview", category, BulkFields("SetDiscount", "150", [normal], back));
        Assert.Contains("Enter a valid value for this change.", Decoded(await admin.GetStringAsync(back)));

        // Nothing ticked.
        await BulkAsync(admin, "BulkPreview", category, BulkFields("SetDiscount", "10", [], back));
        Assert.Contains("Tick at least one product first.", Decoded(await admin.GetStringAsync(back)));

        // Move without a category.
        await BulkAsync(admin, "BulkPreview", category, BulkFields("MoveCategory", null, [normal], back));
        Assert.Contains("Choose the category to move to.", Decoded(await admin.GetStringAsync(back)));

        // A price that would fall below 1: the preview shows the problem and offers no Apply button.
        var problem = await BulkAsync(admin, "BulkPreview", category, BulkFields("ChangePricePercent", "-90", [cheap, normal], back));
        var html = Decoded(await BodyAsync(problem));
        Assert.Contains("The price would be below 1", html);
        Assert.Contains("Some products have a problem, so nothing can be applied.", html);
        Assert.DoesNotContain("Apply to", html);

        // Even a hand-made apply of that is refused, and nothing is saved.
        await BulkAsync(admin, "BulkApply", category, BulkFields("ChangePricePercent", "-90", [cheap, normal], back));
        Assert.Contains("Some products have a problem, so nothing can be applied.", Decoded(await admin.GetStringAsync(back)));
        Assert.Equal(12000m, await InDbAsync(db => db.Products.Where(p => p.Id == normal).Select(p => p.Price).SingleAsync()));

        // Nothing would change.
        var same = await BulkAsync(admin, "BulkPreview", category, BulkFields("SetDiscount", "0", [normal], back));
        var sameHtml = Decoded(await BodyAsync(same));
        Assert.Contains("Nothing would change.", sameHtml);
        Assert.DoesNotContain("Apply to", sameHtml);
    }

    [Fact]
    public async Task Bulk_ShowsHideShowAndMoveAsWordsAndNames_AndIgnoresAnOutsideBackAddress()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin);
        var target = await CreateCategoryAsync(admin);
        var (id, _) = await CreateProductAsync(admin, category);

        var archive = Decoded(await BodyAsync(await BulkAsync(admin, "BulkPreview", category, BulkFields("Archive", null, [id], ListUrl(category)))));
        Assert.Contains("Shown → <strong>Archived</strong>", archive);

        var move = Decoded(await BodyAsync(await BulkAsync(admin, "BulkPreview", category, [.. BulkFields("MoveCategory", null, [id], ListUrl(category)), F("TargetCategoryId", target.ToString())])));
        Assert.Contains(" EN", move.Replace("→", " ")); // the category names are shown

        var outside = await BulkAsync(admin, "BulkApply", category, BulkFields("Archive", null, [id], "https://evil.example/steal"));
        Assert.Equal(HttpStatusCode.Redirect, outside.StatusCode);
        Assert.Equal("/en/Admin/Products", outside.Headers.Location!.OriginalString);
        Assert.True(await InDbAsync(db => db.Products.Where(p => p.Id == id).Select(p => p.IsArchived).SingleAsync()));
    }

    // ---- Export ----

    [Fact]
    public async Task Export_IsAPostDownload_WithTheHeaderAndTheProducts_AndIsAudited()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin);
        var (_, sku) = await CreateProductAsync(admin, category, stock: 17, price: 8900);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/en/Admin/Products/ExportCsv")).StatusCode); // only a POST exports: a link or a prefetch cannot trigger it

        var response = await PostFormListAsync(admin, "/en/Admin/Products", "/en/Admin/Products/ExportCsv", []);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Matches(@"products-\d{4}-\d{2}-\d{2}\.csv", response.Content.Headers.ContentDisposition!.FileName!);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var rows = CsvReader.Parse(Encoding.UTF8.GetString(bytes)).Select(r => r.ToArray()).ToList();
        Assert.Equal(ProductCsv.Columns, rows[0]);
        var row = rows.Single(r => r[0] == sku);
        Assert.Equal(("TestBrand", category.ToString(), "8900", "17"), (row[1], row[2], row[3], row[5]));

        Assert.Contains("Export.Products", await admin.GetStringAsync("/en/Admin/Audit?Query=Export.Products"));
    }

    [Fact]
    public async Task TheTemplate_IsACsvWithTheHeaderAndTwoExampleRows()
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync("/en/Admin/Products/ImportTemplate");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("products-template.csv", response.Content.Headers.ContentDisposition!.FileName);
        var rows = CsvReader.Parse(Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal(ProductCsv.Columns, rows[0]);
        Assert.Equal(3, rows.Count);
        Assert.Equal("NEW-001", rows[1][0]);
    }

    // ---- Import ----

    [Fact]
    public async Task Import_ShowsTheRules_AndTheCategoryAndAgeGroupNumbers()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin);

        var html = Decoded(await admin.GetStringAsync("/en/Admin/Products/Import"));

        Assert.Contains("The SKU is the key", html);
        Assert.Contains("At most 1000 rows and 2 MB.", html);
        Assert.Contains("Photos are not imported", html);
        Assert.Contains("href=\"/en/Admin/Products/ImportTemplate\"", html);
        Assert.Contains($"<strong>{category}</strong>", html);   // the number to put in the CategoryId column
        Assert.Contains("type=\"file\"", html);
        Assert.Contains("enctype=\"multipart/form-data\"", html);
    }

    [Fact]
    public async Task Import_CheckThenApply_CreatesAndUpdatesProducts()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin);
        var (id, existingSku) = await CreateProductAsync(admin, category, price: 1000);
        var newSku = Unique("NEW-");
        var csv = $"{Header}\r\n{newSku},ToyLand,{category},12500,15,30,,yes,Նոր ապրանք,Новый товар,New toy,Նկարագրություն,,,\r\n{existingSku},,,1300,,,,,,,,,,,\r\n";

        var preview = await UploadCsvAsync(admin, csv);
        var html = Decoded(await BodyAsync(preview));

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Contains("1 to create, 1 to update, 0 unchanged.", html);
        Assert.Contains("No problems found. Nothing is saved until you press Apply.", html);
        Assert.Contains("Price 1000 -> 1300", html);
        Assert.Null(await InDbAsync(db => db.Products.Where(p => p.Sku == newSku).Select(p => (int?)p.Id).FirstOrDefaultAsync())); // nothing yet

        // The page hands the file back as it is.
        var sent = CsvOfPreview(await BodyAsync(preview));
        Assert.Equal(csv, sent);

        var apply = await PostFormListAsync(admin, "/en/Admin/Products/Import", "/en/Admin/Products/ImportApply", [F("csv", sent)]);

        Assert.Equal(HttpStatusCode.Redirect, apply.StatusCode);
        Assert.Contains("/en/Admin/Products", apply.Headers.Location!.ToString());
        Assert.Contains("Import done: 1 created, 1 updated, 0 unchanged.", Decoded(await admin.GetStringAsync("/en/Admin/Products")));

        var created = await InDbAsync(db => db.Products.Include(p => p.Translations).SingleAsync(p => p.Sku == newSku));
        Assert.True(created.IsArchived);            // hidden until checked
        Assert.True(created.IsPinnedAsNew);
        Assert.Equal(3, created.Translations.Count);
        Assert.Equal(1300m, await InDbAsync(db => db.Products.Where(p => p.Id == id).Select(p => p.Price).SingleAsync()));
        Assert.Contains("Import.Products", await admin.GetStringAsync("/en/Admin/Audit?Query=Import.Products"));
    }

    [Fact]
    public async Task Import_AFileWithProblems_ShowsThemByRow_OffersNoApply_AndAHandMadeApplySavesNothing()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin);
        var goodSku = Unique("GOOD-");
        var csv = $"{Header}\r\n{goodSku},Br,{category},500,,,,,Name,,,,,,\r\nBAD-1,Br,{category},abc,,,,,Name,,,,,,\r\nBAD-2,Br,987654,500,,,,,Name,,,,,,\r\n,Br,{category},500,,,,,Name,,,,,,\r\n";

        var preview = await UploadCsvAsync(admin, csv);
        var html = Decoded(await BodyAsync(preview));

        Assert.Contains("3 rows have problems. Nothing can be applied", html);
        Assert.Contains("Price: \"abc\" is not a valid value.", html);
        Assert.Contains("CategoryId: 987654 does not exist.", html);
        Assert.Contains("The Sku is empty.", html);
        Assert.DoesNotContain("Apply the import", html);
        Assert.DoesNotContain("<textarea", html);

        var apply = await PostFormListAsync(admin, "/en/Admin/Products/Import", "/en/Admin/Products/ImportApply", [F("csv", csv)]);
        Assert.Equal(HttpStatusCode.Redirect, apply.StatusCode);
        Assert.Contains("/Products/Import", apply.Headers.Location!.ToString());
        Assert.Contains("The file has problems, so nothing was saved.", Decoded(await admin.GetStringAsync("/en/Admin/Products/Import")));
        Assert.Null(await InDbAsync(db => db.Products.Where(p => p.Sku == goodSku).Select(p => (int?)p.Id).FirstOrDefaultAsync()));
    }

    [Fact]
    public async Task Import_NoFile_ATooBigFile_AFileWithFileProblems_AndNotACsvAtAll_AreAllHandled()
    {
        var admin = await AdminClientAsync();

        // No file chosen.
        var none = await PostMultipartAsync(admin, "/en/Admin/Products/Import", "/en/Admin/Products/ImportPreview", []);
        Assert.Equal(HttpStatusCode.Redirect, none.StatusCode);
        Assert.Contains("Choose a CSV file.", Decoded(await admin.GetStringAsync("/en/Admin/Products/Import")));

        // More than 2 MB.
        var big = await PostMultipartAsync(admin, "/en/Admin/Products/Import", "/en/Admin/Products/ImportPreview", [], ("file", "big.csv", new byte[ProductCsv.MaxFileBytes + 10], "text/csv"));
        Assert.Equal(HttpStatusCode.Redirect, big.StatusCode);
        Assert.Contains("The file is too big (up to 2 MB).", Decoded(await admin.GetStringAsync("/en/Admin/Products/Import")));

        // Problems with the file itself.
        var unknown = Decoded(await BodyAsync(await UploadCsvAsync(admin, "Sku,Colour\r\nA,red\r\n")));
        Assert.Contains("Unknown column: Colour.", unknown);
        var noSku = Decoded(await BodyAsync(await UploadCsvAsync(admin, "Brand,Price\r\nB,1\r\n")));
        Assert.Contains("The header needs a Sku column.", noSku);
        var emptyFile = await PostMultipartAsync(admin, "/en/Admin/Products/Import", "/en/Admin/Products/ImportPreview", [], ("file", "e.csv", [(byte)' '], "text/csv"));
        Assert.Contains("The file is empty", Decoded(await BodyAsync(emptyFile)));

        // A picture sent by mistake: problems are shown, nothing breaks.
        var picture = await PostMultipartAsync(admin, "/en/Admin/Products/Import", "/en/Admin/Products/ImportPreview", [], ("file", "a.csv", TestImages.Jpeg(), "text/csv"));
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.DoesNotContain("Apply the import", await BodyAsync(picture));
    }

    [Fact]
    public async Task Import_AFileFromASpreadsheet_WithSemicolonsAndABom_Works()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin);
        var sku = Unique("SEMI-");
        var csv = $"﻿Sku;Brand;CategoryId;Price;NameHy\r\n{sku};Brand;{category};2500;\"Name; with semicolon\"\r\n";

        var preview = Decoded(await BodyAsync(await UploadCsvAsync(admin, csv)));

        Assert.Contains("1 to create, 0 to update, 0 unchanged.", preview);
        await PostFormListAsync(admin, "/en/Admin/Products/Import", "/en/Admin/Products/ImportApply", [F("csv", csv)]);
        var name = await InDbAsync(db => db.ProductTranslations.Where(t => t.Product.Sku == sku && t.Culture == "hy").Select(t => t.Name).SingleAsync());
        Assert.Equal("Name; with semicolon", name);
    }

    // ---- Duplicate ----

    [Fact]
    public async Task Duplicate_CopiesThePhotosAsNewFiles_StaysHidden_AndDeletingACopyPhotoKeepsTheOriginals()
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin);
        var (id, _) = await CreateProductAsync(admin, category, stock: 12);
        Assert.Equal(HttpStatusCode.Redirect, (await PostMultipartAsync(admin, $"/en/Admin/Products/Edit/{id}", "/en/Admin/Products/UploadPhotos", [F("id", id.ToString())],
            ("photos", "a.jpg", TestImages.Jpeg(900, 700), "image/jpeg"), ("photos", "b.jpg", TestImages.Jpeg(800, 800), "image/jpeg"))).StatusCode);

        var editPage = await admin.GetStringAsync($"/en/Admin/Products/Edit/{id}");
        Assert.Contains("Duplicate this product", editPage);
        Assert.Contains("action=\"/en/Admin/Products/Duplicate\"", editPage);
        var originalPhotos = Regex.Matches(editPage, @"src=""(/uploads/products/\d+/[0-9a-f]{32}_t\.jpg)""").Select(m => m.Groups[1].Value.Replace("_t.jpg", ".jpg")).ToList();
        Assert.Equal(2, originalPhotos.Count);

        var newSku = Unique("COPY-");
        var duplicate = await PostFormListAsync(admin, $"/en/Admin/Products/Edit/{id}", "/en/Admin/Products/Duplicate", [F("id", id.ToString()), F("sku", newSku)]);

        Assert.Equal(HttpStatusCode.Redirect, duplicate.StatusCode);
        var match = Regex.Match(duplicate.Headers.Location!.ToString(), @"/en/Admin/Products/Edit/(\d+)");
        Assert.True(match.Success);
        var copyId = int.Parse(match.Groups[1].Value);
        Assert.NotEqual(id, copyId);

        var copyPage = await admin.GetStringAsync($"/en/Admin/Products/Edit/{copyId}");
        Assert.Contains("Copy created (hidden, stock 0). Photos copied: 2 of 2.", Decoded(copyPage));
        Assert.Contains($"value=\"{newSku}\"", copyPage);
        var copyPhotos = Regex.Matches(copyPage, @"src=""(/uploads/products/\d+/[0-9a-f]{32}_t\.jpg)""").Select(m => m.Groups[1].Value.Replace("_t.jpg", ".jpg")).ToList();
        Assert.Equal(2, copyPhotos.Count);
        Assert.Empty(copyPhotos.Intersect(originalPhotos));                               // new files, not shared ones
        Assert.All(copyPhotos.Concat(originalPhotos), p => Assert.True(File.Exists(Fixture.Factory.FileOf(p)), p));
        Assert.StartsWith($"/uploads/products/{copyId}/", copyPhotos[0]);

        var stored = await InDbAsync(db => db.Products.AsNoTracking().SingleAsync(p => p.Id == copyId));
        Assert.True(stored.IsArchived);
        Assert.Equal(0, stored.StockQuantity);
        Assert.Equal(12, await InDbAsync(db => db.Products.Where(p => p.Id == id).Select(p => p.StockQuantity).SingleAsync()));

        // A hidden copy is not in the shop.
        Assert.Equal(HttpStatusCode.NotFound, (await NewClient().GetAsync($"/en/Products/Details/{copyId}")).StatusCode);

        // Deleting a photo of the copy leaves the original's files alone.
        var imageId = await InDbAsync(db => db.ProductImages.Where(i => i.ProductId == copyId).OrderBy(i => i.SortOrder).Select(i => i.Id).FirstAsync());
        await PostFormListAsync(admin, $"/en/Admin/Products/Edit/{copyId}", "/en/Admin/Products/RemovePhoto", [F("id", copyId.ToString()), F("imageId", imageId.ToString())]);
        Assert.False(File.Exists(Fixture.Factory.FileOf(copyPhotos[0])));
        Assert.All(originalPhotos, p => Assert.True(File.Exists(Fixture.Factory.FileOf(p)), p));
        Assert.Contains("value=\"" + newSku + "\"", await admin.GetStringAsync($"/en/Admin/Products/Edit/{copyId}"));
    }

    [Fact]
    public async Task Duplicate_AProductWithAPlaceholderPicture_SharesIt_AndABadOrTakenSkuIsSaidClearly()
    {
        var admin = await AdminClientAsync();
        var demoId = await PlainProductAsync();
        var demoPage = await admin.GetStringAsync($"/en/Admin/Products/Edit/{demoId}");
        var placeholder = Regex.Match(demoPage, @"src=""(/images/placeholders/[^""]+)""");
        var newSku = Unique("DEMOCOPY-");

        var duplicate = await PostFormListAsync(admin, $"/en/Admin/Products/Edit/{demoId}", "/en/Admin/Products/Duplicate", [F("id", demoId.ToString()), F("sku", newSku)]);
        var copyId = int.Parse(Regex.Match(duplicate.Headers.Location!.ToString(), @"/Edit/(\d+)").Groups[1].Value);
        var copyPage = await admin.GetStringAsync($"/en/Admin/Products/Edit/{copyId}");

        if (placeholder.Success)
        {
            Assert.Contains(placeholder.Groups[1].Value, copyPage);   // the same placeholder, shared
        }

        // Empty, too long, and taken SKUs.
        var editUrl = $"/en/Admin/Products/Edit/{demoId}";
        await PostFormListAsync(admin, editUrl, "/en/Admin/Products/Duplicate", [F("id", demoId.ToString()), F("sku", "   ")]);
        Assert.Contains("Enter a new SKU (up to 50 characters).", Decoded(await admin.GetStringAsync(editUrl)));
        await PostFormListAsync(admin, editUrl, "/en/Admin/Products/Duplicate", [F("id", demoId.ToString()), F("sku", new string('x', 51))]);
        Assert.Contains("Enter a new SKU (up to 50 characters).", Decoded(await admin.GetStringAsync(editUrl)));
        await PostFormListAsync(admin, editUrl, "/en/Admin/Products/Duplicate", [F("id", demoId.ToString()), F("sku", newSku)]);
        Assert.Contains("This SKU is already used by another product.", Decoded(await admin.GetStringAsync(editUrl)));

        Assert.Equal(HttpStatusCode.NotFound, (await PostFormListAsync(admin, editUrl, "/en/Admin/Products/Duplicate", [F("id", "99999999"), F("sku", Unique("X"))])).StatusCode);

        // The new-product page has no duplicate box.
        Assert.DoesNotContain("Duplicate this product", await admin.GetStringAsync("/en/Admin/Products/Edit"));
    }

    // ---- Languages ----

    [Theory]
    [InlineData("hy")]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task TheCatalogToolPages_OpenInEveryLanguage_WithNoUntranslatedKeys(string culture)
    {
        var admin = await AdminClientAsync();
        var category = await CreateCategoryAsync(admin);
        var (id, sku) = await CreateProductAsync(admin, category, price: 4);
        var back = $"/{culture}/Admin/Products?CategoryId={category}";

        var pages = new List<string>
        {
            await admin.GetStringAsync(back),
            await admin.GetStringAsync($"/{culture}/Admin/Products/Import"),
            await admin.GetStringAsync($"/{culture}/Admin/Products/Edit/{id}"),
            await BodyAsync(await PostFormListAsync(admin, back, $"/{culture}/Admin/Products/BulkPreview", BulkFields("ChangePricePercent", "-90", [id], back))),
            await BodyAsync(await PostFormListAsync(admin, back, $"/{culture}/Admin/Products/BulkPreview", BulkFields("SetDiscount", "10", [id], back))),
            await BodyAsync(await PostMultipartAsync(admin, $"/{culture}/Admin/Products/Import", $"/{culture}/Admin/Products/ImportPreview", [], ("file", "p.csv", Encoding.UTF8.GetBytes($"{Header}\r\n{sku},,,5,,,,,,,,,,,\r\nX1,Br,{category},abc,,,,,N,,,,,,\r\nX2,Br,{category},5,,,,,N,,,,,,\r\n"), "text/csv")))
        };

        foreach (var html in pages)
        {
            var text = Regex.Replace(Regex.Replace(html, "<script[\\s\\S]*?</script>", " "), "<textarea[\\s\\S]*?</textarea>", " ");
            text = Regex.Replace(text, "<[^>]+>", " ");
            Assert.Empty(Regex.Matches(text, @"\b(Admin|Val|Status|Pay|Label)_[A-Za-z_]+\b").Select(m => m.Value).Distinct());
        }
    }
}

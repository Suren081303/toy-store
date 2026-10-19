using System.Text;
using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Entities;

namespace Shop.Tests;

// Duplicate, change many, CSV export and import of the catalog, on the real database.
[Collection("Orders")]
public class AdminProductToolsTests
{
    private const string Header = "Sku,Brand,CategoryId,Price,DiscountPercent,Stock,Archived,NewArrival,NameHy,NameRu,NameEn,DescriptionHy,DescriptionRu,DescriptionEn,AgeGroupIds";

    private readonly OrderFixture _fx;

    public AdminProductToolsTests(OrderFixture fx)
    {
        _fx = fx;
    }

    private static string Token() => Guid.NewGuid().ToString("N")[..8];

    private Task<string> NewAdminAsync() => _fx.CreateUserAsync();

    private async Task<int> NewCategoryAsync(string? name = null)
    {
        var form = new AdminCategoryForm { NameHy = name ?? "Cat " + Token(), NameEn = "Cat EN", SortOrder = 1, IsActive = true };
        return (await _fx.CreateCatalogAdminServices().Categories.SaveAsync(form, await NewAdminAsync())).Id!.Value;
    }

    private async Task<int> NewAgeGroupAsync()
    {
        var form = new AdminAgeGroupForm { NameHy = "Age " + Token(), SortOrder = 1, IsActive = true };
        return (await _fx.CreateCatalogAdminServices().AgeGroups.SaveAsync(form, await NewAdminAsync()))!.Value;
    }

    // A product made the way the admin form makes it.
    private async Task<(int Id, string Sku)> NewProductAsync(
        int categoryId, decimal price = 10000, int discount = 0, int stock = 10, bool archived = false, string? sku = null,
        string? nameHy = null, List<int>? ages = null, bool pinned = false)
    {
        sku ??= "T" + Token();
        var form = new AdminProductForm
        {
            Sku = sku,
            Brand = "Brand " + sku,
            CategoryId = categoryId,
            Price = price,
            DiscountPercent = discount,
            StockQuantity = stock,
            NameHy = nameHy ?? "Անուն " + sku,
            NameRu = "Имя " + sku,
            NameEn = "Name " + sku,
            DescriptionHy = "Նկարագրություն " + sku,
            DescriptionRu = "Описание " + sku,
            DescriptionEn = "Description " + sku,
            AgeGroupIds = ages ?? [],
            IsPinnedAsNew = pinned,
            IsArchived = archived
        };

        var result = await _fx.CreateCatalogAdminServices().Products.SaveAsync(form, await NewAdminAsync());
        Assert.Equal(SaveProductStatus.Saved, result.Status);
        return (result.Id!.Value, sku);
    }

    private async Task<Product> LoadAsync(int id)
    {
        using var db = _fx.CreateDbContext();
        return await db.Products.AsNoTracking().Include(p => p.Translations).Include(p => p.AgeGroups).SingleAsync(p => p.Id == id);
    }

    private async Task<Product?> LoadBySkuAsync(string sku)
    {
        using var db = _fx.CreateDbContext();
        return await db.Products.AsNoTracking().Include(p => p.Translations).Include(p => p.AgeGroups).FirstOrDefaultAsync(p => p.Sku == sku);
    }

    private async Task<List<AuditLog>> AuditAsync(string action, string? entityId = null)
    {
        using var db = _fx.CreateDbContext();
        return await db.AuditLogs.AsNoTracking().Where(a => a.Action == action && (entityId == null || a.EntityId == entityId)).ToListAsync();
    }

    // ================= Duplicate =================

    [Fact]
    public async Task Duplicate_CopiesTheDetails_ButIsHiddenWithNoStock_AndIsAudited()
    {
        var category = await NewCategoryAsync();
        var age1 = await NewAgeGroupAsync();
        var age2 = await NewAgeGroupAsync();
        var (id, sku) = await NewProductAsync(category, price: 12345, discount: 15, stock: 7, ages: [age1, age2], pinned: true);
        var admin = await NewAdminAsync();
        var newSku = "COPY-" + Token();

        var result = await _fx.CreateProductToolsService().DuplicateAsync(id, "  " + newSku + "  ", admin); // spaces do not matter

        Assert.Equal(DuplicateProductStatus.Created, result.Status);
        var original = await LoadAsync(id);
        var copy = await LoadAsync(result.NewId!.Value);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(newSku, copy.Sku);
        Assert.Equal(original.Brand, copy.Brand);
        Assert.Equal(category, copy.CategoryId);
        Assert.Equal(12345m, copy.Price);
        Assert.Equal(15, copy.DiscountPercent);
        Assert.Equal(0, copy.StockQuantity);   // nothing on the shelf yet
        Assert.True(copy.IsArchived);          // hidden until checked
        Assert.False(copy.IsPinnedAsNew);
        Assert.Equal(OrderFixture.Now, copy.CreatedAt);
        Assert.Equal([age1, age2], copy.AgeGroups.Select(a => a.Id).OrderBy(i => i));

        foreach (var culture in new[] { "hy", "ru", "en" })
        {
            var from = original.Translations.Single(t => t.Culture == culture);
            var to = copy.Translations.Single(t => t.Culture == culture);
            Assert.Equal(from.Name, to.Name);
            Assert.Equal(from.Description, to.Description);
        }

        // The original is untouched.
        Assert.Equal(7, original.StockQuantity);
        Assert.False(original.IsArchived);
        Assert.True(original.IsPinnedAsNew);

        var line = Assert.Single(await AuditAsync("Product.Duplicated", copy.Id.ToString()));
        Assert.Equal(admin, line.UserId);
        Assert.Equal($"{newSku} from {sku}", line.Details);
    }

    [Fact]
    public async Task Duplicate_RefusesAnEmptyOrTooLongSku_ATakenOne_AndAnUnknownProduct()
    {
        var category = await NewCategoryAsync();
        var (id, sku) = await NewProductAsync(category);
        var (_, otherSku) = await NewProductAsync(category);
        var admin = await NewAdminAsync();
        var service = _fx.CreateProductToolsService();

        Assert.Equal(DuplicateProductStatus.InvalidSku, (await service.DuplicateAsync(id, "", admin)).Status);
        Assert.Equal(DuplicateProductStatus.InvalidSku, (await service.DuplicateAsync(id, "   ", admin)).Status);
        Assert.Equal(DuplicateProductStatus.InvalidSku, (await service.DuplicateAsync(id, new string('x', 51), admin)).Status);
        Assert.Equal(DuplicateProductStatus.DuplicateSku, (await _fx.CreateProductToolsService().DuplicateAsync(id, sku, admin)).Status);               // its own SKU
        Assert.Equal(DuplicateProductStatus.DuplicateSku, (await _fx.CreateProductToolsService().DuplicateAsync(id, otherSku.ToLowerInvariant(), admin)).Status); // any case
        Assert.Equal(DuplicateProductStatus.NotFound, (await _fx.CreateProductToolsService().DuplicateAsync(99999999, "NEW-" + Token(), admin)).Status);
        Assert.Equal(DuplicateProductStatus.Created, (await _fx.CreateProductToolsService().DuplicateAsync(id, new string('x', 50), admin)).Status);    // 50 is fine
    }

    // ================= Change many =================

    private async Task<List<int>> ProductsAsync(int category, int count, decimal price = 10000, int discount = 0, int stock = 10)
    {
        var ids = new List<int>();
        for (var i = 0; i < count; i++)
        {
            ids.Add((await NewProductAsync(category, price, discount, stock)).Id);
        }

        return ids;
    }

    [Fact]
    public async Task Bulk_SetDiscount_ShowsAPreview_ThenChangesOnlyWhatDiffers_WithOneAuditLinePerChangedProduct()
    {
        var category = await NewCategoryAsync();
        var first = await ProductsAsync(category, 2, discount: 0);
        var already = (await NewProductAsync(category, discount: 20)).Id;
        var ids = first.Append(already).ToList();
        var change = new BulkChange { Action = BulkAction.SetDiscount, Value = 20 };
        var admin = await NewAdminAsync();

        var preview = await _fx.CreateProductToolsService().PreviewBulkAsync(ids, change, "en");

        Assert.True(preview.IsValid);
        Assert.Equal(3, preview.Rows.Count);
        Assert.Equal(2, preview.ChangedCount);
        Assert.All(preview.Rows.Where(r => r.Id != already), r => { Assert.Equal("0", r.Before); Assert.Equal("20", r.After); Assert.True(r.Changes); });
        Assert.False(preview.Rows.Single(r => r.Id == already).Changes);
        Assert.Equal(0, (await LoadAsync(first[0])).DiscountPercent); // the preview saved nothing

        var result = await _fx.CreateProductToolsService().ApplyBulkAsync(ids, change, admin);

        Assert.Equal(new BulkApplyResult(BulkApplyStatus.Applied, 2, 1), result);
        foreach (var id in ids)
        {
            Assert.Equal(20, (await LoadAsync(id)).DiscountPercent);
        }

        foreach (var id in first)
        {
            var line = Assert.Single(await AuditAsync("Product.Bulk", id.ToString()));
            Assert.Equal(admin, line.UserId);
            Assert.Equal("SetDiscount: 0 -> 20", line.Details);
        }

        Assert.Empty(await AuditAsync("Product.Bulk", already.ToString())); // it was already like that
    }

    [Theory]
    [InlineData(1000, 10, 1100)]
    [InlineData(999, 10, 1099)]      // 1098.9
    [InlineData(105, 10, 116)]       // 115.5 rounds up
    [InlineData(15000, -5, 14250)]
    [InlineData(1000, 0, 1000)]
    [InlineData(1000, 1000, 11000)]
    [InlineData(5, -90, 1)]          // 0.5 rounds up to 1
    public async Task Bulk_ChangePriceByPercent_RoundsToWholeDrams(decimal price, decimal percent, decimal expected)
    {
        var category = await NewCategoryAsync();
        var (id, _) = await NewProductAsync(category, price: price);

        var result = await _fx.CreateProductToolsService().ApplyBulkAsync([id], new BulkChange { Action = BulkAction.ChangePricePercent, Value = percent }, await NewAdminAsync());

        Assert.Equal(BulkApplyStatus.Applied, result.Status);
        Assert.Equal(expected, (await LoadAsync(id)).Price);
    }

    [Fact]
    public async Task Bulk_APriceThatWouldBeBelowOne_OrTooHigh_IsAProblem_AndNothingIsSaved_NotEvenForTheOthers()
    {
        var category = await NewCategoryAsync();
        var fine = (await NewProductAsync(category, price: 20000)).Id;
        var tiny = (await NewProductAsync(category, price: 4)).Id;   // 4 - 90% = 0.4 -> 0
        var change = new BulkChange { Action = BulkAction.ChangePricePercent, Value = -90 };

        var preview = await _fx.CreateProductToolsService().PreviewBulkAsync([fine, tiny], change, "en");

        Assert.False(preview.IsValid);
        Assert.Null(preview.Rows.Single(r => r.Id == fine).Problem);
        Assert.Equal("Admin_Bulk_Problem_PriceLow", preview.Rows.Single(r => r.Id == tiny).Problem);

        var result = await _fx.CreateProductToolsService().ApplyBulkAsync([fine, tiny], change, await NewAdminAsync());

        Assert.Equal(BulkApplyStatus.Invalid, result.Status);
        Assert.Equal(20000m, (await LoadAsync(fine)).Price); // all or nothing
        Assert.Equal(4m, (await LoadAsync(tiny)).Price);

        var rich = (await NewProductAsync(category, price: 90_000_000)).Id;
        var tooHigh = await _fx.CreateProductToolsService().PreviewBulkAsync([rich], new BulkChange { Action = BulkAction.ChangePricePercent, Value = 100 }, "en");
        Assert.Equal("Admin_Bulk_Problem_PriceHigh", tooHigh.Rows.Single().Problem);
    }

    [Fact]
    public async Task Bulk_SetPrice_SetStock_Archive_Restore_AndMove_Work()
    {
        var category = await NewCategoryAsync();
        var target = await NewCategoryAsync("Target " + Token());
        var ids = await ProductsAsync(category, 2, price: 5000, stock: 3);
        var admin = await NewAdminAsync();

        async Task Apply(BulkAction action, decimal? value = null, int? categoryId = null)
        {
            var result = await _fx.CreateProductToolsService().ApplyBulkAsync(ids, new BulkChange { Action = action, Value = value, CategoryId = categoryId }, admin);
            Assert.Equal(BulkApplyStatus.Applied, result.Status);
            Assert.Equal(2, result.Changed);
        }

        await Apply(BulkAction.SetPrice, 7777.5m);
        await Apply(BulkAction.SetStock, 40);
        await Apply(BulkAction.Archive);
        await Apply(BulkAction.MoveCategory, categoryId: target);

        foreach (var id in ids)
        {
            var p = await LoadAsync(id);
            Assert.Equal(7777.5m, p.Price);
            Assert.Equal(40, p.StockQuantity);
            Assert.True(p.IsArchived);
            Assert.Equal(target, p.CategoryId);
        }

        await Apply(BulkAction.Restore);
        Assert.All(ids, id => Assert.False(LoadAsync(id).Result.IsArchived));

        // Doing it again changes nothing and says so.
        var again = await _fx.CreateProductToolsService().ApplyBulkAsync(ids, new BulkChange { Action = BulkAction.Restore }, admin);
        Assert.Equal(new BulkApplyResult(BulkApplyStatus.Applied, 0, 2), again);

        var move = await _fx.CreateProductToolsService().PreviewBulkAsync(ids, new BulkChange { Action = BulkAction.MoveCategory, CategoryId = category }, "hy");
        Assert.All(move.Rows, r => { Assert.True(r.Changes); Assert.False(string.IsNullOrEmpty(r.Before)); Assert.False(string.IsNullOrEmpty(r.After)); Assert.NotEqual(r.Before, r.After); });
    }

    [Theory]
    [InlineData(BulkAction.SetDiscount, -1)]
    [InlineData(BulkAction.SetDiscount, 101)]
    [InlineData(BulkAction.SetDiscount, 5.5)]
    [InlineData(BulkAction.ChangePricePercent, -91)]
    [InlineData(BulkAction.ChangePricePercent, 1001)]
    [InlineData(BulkAction.SetPrice, 0)]
    [InlineData(BulkAction.SetPrice, -5)]
    [InlineData(BulkAction.SetPrice, 100000001)]
    [InlineData(BulkAction.SetPrice, 10.123)]
    [InlineData(BulkAction.SetStock, -1)]
    [InlineData(BulkAction.SetStock, 100001)]
    [InlineData(BulkAction.SetStock, 2.5)]
    public async Task Bulk_AWrongValue_IsRefused_ByThePreviewAndTheApply(BulkAction action, double value)
    {
        var category = await NewCategoryAsync();
        var (id, _) = await NewProductAsync(category, price: 5000, discount: 5, stock: 5);
        var change = new BulkChange { Action = action, Value = (decimal)value };

        var preview = await _fx.CreateProductToolsService().PreviewBulkAsync([id], change, "en");
        var result = await _fx.CreateProductToolsService().ApplyBulkAsync([id], change, await NewAdminAsync());

        Assert.Equal("Admin_Bulk_BadValue", preview.ErrorKey);
        Assert.False(preview.IsValid);
        Assert.Equal(BulkApplyStatus.Invalid, result.Status);
        var p = await LoadAsync(id);
        Assert.Equal((5000m, 5, 5), (p.Price, p.DiscountPercent, p.StockQuantity));
    }

    [Fact]
    public async Task Bulk_NoValue_AndAnUnknownCategory_AreRefused()
    {
        var category = await NewCategoryAsync();
        var (id, _) = await NewProductAsync(category);
        var service = _fx.CreateProductToolsService();

        foreach (var action in new[] { BulkAction.SetDiscount, BulkAction.ChangePricePercent, BulkAction.SetPrice, BulkAction.SetStock })
        {
            Assert.Equal("Admin_Bulk_BadValue", (await service.PreviewBulkAsync([id], new BulkChange { Action = action }, "en")).ErrorKey);
        }

        Assert.Equal("Admin_Bulk_NoCategory", (await service.PreviewBulkAsync([id], new BulkChange { Action = BulkAction.MoveCategory }, "en")).ErrorKey);
        Assert.Equal("Admin_Bulk_NoCategory", (await service.PreviewBulkAsync([id], new BulkChange { Action = BulkAction.MoveCategory, CategoryId = 987654 }, "en")).ErrorKey);
        Assert.Equal(BulkApplyStatus.Invalid, (await service.ApplyBulkAsync([id], new BulkChange { Action = BulkAction.MoveCategory, CategoryId = 987654 }, await NewAdminAsync())).Status);
    }

    [Fact]
    public async Task Bulk_NothingSelected_TooMany_UnknownIds_AndRepeatedIds()
    {
        var category = await NewCategoryAsync();
        var ids = await ProductsAsync(category, 2);
        var change = new BulkChange { Action = BulkAction.SetStock, Value = 9 };
        var admin = await NewAdminAsync();

        Assert.Equal(BulkApplyStatus.NothingSelected, (await _fx.CreateProductToolsService().ApplyBulkAsync([], change, admin)).Status);
        Assert.Equal(BulkApplyStatus.NothingSelected, (await _fx.CreateProductToolsService().ApplyBulkAsync([99999991, 99999992], change, admin)).Status);
        Assert.Equal(BulkApplyStatus.TooMany, (await _fx.CreateProductToolsService().ApplyBulkAsync(Enumerable.Range(1, BulkRules.MaxProducts + 1).ToList(), change, admin)).Status);

        // Unknown ids are left out, repeated ids count once.
        var result = await _fx.CreateProductToolsService().ApplyBulkAsync([ids[0], ids[0], ids[1], 99999991], change, admin);
        Assert.Equal(new BulkApplyResult(BulkApplyStatus.Applied, 2, 0), result);

        var preview = await _fx.CreateProductToolsService().PreviewBulkAsync([ids[0], ids[0], 99999991], change, "en");
        Assert.Single(preview.Rows);
    }

    [Fact]
    public async Task FilterIds_AreNewestFirst_FollowTheFilter_AndGiveOneMoreThanMaxSoTheCallerSeesTooMany()
    {
        var category = await NewCategoryAsync();
        var otherCategory = await NewCategoryAsync();
        var hidden = (await NewProductAsync(category, archived: true)).Id;
        var shown = await ProductsAsync(category, 3);
        var elsewhere = (await NewProductAsync(otherCategory)).Id;
        var service = _fx.CreateProductToolsService();

        var active = await service.FilterIdsAsync(new AdminProductFilter { CategoryId = category }, 100);
        Assert.Equal(shown.AsEnumerable().Reverse(), active); // newest (highest id) first; the archived one is not in "active"

        var all = await service.FilterIdsAsync(new AdminProductFilter { CategoryId = category, Show = ProductVisibility.All }, 100);
        Assert.Equal(shown.Append(hidden).OrderByDescending(i => i), all);

        Assert.Equal([hidden], await service.FilterIdsAsync(new AdminProductFilter { CategoryId = category, Show = ProductVisibility.Archived }, 100));
        Assert.Equal(3, (await service.FilterIdsAsync(new AdminProductFilter { CategoryId = category }, 2)).Count); // max 2 -> returns 3: "too many"
        Assert.DoesNotContain(elsewhere, active);

        var bySku = await service.FilterIdsAsync(new AdminProductFilter { Query = (await LoadAsync(shown[1])).Sku, Show = ProductVisibility.All }, 10);
        Assert.Equal([shown[1]], bySku);
    }

    // ================= Export =================

    [Fact]
    public async Task Export_HasTheHeader_EveryProductIncludingHiddenOnes_AndIsAudited()
    {
        var category = await NewCategoryAsync();
        var age1 = await NewAgeGroupAsync();
        var age2 = await NewAgeGroupAsync();
        var (id, sku) = await NewProductAsync(category, price: 8900.5m, discount: 15, stock: 25, ages: [age1, age2], pinned: true);
        var (_, hiddenSku) = await NewProductAsync(category, archived: true);
        var admin = await NewAdminAsync();

        var bytes = await _fx.CreateProductToolsService().ExportCsvAsync(admin);

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var rows = CsvReader.Parse(Encoding.UTF8.GetString(bytes)).Select(r => r.ToArray()).ToList();
        Assert.Equal(ProductCsv.Columns, rows[0]);
        var row = rows.Single(r => r[0] == sku);
        var original = await LoadAsync(id);
        Assert.Equal(["Sku", "Brand", "CategoryId", "Price", "DiscountPercent", "Stock", "Archived", "NewArrival", "NameHy", "NameRu", "NameEn", "DescriptionHy", "DescriptionRu", "DescriptionEn", "AgeGroupIds"], rows[0]);
        Assert.Equal($"Brand {sku}", row[1]);
        Assert.Equal(category.ToString(), row[2]);
        Assert.Equal("8900.5", row[3]);
        Assert.Equal("15", row[4]);
        Assert.Equal("25", row[5]);
        Assert.Equal("no", row[6]);
        Assert.Equal("yes", row[7]);
        Assert.Equal(original.Translations.Single(t => t.Culture == "hy").Name, row[8]);
        Assert.Equal($"{age1};{age2}", row[14]);
        Assert.Equal("yes", rows.Single(r => r[0] == hiddenSku)[6]);

        Assert.NotEmpty(await AuditAsync("Export.Products"));
    }

    [Fact]
    public async Task AnExportedFile_ImportedAgain_ChangesNothing_EvenWithHardTexts()
    {
        var category = await NewCategoryAsync();
        var age = await NewAgeGroupAsync();
        await NewProductAsync(category, nameHy: "=SUM(1,2) \"quoted\", with comma", ages: [age]);
        await NewProductAsync(category, nameHy: "-minus first");
        await NewProductAsync(category, archived: true, pinned: true);
        var service = _fx.CreateProductToolsService();

        var csv = Encoding.UTF8.GetString(await service.ExportCsvAsync(await NewAdminAsync()));
        var preview = await _fx.CreateProductToolsService().PreviewImportAsync(csv);

        Assert.True(preview.FileProblems.Count == 0, string.Join(";", preview.FileProblems.Select(p => p.Key)));
        Assert.Empty(preview.Rows.Where(r => r.Action == ImportAction.Error).Select(r => $"{r.Sku}: {string.Join(",", r.Problems.Select(p => p.Key + " " + p.A))}"));
        Assert.Equal(0, preview.Creates);
        Assert.Equal(0, preview.Updates);
        Assert.Equal(preview.Rows.Count, preview.Unchanged);
    }

    // ================= Import: checking =================

    private static string Csv(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    private async Task<ImportPreview> PreviewAsync(string csv) => await _fx.CreateProductToolsService().PreviewImportAsync(csv);

    private static ImportRow Row(ImportPreview preview, string sku) => preview.Rows.Single(r => r.Sku == sku);

    [Fact]
    public async Task Import_ANewProduct_NeedsItsBasics_AndTheOthersAreReportedByRow()
    {
        var category = await NewCategoryAsync();
        var preview = await PreviewAsync(Csv(
            Header,
            $"NEW-A,Brand,{category},1500,0,5,no,no,Name,,,,,,",                // fine
            $"NEW-B,,{category},1500,,,,,Name,,,,,,",                           // no brand
            $"NEW-C,Brand,,1500,,,,,Name,,,,,,",                                // no category
            $"NEW-D,Brand,{category},,,,,,Name,,,,,,",                          // no price
            $"NEW-E,Brand,{category},1500,,,,,,,,,,,"));                        // no Armenian name

        Assert.Equal(ImportAction.Create, Row(preview, "NEW-A").Action);
        foreach (var (sku, column) in new[] { ("NEW-B", "Brand"), ("NEW-C", "CategoryId"), ("NEW-D", "Price"), ("NEW-E", "NameHy") })
        {
            var row = Row(preview, sku);
            Assert.Equal(ImportAction.Error, row.Action);
            Assert.Contains(row.Problems, p => p.Key == "Admin_Imp_R_Required" && p.A == column);
        }

        Assert.False(preview.IsValid);
        Assert.Equal(4, preview.ErrorRows);
        Assert.Equal(1, preview.Creates);
        Assert.Equal(["1", "2", "3", "4", "5"], preview.Rows.Select(r => r.Row.ToString()));
    }

    [Fact]
    public async Task Import_WrongValues_AreReported_WithTheColumnAndTheValue()
    {
        var category = await NewCategoryAsync();
        var age = await NewAgeGroupAsync();
        var bad = new (string Line, string Key, string Column)[]
        {
            ($"B1,Br,{category},abc,0,0,no,no,N,,,,,,", "Admin_Imp_R_Invalid", "Price"),
            ($"B2,Br,{category},-5,0,0,no,no,N,,,,,,", "Admin_Imp_R_Range", "Price"),
            ($"B3,Br,{category},100000001,0,0,no,no,N,,,,,,", "Admin_Imp_R_Range", "Price"),
            ($"B4,Br,{category},10.123,0,0,no,no,N,,,,,,", "Admin_Imp_R_Invalid", "Price"),
            ($"B5,Br,{category},1500,101,0,no,no,N,,,,,,", "Admin_Imp_R_Range", "DiscountPercent"),
            ($"B6,Br,{category},1500,x,0,no,no,N,,,,,,", "Admin_Imp_R_Invalid", "DiscountPercent"),
            ($"B7,Br,{category},1500,0,-1,no,no,N,,,,,,", "Admin_Imp_R_Range", "Stock"),
            ($"B8,Br,{category},1500,0,100001,no,no,N,,,,,,", "Admin_Imp_R_Range", "Stock"),
            ($"B9,Br,{category},1500,0,2.5,no,no,N,,,,,,", "Admin_Imp_R_Invalid", "Stock"),
            ($"B10,Br,{category},1500,0,0,maybe,no,N,,,,,,", "Admin_Imp_R_Bool", "Archived"),
            ($"B11,Br,{category},1500,0,0,no,perhaps,N,,,,,,", "Admin_Imp_R_Bool", "NewArrival"),
            ($"B12,Br,987654,1500,0,0,no,no,N,,,,,,", "Admin_Imp_R_NotFound", "CategoryId"),
            ($"B13,Br,abc,1500,0,0,no,no,N,,,,,,", "Admin_Imp_R_Invalid", "CategoryId"),
            ($"B14,Br,{category},1500,0,0,no,no,N,,,,,,{age};987654", "Admin_Imp_R_NotFound", "AgeGroupIds"),
            ($"B15,Br,{category},1500,0,0,no,no,N,,,,,,x", "Admin_Imp_R_Invalid", "AgeGroupIds"),
            ($"B16,{new string('b', 101)},{category},1500,0,0,no,no,N,,,,,,", "Admin_Imp_R_TooLong", "Brand"),
            ($"B17,Br,{category},1500,0,0,no,no,{new string('n', 201)},,,,,,", "Admin_Imp_R_TooLong", "NameHy"),
            ($"B18,Br,{category},1500,0,0,no,no,N,,,{new string('d', 4001)},,,", "Admin_Imp_R_TooLong", "DescriptionHy"),
            ($"B19,Br,{category},1500,0,0,no,no,N,,,,Description only,,", "Admin_Imp_R_DescNeedsName", "DescriptionRu"),
            ($"{new string('s', 51)},Br,{category},1500,0,0,no,no,N,,,,,,", "Admin_Imp_R_TooLong", "Sku"),
            ($",Br,{category},1500,0,0,no,no,N,,,,,,", "Admin_Imp_R_NoSku", ""),
            ($"B22,Br,{category},1500,0,0,no,no,N,,,,,,,extra-cell", "Admin_Imp_R_ExtraCells", "")
        };

        var preview = await PreviewAsync(Csv([Header, .. bad.Select(b => b.Line)]));

        Assert.Equal(bad.Length, preview.Rows.Count);
        for (var i = 0; i < bad.Length; i++)
        {
            var row = preview.Rows[i];
            Assert.Equal(ImportAction.Error, row.Action);
            Assert.True(
                row.Problems.Any(p => p.Key == bad[i].Key && (bad[i].Column == "" || p.A == bad[i].Column)),
                $"Row {row.Row} ({bad[i].Key} {bad[i].Column}): got " + string.Join(", ", row.Problems.Select(p => p.Key + " " + p.A)));
        }
    }

    [Fact]
    public async Task Import_ARepeatedSku_IsReported_CaseInsensitively_PointingToTheFirstRow()
    {
        var category = await NewCategoryAsync();
        var sku = "DUP-" + Token();

        var preview = await PreviewAsync(Csv(Header, $"{sku},Br,{category},1500,,,,,N,,,,,,", $"{sku.ToLowerInvariant()},Br,{category},1600,,,,,N2,,,,,,"));

        Assert.Equal(ImportAction.Create, preview.Rows[0].Action);
        var second = preview.Rows[1];
        Assert.Equal(ImportAction.Error, second.Action);
        var problem = Assert.Single(second.Problems);
        Assert.Equal("Admin_Imp_R_Duplicate", problem.Key);
        Assert.Equal("1", problem.B);
    }

    [Fact]
    public async Task Import_FileProblems_StopEverything_NoRowIsLookedAt()
    {
        var category = await NewCategoryAsync();

        async Task<ImportPreview> Check(string csv) => await PreviewAsync(csv);
        static string[] Keys(ImportPreview p) => p.FileProblems.Select(x => x.Key).ToArray();

        var empty = await Check("");
        Assert.Equal(["Admin_Imp_P_Empty"], Keys(empty));
        Assert.False(empty.IsValid);

        Assert.Equal(["Admin_Imp_P_Empty"], Keys(await Check(Header + "\r\n")));                       // a header and no products
        Assert.Contains("Admin_Imp_P_NoSku", Keys(await Check($"Brand,Price\r\nB,1\r\n")));
        var unknown = await Check($"Sku,Colour\r\nA,red\r\n");
        Assert.Contains(unknown.FileProblems, p => p.Key == "Admin_Imp_P_UnknownColumn" && p.A == "Colour");
        Assert.Empty(unknown.Rows);
        var twice = await Check($"Sku,Price,price\r\nA,1,2\r\n");
        Assert.Contains(twice.FileProblems, p => p.Key == "Admin_Imp_P_DuplicateColumn" && p.A == "Price");

        var many = Csv([Header, .. Enumerable.Range(0, ProductCsv.MaxRows + 1).Select(i => $"M-{i},Br,{category},1,,,,,N,,,,,,")]);
        var tooMany = await Check(many);
        Assert.Contains(tooMany.FileProblems, p => p.Key == "Admin_Imp_P_TooMany" && p.A == "1000");
        Assert.Empty(tooMany.Rows);

        // Exactly the limit is fine.
        var limit = await Check(Csv([Header, .. Enumerable.Range(0, ProductCsv.MaxRows).Select(i => $"L-{Token()}-{i},Br,{category},1,,,,,N,,,,,,")]));
        Assert.True(limit.IsValid);
        Assert.Equal(ProductCsv.MaxRows, limit.Creates);
    }

    [Fact]
    public async Task Import_ColumnNamesAndSkus_AreMatchedIgnoringCase_AndSpaces_AndAnyDelimiter()
    {
        var category = await NewCategoryAsync();
        var (id, sku) = await NewProductAsync(category, price: 1000);

        var preview = await PreviewAsync($"﻿ sku ; PRICE ;stock\r\n  {sku.ToLowerInvariant()}  ; 1200 ; 9 \r\n");

        var row = Assert.Single(preview.Rows);
        Assert.Equal(ImportAction.Update, row.Action);
        Assert.Equal(["Price 1000 -> 1200", "Stock 10 -> 9"], row.Changes);
        Assert.Equal(id, (await LoadAsync(id)).Id);
    }

    [Fact]
    public async Task Import_UpdatesOnlyTheColumnsThatAreGiven_AnEmptyCellKeepsTheValue_AndUnchangedRowsSayUnchanged()
    {
        var category = await NewCategoryAsync();
        var (id, sku) = await NewProductAsync(category, price: 1000, discount: 10, stock: 10);
        var (_, same) = await NewProductAsync(category, price: 700, stock: 4);

        var preview = await PreviewAsync(Csv("Sku,Price,DiscountPercent,Stock,NameEn", $"{sku},1500,,,", $"{same},700,0,4,{(await LoadBySkuAsync(same))!.Translations.Single(t => t.Culture == "en").Name}"));

        var changed = Row(preview, sku);
        Assert.Equal(ImportAction.Update, changed.Action);
        Assert.Equal(["Price 1000 -> 1500"], changed.Changes);   // the empty cells changed nothing
        Assert.Equal(ImportAction.Unchanged, Row(preview, same).Action);
        Assert.Equal((1, 1), (preview.Updates, preview.Unchanged));
        Assert.Equal(1000m, (await LoadAsync(id)).Price);        // a preview saves nothing
    }

    // ================= Import: applying =================

    [Fact]
    public async Task Import_Apply_CreatesAndUpdates_InOneGo_WithAuditLines_AndANewProductStaysHidden()
    {
        var category = await NewCategoryAsync();
        var age = await NewAgeGroupAsync();
        var (id, existingSku) = await NewProductAsync(category, price: 1000, discount: 0, stock: 10);
        var (_, untouchedSku) = await NewProductAsync(category, price: 800);
        var newSku = "NEW-" + Token();
        var visibleSku = "VIS-" + Token();
        var admin = await NewAdminAsync();
        var csv = Csv(
            Header,
            $"{newSku},ToyLand,{category},12500,15,30,,yes,Հայ անուն,Русское имя,English name,Նկար,Описание,Desc,{age}",
            $"{visibleSku},ToyLand,{category},900,,5,no,,Name only,,,,,,",
            $"{existingSku},,,1300,10,,,,,,,,,,",
            $"{untouchedSku},,,800,,,,,,,,,,,");

        var result = await _fx.CreateProductToolsService().ApplyImportAsync(csv, admin);

        Assert.Equal(new ImportApplyResult(ImportApplyStatus.Applied, 2, 1, 1), result);

        var created = (await LoadBySkuAsync(newSku))!;
        Assert.Equal(("ToyLand", category, 12500m, 15, 30), (created.Brand, created.CategoryId, created.Price, created.DiscountPercent, created.StockQuantity));
        Assert.True(created.IsArchived);        // not on the shop until the admin has looked at it
        Assert.True(created.IsPinnedAsNew);
        Assert.Equal(OrderFixture.Now, created.CreatedAt);
        Assert.Equal([age], created.AgeGroups.Select(a => a.Id));
        Assert.Equal("Հայ անուն", created.Translations.Single(t => t.Culture == "hy").Name);
        Assert.Equal("Описание", created.Translations.Single(t => t.Culture == "ru").Description);
        Assert.Equal("English name", created.Translations.Single(t => t.Culture == "en").Name);

        var visible = (await LoadBySkuAsync(visibleSku))!;
        Assert.False(visible.IsArchived);       // the file said Archived = no
        Assert.Single(visible.Translations);    // only the Armenian name was given

        var updated = await LoadAsync(id);
        Assert.Equal((1300m, 10, 10), (updated.Price, updated.DiscountPercent, updated.StockQuantity)); // stock was not in the row

        // Audit: one line per created or updated product, one summary, none for the untouched one.
        Assert.Equal("created", (await AuditAsync("Product.Imported", created.Id.ToString())).Single().Details!.Split(": ")[1]);
        Assert.Contains("Price 1000 -> 1300", (await AuditAsync("Product.Imported", id.ToString())).Single().Details);
        Assert.Empty(await AuditAsync("Product.Imported", (await LoadBySkuAsync(untouchedSku))!.Id.ToString()));
        var summary = (await AuditAsync("Import.Products")).Last();
        Assert.Equal(admin, summary.UserId);
        Assert.Equal("2 created, 1 updated, 1 unchanged", summary.Details);
    }

    [Fact]
    public async Task Import_Apply_IsAllOrNothing_OneBadRowMeansNothingIsSaved()
    {
        var category = await NewCategoryAsync();
        var (id, sku) = await NewProductAsync(category, price: 1000);
        var goodNew = "GOOD-" + Token();
        var csv = Csv(Header, $"{goodNew},Br,{category},500,,,,,N,,,,,,", $"{sku},,,2000,,,,,,,,,,,", $"BAD-{Token()},Br,{category},abc,,,,,N,,,,,,");

        var result = await _fx.CreateProductToolsService().ApplyImportAsync(csv, await NewAdminAsync());

        Assert.Equal(ImportApplyStatus.Invalid, result.Status);
        Assert.Null(await LoadBySkuAsync(goodNew));
        Assert.Equal(1000m, (await LoadAsync(id)).Price);

        // An empty file, and a file with no rows, apply nothing too.
        Assert.Equal(ImportApplyStatus.Invalid, (await _fx.CreateProductToolsService().ApplyImportAsync("", await NewAdminAsync())).Status);
        Assert.Equal(ImportApplyStatus.Invalid, (await _fx.CreateProductToolsService().ApplyImportAsync(Header + "\r\n", await NewAdminAsync())).Status);
    }

    [Fact]
    public async Task Import_Apply_TextsAndAgeGroupsAndFlags_OfAnExistingProduct()
    {
        var category = await NewCategoryAsync();
        var age1 = await NewAgeGroupAsync();
        var age2 = await NewAgeGroupAsync();
        var (id, sku) = await NewProductAsync(category, ages: [age1], archived: false);

        var csv = Csv("Sku,Archived,NewArrival,NameRu,DescriptionEn,AgeGroupIds", $"{sku},yes,yes,Новое имя,New description,{age2}");
        var result = await _fx.CreateProductToolsService().ApplyImportAsync(csv, await NewAdminAsync());

        Assert.Equal(ImportApplyStatus.Applied, result.Status);
        var p = await LoadAsync(id);
        Assert.True(p.IsArchived);
        Assert.True(p.IsPinnedAsNew);
        Assert.Equal("Новое имя", p.Translations.Single(t => t.Culture == "ru").Name);
        Assert.Equal("Описание " + sku, p.Translations.Single(t => t.Culture == "ru").Description); // the description stayed
        Assert.Equal("New description", p.Translations.Single(t => t.Culture == "en").Description);
        Assert.Equal("Name " + sku, p.Translations.Single(t => t.Culture == "en").Name);
        Assert.Equal([age2], p.AgeGroups.Select(a => a.Id));
    }

    [Fact]
    public async Task Import_ADescriptionForALanguageThatHasNoName_IsAProblem_ButWithTheNameItIsFine()
    {
        var category = await NewCategoryAsync();
        var (id, sku) = await NewProductAsync(category);
        using (var db = _fx.CreateDbContext())
        {
            await db.ProductTranslations.Where(t => t.ProductId == id && t.Culture == "ru").ExecuteDeleteAsync();
        }

        var noName = await PreviewAsync(Csv("Sku,DescriptionRu", $"{sku},Только описание"));
        Assert.Contains(noName.Rows.Single().Problems, p => p.Key == "Admin_Imp_R_DescNeedsName");

        var result = await _fx.CreateProductToolsService().ApplyImportAsync(Csv("Sku,NameRu,DescriptionRu", $"{sku},Имя,Описание"), await NewAdminAsync());
        Assert.Equal(ImportApplyStatus.Applied, result.Status);
        var ru = (await LoadAsync(id)).Translations.Single(t => t.Culture == "ru");
        Assert.Equal(("Имя", "Описание"), (ru.Name, ru.Description));
    }

    [Fact]
    public async Task Import_TwoImportsAtTheSameMoment_AreHandledOneAfterTheOther()
    {
        var category = await NewCategoryAsync();
        var sku = "RACE-" + Token();
        var csv = Csv(Header, $"{sku},Br,{category},500,,,,,N,,,,,,");
        var admin = await NewAdminAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => _fx.CreateProductToolsService().ApplyImportAsync(csv, admin))));

        // The first creates the product; the second finds it already there and finds nothing to change.
        Assert.Equal(1, results.Count(r => r.Created == 1));
        Assert.Equal(1, results.Count(r => r.Unchanged == 1 && r.Created == 0));
        using var db = _fx.CreateDbContext();
        Assert.Equal(1, await db.Products.CountAsync(p => p.Sku == sku));
    }
}

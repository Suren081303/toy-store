using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Orders;

namespace Shop.Tests;

[Collection("Orders")]
public class AdminCatalogServiceTests
{
    private readonly OrderFixture _fx;

    public AdminCatalogServiceTests(OrderFixture fx)
    {
        _fx = fx;
    }

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<int> NewCategoryAsync(string? nameHy = null, bool active = true)
    {
        var saved = await _fx.CreateCatalogAdminServices().Categories.SaveAsync(
            new AdminCategoryForm { NameHy = nameHy ?? Unique("Կատեգորիա"), NameEn = "Cat", SortOrder = 5, IsActive = active }, await _fx.CreateUserAsync());
        return saved.Id!.Value;
    }

    private async Task<int> NewAgeGroupAsync()
    {
        return (await _fx.CreateCatalogAdminServices().AgeGroups.SaveAsync(
            new AdminAgeGroupForm { NameHy = Unique("Տարիք"), SortOrder = 5 }, await _fx.CreateUserAsync()))!.Value;
    }

    private AdminProductForm NewForm(int categoryId, string? sku = null) => new()
    {
        Sku = sku ?? Unique("SKU"),
        Brand = "PlayCo",
        CategoryId = categoryId,
        Price = 10000,
        DiscountPercent = 0,
        StockQuantity = 10,
        NameHy = Unique("Խաղ"),
        DescriptionHy = "Նկարագրություն"
    };

    private async Task<int> NewProductAsync(int? categoryId = null, Action<AdminProductForm>? change = null)
    {
        var form = NewForm(categoryId ?? await NewCategoryAsync());
        change?.Invoke(form);
        var result = await _fx.CreateCatalogAdminServices().Products.SaveAsync(form, await _fx.CreateUserAsync());
        Assert.Equal(SaveProductStatus.Saved, result.Status);
        return result.Id!.Value;
    }

    // ---- Categories ----

    [Fact]
    public async Task Category_Create_TrimsNames_KeepsOnlyTheTypedLanguages_AndResolvesWithFallback()
    {
        var adminId = await _fx.CreateUserAsync();
        var categories = _fx.CreateCatalogAdminServices().Categories;

        var saved = await categories.SaveAsync(new AdminCategoryForm { NameHy = "  Հայերեն  ", NameRu = "   ", NameEn = "English", SortOrder = 3 }, adminId);

        var form = (await _fx.CreateCatalogAdminServices().Categories.GetAsync(saved.Id!.Value))!;
        Assert.Equal("Հայերեն", form.NameHy);
        Assert.Equal(string.Empty, form.NameRu); // nothing typed: no Russian row
        Assert.Equal("English", form.NameEn);

        // In the list, a language without a name shows the Armenian one.
        var list = await _fx.CreateCatalogAdminServices().Categories.ListAsync("ru");
        Assert.Equal("Հայերեն", list.Single(r => r.Id == saved.Id).Name);
        Assert.Equal("English", (await _fx.CreateCatalogAdminServices().Categories.ListAsync("en")).Single(r => r.Id == saved.Id).Name);
    }

    [Fact]
    public async Task Category_Change_RemovesAClearedLanguage_AndReportsTheReplacedPicture()
    {
        var adminId = await _fx.CreateUserAsync();
        var categories = _fx.CreateCatalogAdminServices().Categories;
        var created = await categories.SaveAsync(new AdminCategoryForm { NameHy = "Ա", NameRu = "Б", ImagePath = "/uploads/categories/old.jpg" }, adminId);

        var changed = await _fx.CreateCatalogAdminServices().Categories.SaveAsync(new AdminCategoryForm
        {
            Id = created.Id,
            NameHy = "Ա2",
            NameRu = string.Empty,
            SortOrder = 9,
            IsActive = false,
            ImagePath = "/uploads/categories/new.jpg"
        }, adminId);

        Assert.Equal(created.Id, changed.Id);
        Assert.Equal("/uploads/categories/old.jpg", changed.ReplacedImagePath);

        var form = (await _fx.CreateCatalogAdminServices().Categories.GetAsync(created.Id!.Value))!;
        Assert.Equal("Ա2", form.NameHy);
        Assert.Equal(string.Empty, form.NameRu);
        Assert.False(form.IsActive);
        Assert.Equal(9, form.SortOrder);

        // Saving again with the same picture reports nothing to delete.
        var same = await _fx.CreateCatalogAdminServices().Categories.SaveAsync(form, adminId);
        Assert.Null(same.ReplacedImagePath);
    }

    [Fact]
    public async Task Category_Save_OfAMissingId_ReportsNull()
    {
        var result = await _fx.CreateCatalogAdminServices().Categories.SaveAsync(new AdminCategoryForm { Id = -1, NameHy = "x" }, await _fx.CreateUserAsync());

        Assert.Null(result.Id);
    }

    [Fact]
    public async Task Category_WithoutAnArmenianName_IsAProgrammingError()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _fx.CreateCatalogAdminServices().Categories.SaveAsync(new AdminCategoryForm { NameHy = "  " }, await _fx.CreateUserAsync()));
    }

    [Fact]
    public async Task Category_List_CountsProducts_ArchivedOnesToo()
    {
        var categoryId = await NewCategoryAsync();
        await NewProductAsync(categoryId);
        await NewProductAsync(categoryId, f => f.IsArchived = true);

        var row = (await _fx.CreateCatalogAdminServices().Categories.ListAsync("hy")).Single(r => r.Id == categoryId);

        Assert.Equal(2, row.ProductCount);
    }

    [Fact]
    public async Task Category_Delete_OnlyWhenNoProductUsesIt()
    {
        var adminId = await _fx.CreateUserAsync();
        var used = await NewCategoryAsync();
        await NewProductAsync(used, f => f.IsArchived = true); // even an archived product keeps it
        var empty = (await _fx.CreateCatalogAdminServices().Categories.SaveAsync(
            new AdminCategoryForm { NameHy = Unique("Դատարկ"), ImagePath = "/uploads/categories/x.jpg" }, adminId)).Id!.Value;
        var categories = _fx.CreateCatalogAdminServices().Categories;

        Assert.Equal(DeleteStatus.InUse, (await categories.DeleteAsync(used, adminId)).Status);
        Assert.NotNull(await _fx.CreateCatalogAdminServices().Categories.GetAsync(used));

        var deleted = await _fx.CreateCatalogAdminServices().Categories.DeleteAsync(empty, adminId);
        Assert.Equal(DeleteStatus.Deleted, deleted.Status);
        Assert.Equal("/uploads/categories/x.jpg", deleted.ImagePath); // so the caller can delete the file
        Assert.Null(await _fx.CreateCatalogAdminServices().Categories.GetAsync(empty));

        Assert.Equal(DeleteStatus.NotFound, (await _fx.CreateCatalogAdminServices().Categories.DeleteAsync(empty, adminId)).Status);
    }

    [Fact]
    public async Task Category_Options_IncludeHiddenCategories()
    {
        var hidden = await NewCategoryAsync(active: false);

        var options = await _fx.CreateCatalogAdminServices().Categories.OptionsAsync("hy");

        Assert.Contains(options, o => o.Id == hidden);
    }

    [Fact]
    public async Task Category_Changes_AreAudited()
    {
        var adminId = await _fx.CreateUserAsync();
        var saved = await _fx.CreateCatalogAdminServices().Categories.SaveAsync(new AdminCategoryForm { NameHy = "Աուդիտ" }, adminId);
        await _fx.CreateCatalogAdminServices().Categories.DeleteAsync(saved.Id!.Value, adminId);

        using var db = _fx.CreateDbContext();
        var actions = await db.AuditLogs.Where(a => a.EntityType == "Category" && a.EntityId == saved.Id.ToString() && a.UserId == adminId)
            .OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
        Assert.Equal(["Category.Created", "Category.Deleted"], actions);
    }

    // ---- Age groups ----

    [Fact]
    public async Task AgeGroup_CreateChangeDelete_AndInUseIsRefused()
    {
        var adminId = await _fx.CreateUserAsync();
        var groups = _fx.CreateCatalogAdminServices().AgeGroups;
        var id = (await groups.SaveAsync(new AdminAgeGroupForm { NameHy = "0-1", NameEn = "0-1 year", SortOrder = 1 }, adminId))!.Value;

        var form = (await _fx.CreateCatalogAdminServices().AgeGroups.GetAsync(id))!;
        Assert.Equal("0-1 year", form.NameEn);

        form.NameEn = string.Empty;
        form.IsActive = false;
        await _fx.CreateCatalogAdminServices().AgeGroups.SaveAsync(form, adminId);
        var changed = (await _fx.CreateCatalogAdminServices().AgeGroups.GetAsync(id))!;
        Assert.Equal(string.Empty, changed.NameEn);
        Assert.False(changed.IsActive);

        // A product uses it: it cannot be deleted, and the list counts the product.
        var productId = await NewProductAsync(change: f => f.AgeGroupIds.Add(id));
        Assert.Equal(DeleteStatus.InUse, await _fx.CreateCatalogAdminServices().AgeGroups.DeleteAsync(id, adminId));
        Assert.Equal(1, (await _fx.CreateCatalogAdminServices().AgeGroups.ListAsync("hy")).Single(r => r.Id == id).ProductCount);

        // Taken off the product, it can go.
        var product = (await _fx.CreateCatalogAdminServices().Products.GetAsync(productId))!;
        product.Form.AgeGroupIds.Clear();
        await _fx.CreateCatalogAdminServices().Products.SaveAsync(product.Form, adminId);
        Assert.Equal(DeleteStatus.Deleted, await _fx.CreateCatalogAdminServices().AgeGroups.DeleteAsync(id, adminId));
        Assert.Equal(DeleteStatus.NotFound, await _fx.CreateCatalogAdminServices().AgeGroups.DeleteAsync(id, adminId));
        Assert.Null(await _fx.CreateCatalogAdminServices().AgeGroups.SaveAsync(new AdminAgeGroupForm { Id = -1, NameHy = "x" }, adminId));
    }

    // ---- Products: create and read ----

    [Fact]
    public async Task Product_Create_SavesEverything_AndReadsItBack()
    {
        var categoryId = await NewCategoryAsync();
        var ageA = await NewAgeGroupAsync();
        var ageB = await NewAgeGroupAsync();
        var form = NewForm(categoryId, "  ABC-123  ");
        form.Brand = "  Lego ";
        form.Price = 25000;
        form.DiscountPercent = 20;
        form.StockQuantity = 7;
        form.NameRu = "Русское имя";
        form.DescriptionRu = "Русское описание";
        form.NameEn = string.Empty;
        form.DescriptionEn = "Orphan description"; // no name: the English text is not kept
        form.AgeGroupIds = [ageA, ageB, ageA];       // a repeated id counts once
        form.IsPinnedAsNew = true;

        var result = await _fx.CreateCatalogAdminServices().Products.SaveAsync(form, await _fx.CreateUserAsync());

        Assert.Equal(SaveProductStatus.Saved, result.Status);
        var saved = (await _fx.CreateCatalogAdminServices().Products.GetAsync(result.Id!.Value))!.Form;
        Assert.Equal("ABC-123", saved.Sku);
        Assert.Equal("Lego", saved.Brand);
        Assert.Equal(categoryId, saved.CategoryId);
        Assert.Equal(25000, saved.Price);
        Assert.Equal(20, saved.DiscountPercent);
        Assert.Equal(7, saved.StockQuantity);
        Assert.Equal("Русское имя", saved.NameRu);
        Assert.Equal("Русское описание", saved.DescriptionRu);
        Assert.Equal(string.Empty, saved.NameEn);
        Assert.Equal(string.Empty, saved.DescriptionEn);
        Assert.Equal(new[] { ageA, ageB }.Order(), saved.AgeGroupIds.Order());
        Assert.True(saved.IsPinnedAsNew);
        Assert.False(string.IsNullOrEmpty(saved.RowVersion));
    }

    [Fact]
    public async Task Product_Create_WithAnUnknownCategory_OrADuplicateSku_IsRefused()
    {
        var adminId = await _fx.CreateUserAsync();
        var products = _fx.CreateCatalogAdminServices().Products;

        Assert.Equal(SaveProductStatus.UnknownCategory, (await products.SaveAsync(NewForm(-1), adminId)).Status);

        var existing = Unique("SKU");
        await NewProductAsync(change: f => f.Sku = existing);
        var duplicate = NewForm(await NewCategoryAsync(), existing.ToLowerInvariant()); // same SKU in lower case
        Assert.Equal(SaveProductStatus.DuplicateSku, (await _fx.CreateCatalogAdminServices().Products.SaveAsync(duplicate, adminId)).Status);
    }

    [Fact]
    public async Task Product_Update_MayKeepItsOwnSku_ButNotTakeAnothers()
    {
        var adminId = await _fx.CreateUserAsync();
        var mine = await NewProductAsync();
        var other = Unique("OTHER");
        await NewProductAsync(change: f => f.Sku = other);

        var form = (await _fx.CreateCatalogAdminServices().Products.GetAsync(mine))!.Form;
        Assert.Equal(SaveProductStatus.Saved, (await _fx.CreateCatalogAdminServices().Products.SaveAsync(form, adminId)).Status); // unchanged SKU

        form = (await _fx.CreateCatalogAdminServices().Products.GetAsync(mine))!.Form;
        form.Sku = other;
        Assert.Equal(SaveProductStatus.DuplicateSku, (await _fx.CreateCatalogAdminServices().Products.SaveAsync(form, adminId)).Status);
    }

    [Fact]
    public async Task Product_Update_ChangesFields_AndTheAuditLogShowsWhat()
    {
        var adminId = await _fx.CreateUserAsync();
        var id = await NewProductAsync();
        var form = (await _fx.CreateCatalogAdminServices().Products.GetAsync(id))!.Form;
        form.Price = 12345;
        form.StockQuantity = 3;
        form.IsArchived = true;
        form.NameEn = "A new English name";

        Assert.Equal(SaveProductStatus.Saved, (await _fx.CreateCatalogAdminServices().Products.SaveAsync(form, adminId)).Status);

        var saved = (await _fx.CreateCatalogAdminServices().Products.GetAsync(id))!.Form;
        Assert.Equal(12345, saved.Price);
        Assert.Equal(3, saved.StockQuantity);
        Assert.True(saved.IsArchived);
        Assert.Equal("A new English name", saved.NameEn);

        using var db = _fx.CreateDbContext();
        var audit = await db.AuditLogs.Where(a => a.EntityType == "Product" && a.EntityId == id.ToString() && a.Action == "Product.Changed").SingleAsync();
        Assert.Equal(adminId, audit.UserId);
        Assert.Contains("Price 10000 -> 12345", audit.Details);
        Assert.Contains("Stock 10 -> 3", audit.Details);
        Assert.Contains("Archived False -> True", audit.Details);
        Assert.Contains("texts changed", audit.Details);
    }

    [Fact]
    public async Task Product_Update_OfAMissingProduct_IsNotFound()
    {
        var form = NewForm(await NewCategoryAsync());
        form.Id = -7;
        form.RowVersion = Convert.ToBase64String(new byte[8]);

        Assert.Equal(SaveProductStatus.NotFound, (await _fx.CreateCatalogAdminServices().Products.SaveAsync(form, await _fx.CreateUserAsync())).Status);
    }

    // ---- Products: the "changed meanwhile" rule ----

    [Fact]
    public async Task Product_Save_AfterTheStockChangedMeanwhile_IsAConflict_AndChangesNothing()
    {
        var adminId = await _fx.CreateUserAsync();
        var id = await NewProductAsync();
        var opened = (await _fx.CreateCatalogAdminServices().Products.GetAsync(id))!.Form; // the admin opens the page

        await _fx.SetStockAsync(id, 4); // an order took some stock meanwhile

        opened.Price = 99999;
        var result = await _fx.CreateCatalogAdminServices().Products.SaveAsync(opened, adminId);

        Assert.Equal(SaveProductStatus.Conflict, result.Status);
        var saved = (await _fx.CreateCatalogAdminServices().Products.GetAsync(id))!.Form;
        Assert.Equal(10000, saved.Price); // the admin's change was not applied
        Assert.Equal(4, saved.StockQuantity); // the new stock was not overwritten

        // Opening the page again and saving works.
        saved.Price = 99999;
        Assert.Equal(SaveProductStatus.Saved, (await _fx.CreateCatalogAdminServices().Products.SaveAsync(saved, adminId)).Status);
    }

    [Fact]
    public async Task Product_Save_AfterAnOrder_IsAConflict()
    {
        var adminId = await _fx.CreateUserAsync();
        var order = await _fx.PlaceOrderAsync(stock: 10); // a real order takes stock
        var productId = order.Lines[0].ProductId;
        // The page was opened before the order was placed: use the version from before.
        var before = (await _fx.CreateCatalogAdminServices().Products.GetAsync(productId))!.Form;
        var owner = await _fx.FillCartAsync(null, (productId, 1));
        await _fx.CreateOrderService().PlaceAsync(owner, await _fx.InputAsync(owner), "en");

        Assert.Equal(SaveProductStatus.Conflict, (await _fx.CreateCatalogAdminServices().Products.SaveAsync(before, adminId)).Status);
    }

    [Fact]
    public async Task Product_Save_ThatChangesOnlyATranslation_IsStillChecked()
    {
        var adminId = await _fx.CreateUserAsync();
        var id = await NewProductAsync();
        var opened = (await _fx.CreateCatalogAdminServices().Products.GetAsync(id))!.Form;
        await _fx.SetStockAsync(id, 2);

        opened.NameEn = "Only the text changed";

        Assert.Equal(SaveProductStatus.Conflict, (await _fx.CreateCatalogAdminServices().Products.SaveAsync(opened, adminId)).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !!")]
    public async Task Product_Update_WithoutAValidVersion_IsRefused(string? version)
    {
        var adminId = await _fx.CreateUserAsync();
        var id = await NewProductAsync();
        var form = (await _fx.CreateCatalogAdminServices().Products.GetAsync(id))!.Form;
        form.RowVersion = version;

        Assert.Equal(SaveProductStatus.Conflict, (await _fx.CreateCatalogAdminServices().Products.SaveAsync(form, adminId)).Status);
    }

    // ---- Products: list ----

    [Fact]
    public async Task ProductList_SearchesSkuBrandAndNamesInAnyLanguage()
    {
        var token = Unique("zz");
        var bySku = await NewProductAsync(change: f => f.Sku = token + "-sku");
        var byBrand = await NewProductAsync(change: f => f.Brand = token + "brand");
        var byEnglishName = await NewProductAsync(change: f => f.NameEn = token + " english");
        var products = _fx.CreateCatalogAdminServices().Products;

        var found = (await products.ListAsync(new AdminProductFilter { Query = token }, "hy")).Items.Select(i => i.Id).ToList();

        Assert.Equal(3, found.Count);
        Assert.Contains(bySku, found);
        Assert.Contains(byBrand, found);
        Assert.Contains(byEnglishName, found);
        Assert.Empty((await _fx.CreateCatalogAdminServices().Products.ListAsync(new AdminProductFilter { Query = token + "-nothing" }, "hy")).Items);
    }

    [Fact]
    public async Task ProductList_FiltersByCategoryVisibilityAndLowStock()
    {
        var category = await NewCategoryAsync();
        var normal = await NewProductAsync(category);
        var low = await NewProductAsync(category, f => f.StockQuantity = StockLevels.Low);
        var archived = await NewProductAsync(category, f => f.IsArchived = true);
        ProductService Svc() => new(_fx);

        Assert.Equal(new[] { normal, low }.Order(), (await Svc().Ids(new AdminProductFilter { CategoryId = category })).Order());
        Assert.Equal(new List<int> { archived }, await Svc().Ids(new AdminProductFilter { CategoryId = category, Show = ProductVisibility.Archived }));
        Assert.Equal(3, (await Svc().Ids(new AdminProductFilter { CategoryId = category, Show = ProductVisibility.All })).Count);
        Assert.Equal(new List<int> { low }, await Svc().Ids(new AdminProductFilter { CategoryId = category, LowStock = true }));
    }

    // A tiny helper to keep the filter tests short.
    private sealed class ProductService(OrderFixture fx)
    {
        public async Task<List<int>> Ids(AdminProductFilter filter) =>
            (await fx.CreateCatalogAdminServices().Products.ListAsync(filter, "hy")).Items.Select(i => i.Id).ToList();
    }

    [Fact]
    public async Task ProductList_ShowsNamesInTheLanguage_WithFallback_AndTheSmallPhoto()
    {
        var adminId = await _fx.CreateUserAsync();
        var id = await NewProductAsync(change: f => f.NameEn = "English name");
        await _fx.CreateCatalogAdminServices().Products.AddImagesAsync(id, [new StoredImage("/uploads/products/1/a.jpg", "/uploads/products/1/a_t.jpg")], adminId);

        var english = (await _fx.CreateCatalogAdminServices().Products.ListAsync(new AdminProductFilter { Query = "English name" }, "en")).Items.Single();
        Assert.Equal("English name", english.Name);
        Assert.Equal("/uploads/products/1/a_t.jpg", english.ImagePath); // the small one

        var russian = (await _fx.CreateCatalogAdminServices().Products.ListAsync(new AdminProductFilter { Query = "English name" }, "ru")).Items.Single();
        Assert.StartsWith("Խաղ", russian.Name); // no Russian name: the Armenian one
    }

    [Fact]
    public async Task ProductList_IsPaged_NewestFirst()
    {
        for (var i = 0; i < 3; i++)
        {
            await NewProductAsync();
        }

        var page = await _fx.CreateCatalogAdminServices().Products.ListAsync(new AdminProductFilter { Show = ProductVisibility.All }, "hy");

        Assert.Equal(20, page.PageSize);
        Assert.True(page.TotalCount >= 3);
        Assert.True(page.Items.Zip(page.Items.Skip(1), (a, b) => a.Id > b.Id).All(x => x));
    }

    // ---- Quick stock ----

    [Fact]
    public async Task SetStock_SetsTheNumber_AndIsAudited()
    {
        var adminId = await _fx.CreateUserAsync();
        var id = await NewProductAsync(change: f => f.StockQuantity = 5);

        Assert.True(await _fx.CreateCatalogAdminServices().Products.SetStockAsync(id, 12, adminId));
        Assert.False(await _fx.CreateCatalogAdminServices().Products.SetStockAsync(-1, 12, adminId));

        Assert.Equal(12, await _fx.GetStockAsync(id));
        using var db = _fx.CreateDbContext();
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "Product.StockSet" && a.EntityId == id.ToString());
        Assert.Equal("Stock 5 -> 12", audit.Details);
        Assert.Equal(adminId, audit.UserId);
    }

    // ---- Photos ----

    private static StoredImage Photo(int n) => new($"/uploads/products/9/{n}.jpg", $"/uploads/products/9/{n}_t.jpg");

    private async Task<(int ProductId, string AdminId)> ProductWithPhotosAsync(int count)
    {
        var adminId = await _fx.CreateUserAsync();
        var id = await NewProductAsync();
        var result = await _fx.CreateCatalogAdminServices().Products.AddImagesAsync(id, Enumerable.Range(1, count).Select(Photo).ToList(), adminId);
        Assert.Equal(ImageChangeStatus.Done, result);
        return (id, adminId);
    }

    private async Task<List<string>> PathsAsync(int productId) =>
        (await _fx.CreateCatalogAdminServices().Products.GetAsync(productId))!.Images.Select(i => i.Path).ToList();

    [Fact]
    public async Task Photos_AreAddedInOrder_UpToSix_AndTheSeventhIsRefused()
    {
        var (id, adminId) = await ProductWithPhotosAsync(5);

        // Adding two more would make 7: nothing is added at all.
        Assert.Equal(ImageChangeStatus.TooMany, await _fx.CreateCatalogAdminServices().Products.AddImagesAsync(id, [Photo(6), Photo(7)], adminId));
        Assert.Equal(5, await _fx.CreateCatalogAdminServices().Products.CountImagesAsync(id));

        Assert.Equal(ImageChangeStatus.Done, await _fx.CreateCatalogAdminServices().Products.AddImagesAsync(id, [Photo(6)], adminId));
        Assert.Equal(ImageChangeStatus.TooMany, await _fx.CreateCatalogAdminServices().Products.AddImagesAsync(id, [Photo(7)], adminId));

        var images = (await _fx.CreateCatalogAdminServices().Products.GetAsync(id))!.Images;
        Assert.Equal([0, 1, 2, 3, 4, 5], images.Select(i => i.SortOrder));
        Assert.Equal(Enumerable.Range(1, 6).Select(n => Photo(n).Path), images.Select(i => i.Path));
    }

    [Fact]
    public async Task Photos_ForAMissingProduct_AreNotFound()
    {
        Assert.Equal(ImageChangeStatus.NotFound, await _fx.CreateCatalogAdminServices().Products.AddImagesAsync(-3, [Photo(1)], await _fx.CreateUserAsync()));
    }

    [Fact]
    public async Task Photo_MoveLaterAndEarlier_SwapsNeighbours_AndStopsAtTheEnds()
    {
        var (id, adminId) = await ProductWithPhotosAsync(3);
        var ids = (await _fx.CreateCatalogAdminServices().Products.GetAsync(id))!.Images.Select(i => i.Id).ToList();
        string P(int n) => Photo(n).Path;

        await _fx.CreateCatalogAdminServices().Products.MoveImageAsync(id, ids[0], +1, adminId);
        Assert.Equal([P(2), P(1), P(3)], await PathsAsync(id));

        await _fx.CreateCatalogAdminServices().Products.MoveImageAsync(id, ids[2], -1, adminId);
        Assert.Equal([P(2), P(3), P(1)], await PathsAsync(id));

        // Already last / already first: nothing changes.
        await _fx.CreateCatalogAdminServices().Products.MoveImageAsync(id, ids[0], +1, adminId);
        await _fx.CreateCatalogAdminServices().Products.MoveImageAsync(id, ids[1], -1, adminId);
        Assert.Equal([P(2), P(3), P(1)], await PathsAsync(id));
    }

    [Fact]
    public async Task Photo_MakeMain_MovesItToTheFront_AndRenumbers()
    {
        var (id, adminId) = await ProductWithPhotosAsync(3);
        var images = (await _fx.CreateCatalogAdminServices().Products.GetAsync(id))!.Images;

        await _fx.CreateCatalogAdminServices().Products.MakeMainImageAsync(id, images[2].Id, adminId);

        var after = (await _fx.CreateCatalogAdminServices().Products.GetAsync(id))!.Images;
        Assert.Equal([Photo(3).Path, Photo(1).Path, Photo(2).Path], after.Select(i => i.Path));
        Assert.Equal([0, 1, 2], after.Select(i => i.SortOrder));
    }

    [Fact]
    public async Task Photo_Remove_ReturnsThePaths_AndRenumbers()
    {
        var (id, adminId) = await ProductWithPhotosAsync(3);
        var images = (await _fx.CreateCatalogAdminServices().Products.GetAsync(id))!.Images;

        var removed = await _fx.CreateCatalogAdminServices().Products.RemoveImageAsync(id, images[0].Id, adminId);

        Assert.Equal(Photo(1).Path, removed!.Path);
        Assert.Equal(Photo(1).ThumbPath, removed.ThumbPath);
        var after = (await _fx.CreateCatalogAdminServices().Products.GetAsync(id))!.Images;
        Assert.Equal([Photo(2).Path, Photo(3).Path], after.Select(i => i.Path));
        Assert.Equal([0, 1], after.Select(i => i.SortOrder));
    }

    [Fact]
    public async Task Photo_OfAnotherProduct_CannotBeChanged()
    {
        var (idA, adminId) = await ProductWithPhotosAsync(2);
        var (idB, _) = await ProductWithPhotosAsync(1);
        var imageOfA = (await _fx.CreateCatalogAdminServices().Products.GetAsync(idA))!.Images[0].Id;
        var products = _fx.CreateCatalogAdminServices().Products;

        Assert.Null(await products.RemoveImageAsync(idB, imageOfA, adminId));
        Assert.Equal(ImageChangeStatus.NotFound, await _fx.CreateCatalogAdminServices().Products.MoveImageAsync(idB, imageOfA, 1, adminId));
        Assert.Equal(ImageChangeStatus.NotFound, await _fx.CreateCatalogAdminServices().Products.MakeMainImageAsync(idB, imageOfA, adminId));
        Assert.Equal(2, await _fx.CreateCatalogAdminServices().Products.CountImagesAsync(idA)); // untouched
    }

    [Fact]
    public async Task Photo_Changes_AreAudited()
    {
        var (id, adminId) = await ProductWithPhotosAsync(2);
        var images = (await _fx.CreateCatalogAdminServices().Products.GetAsync(id))!.Images;
        await _fx.CreateCatalogAdminServices().Products.MakeMainImageAsync(id, images[1].Id, adminId);
        await _fx.CreateCatalogAdminServices().Products.RemoveImageAsync(id, images[0].Id, adminId);

        using var db = _fx.CreateDbContext();
        var actions = await db.AuditLogs.Where(a => a.EntityType == "Product" && a.EntityId == id.ToString() && a.UserId == adminId && a.Action.Contains("Photo"))
            .OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
        Assert.Equal(["Product.PhotosAdded", "Product.MainPhotoSet", "Product.PhotoRemoved"], actions);
    }

    [Fact]
    public async Task TheShop_ShowsTheSmallPhotoOnOrderLines_AndStillTheFullOneInTheGallery()
    {
        var adminId = await _fx.CreateUserAsync();
        var order = await _fx.PlaceOrderAsync();
        var productId = order.Lines[0].ProductId;
        await _fx.CreateCatalogAdminServices().Products.AddImagesAsync(productId, [new StoredImage("/uploads/products/1/full.jpg", "/uploads/products/1/small.jpg")], adminId);

        var reloaded = await _fx.CreateOrderService().FindForSystemAsync(order.Number);

        Assert.Equal("/uploads/products/1/small.jpg", reloaded!.Lines[0].ImagePath);
    }
}

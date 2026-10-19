using Shop.Core.Catalog;

namespace Shop.Tests;

[Collection("Catalog")]
public class CatalogServiceTests
{
    private readonly CatalogFixture _fx;

    public CatalogServiceTests(CatalogFixture fx)
    {
        _fx = fx;
    }

    private int[] Ids(params string[] skus) => skus.Select(_fx.ProductId).ToArray();

    private static int[] ItemIds(PagedResult<ProductListItem> result) => result.Items.Select(i => i.Id).ToArray();

    [Fact]
    public async Task GetCategories_ReturnsActiveOnly_OrderedAndWithArmenianFallback()
    {
        var categories = await _fx.CreateService().GetCategoriesAsync("ru");

        Assert.Equal(new[] { "Игра А", "Խաղ Բ" }, categories.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task GetAgeGroups_ReturnsActiveOnly_InSortOrder()
    {
        var ageGroups = await _fx.CreateService().GetAgeGroupsAsync("en");

        Assert.Equal(new[] { "0–2 yrs", "3–5 yrs" }, ageGroups.Select(a => a.Name).ToArray());
    }

    [Fact]
    public async Task GetProducts_HidesArchivedAndInactiveCategoryProducts()
    {
        var result = await _fx.CreateService().GetProductsAsync(new ProductQuery { Culture = "en" });

        Assert.Equal(5, result.TotalCount);
        Assert.Equal(Ids("SKU-001", "SKU-002", "SKU-003", "SKU-004", "SKU-007").Order(), ItemIds(result).Order());
    }

    [Fact]
    public async Task GetProducts_FiltersByCategory()
    {
        var result = await _fx.CreateService().GetProductsAsync(new ProductQuery { CategoryId = _fx.CategoryBId });

        Assert.Equal(Ids("SKU-004", "SKU-007").Order(), ItemIds(result).Order());
    }

    [Fact]
    public async Task GetProducts_FiltersByAgeGroup()
    {
        var result = await _fx.CreateService().GetProductsAsync(new ProductQuery { AgeGroupId = _fx.AgeGroup2Id });

        Assert.Equal(Ids("SKU-002", "SKU-003", "SKU-004").Order(), ItemIds(result).Order());
    }

    [Fact]
    public async Task GetProducts_SearchByName_InCurrentLanguage()
    {
        var result = await _fx.CreateService().GetProductsAsync(new ProductQuery { Culture = "ru", Search = "миш" });

        Assert.Equal(Ids("SKU-001"), ItemIds(result));
    }

    [Fact]
    public async Task GetProducts_SearchByArmenianName_WorksInOtherLanguages()
    {
        var result = await _fx.CreateService().GetProductsAsync(new ProductQuery { Culture = "ru", Search = "Գնդակ" });

        Assert.Equal(Ids("SKU-004"), ItemIds(result));
    }

    [Fact]
    public async Task GetProducts_SearchByBrand_IsCaseInsensitive_AndSkipsArchived()
    {
        var result = await _fx.CreateService().GetProductsAsync(new ProductQuery { Search = "alpha" });

        Assert.Equal(Ids("SKU-001", "SKU-003", "SKU-007").Order(), ItemIds(result).Order());
    }

    [Fact]
    public async Task GetProducts_SearchBySku()
    {
        var result = await _fx.CreateService().GetProductsAsync(new ProductQuery { Search = "SKU-004" });

        Assert.Equal(Ids("SKU-004"), ItemIds(result));
    }

    [Fact]
    public async Task GetProducts_SortPriceAsc_UsesFinalPrice()
    {
        var result = await _fx.CreateService().GetProductsAsync(new ProductQuery { Sort = ProductSort.PriceAsc });

        Assert.Equal(Ids("SKU-004", "SKU-007", "SKU-001", "SKU-002", "SKU-003"), ItemIds(result));
    }

    [Fact]
    public async Task GetProducts_SortPriceDesc_UsesFinalPrice()
    {
        var result = await _fx.CreateService().GetProductsAsync(new ProductQuery { Sort = ProductSort.PriceDesc });

        Assert.Equal(Ids("SKU-003", "SKU-001", "SKU-002", "SKU-007", "SKU-004"), ItemIds(result));
    }

    [Fact]
    public async Task GetProducts_SortNewest()
    {
        var result = await _fx.CreateService().GetProductsAsync(new ProductQuery { Sort = ProductSort.Newest });

        Assert.Equal(Ids("SKU-007", "SKU-001", "SKU-002", "SKU-004", "SKU-003"), ItemIds(result));
    }

    [Fact]
    public async Task GetProducts_SortRecommended_InStockFirst_ThenPinned_ThenNewest()
    {
        var result = await _fx.CreateService().GetProductsAsync(new ProductQuery { Sort = ProductSort.Recommended });

        Assert.Equal(Ids("SKU-003", "SKU-007", "SKU-001", "SKU-004", "SKU-002"), ItemIds(result));
    }

    [Fact]
    public async Task GetProducts_OnlyNew_ReturnsRecentAndPinned()
    {
        var result = await _fx.CreateService().GetProductsAsync(new ProductQuery { OnlyNew = true });

        Assert.Equal(Ids("SKU-001", "SKU-002", "SKU-003", "SKU-007").Order(), ItemIds(result).Order());
    }

    [Fact]
    public async Task GetProducts_Paging_ReturnsRequestedPage()
    {
        var service = _fx.CreateService(pageSize: 2);

        var page2 = await service.GetProductsAsync(new ProductQuery { Sort = ProductSort.Newest, Page = 2 });

        Assert.Equal(Ids("SKU-002", "SKU-004"), ItemIds(page2));
        Assert.Equal(5, page2.TotalCount);
        Assert.Equal(3, page2.TotalPages);
        Assert.Equal(2, page2.Page);
    }

    [Fact]
    public async Task GetProducts_PageBeyondLast_ReturnsLastPage()
    {
        var service = _fx.CreateService(pageSize: 2);

        var result = await service.GetProductsAsync(new ProductQuery { Sort = ProductSort.Newest, Page = 99 });

        Assert.Equal(3, result.Page);
        Assert.Equal(Ids("SKU-003"), ItemIds(result));
    }

    [Fact]
    public async Task GetProducts_MapsPricesFlagsNamesAndImage()
    {
        var result = await _fx.CreateService().GetProductsAsync(new ProductQuery { Culture = "ru" });

        var doll = result.Items.Single(i => i.Id == _fx.ProductId("SKU-003"));
        Assert.Equal("Кукла", doll.Name);
        Assert.Equal(15000m, doll.Price);
        Assert.Equal(13500m, doll.FinalPrice);
        Assert.Equal(10, doll.DiscountPercent);
        Assert.True(doll.IsNew);
        Assert.True(doll.InStock);

        var car = result.Items.Single(i => i.Id == _fx.ProductId("SKU-002"));
        Assert.False(car.InStock);

        var ball = result.Items.Single(i => i.Id == _fx.ProductId("SKU-004"));
        Assert.Equal("Գնդակ", ball.Name);
        Assert.Equal("Խաղ Բ", ball.CategoryName);
        Assert.False(ball.IsNew);

        var bear = result.Items.Single(i => i.Id == _fx.ProductId("SKU-001"));
        Assert.Equal("/img/p1-first.svg", bear.ImagePath);
    }

    [Fact]
    public async Task GetProduct_ReturnsDetails_WithOrderedImagesAndAgeGroups()
    {
        var product = await _fx.CreateService().GetProductAsync(_fx.ProductId("SKU-002"), "en");

        Assert.NotNull(product);
        Assert.Equal("Car", product.Name);
        Assert.Equal("SKU-002", product.Sku);
        Assert.Equal("Description SKU-002", product.Description);
        Assert.Equal(10000m, product.FinalPrice);
        Assert.False(product.InStock);
        Assert.Equal(new[] { "0–2 yrs", "3–5 yrs" }, product.AgeGroups.ToArray());

        var bear = await _fx.CreateService().GetProductAsync(_fx.ProductId("SKU-001"), "en");
        Assert.NotNull(bear);
        Assert.Equal(new[] { "/img/p1-first.svg", "/img/p1-second.svg" }, bear.Images.ToArray());
    }

    [Fact]
    public async Task GetProduct_MissingLanguage_FallsBackToArmenian()
    {
        var product = await _fx.CreateService().GetProductAsync(_fx.ProductId("SKU-004"), "ru");

        Assert.NotNull(product);
        Assert.Equal("Գնդակ", product.Name);
        Assert.Equal("Նկարագրություն SKU-004", product.Description);
    }

    [Fact]
    public async Task GetProduct_ArchivedInactiveCategoryOrUnknown_ReturnsNull()
    {
        var service = _fx.CreateService();

        Assert.Null(await service.GetProductAsync(_fx.ProductId("SKU-005"), "hy"));
        Assert.Null(await service.GetProductAsync(_fx.ProductId("SKU-006"), "hy"));
        Assert.Null(await service.GetProductAsync(int.MaxValue, "hy"));
    }

    [Fact]
    public async Task GetProductsByIds_KeepsTheGivenOrder_AndSkipsUnsaleableProducts()
    {
        var ids = new[]
        {
            _fx.ProductId("SKU-003"),
            _fx.ProductId("SKU-005"),   // archived
            _fx.ProductId("SKU-006"),   // inactive category
            _fx.ProductId("SKU-001"),
            int.MaxValue                // does not exist
        };

        var items = await _fx.CreateService().GetProductsByIdsAsync(ids, "en");

        Assert.Equal(Ids("SKU-003", "SKU-001"), items.Select(i => i.Id).ToArray());
        Assert.Equal("Doll", items[0].Name);
    }

    [Fact]
    public async Task GetProductsByIds_EmptyList_ReturnsNothing()
    {
        var items = await _fx.CreateService().GetProductsByIdsAsync(Array.Empty<int>(), "hy");

        Assert.Empty(items);
    }

    [Fact]
    public async Task GetNewArrivals_ReturnsRequestedCount_PinnedFirst()
    {
        var items = await _fx.CreateService().GetNewArrivalsAsync("hy", 2);

        Assert.Equal(Ids("SKU-003", "SKU-007"), items.Select(i => i.Id).ToArray());
    }
}

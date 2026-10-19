using Microsoft.AspNetCore.Mvc;
using Shop.Core.Catalog;
using Shop.Web.Models;

namespace Shop.Web.Controllers;

public class ProductsController : Controller
{
    private readonly ICatalogService _catalog;

    public ProductsController(ICatalogService catalog)
    {
        _catalog = catalog;
    }

    public async Task<IActionResult> Index(
        int? category, int? age, string? q, bool isNew = false, decimal? priceFrom = null, decimal? priceTo = null,
        ProductSort sort = ProductSort.Recommended, int page = 1)
    {
        var culture = CurrentCulture.Code;
        var (from, to) = PriceRange.Normalize(priceFrom, priceTo);

        var result = await _catalog.GetProductsAsync(new ProductQuery
        {
            Culture = culture,
            CategoryId = category,
            AgeGroupId = age,
            Search = q,
            OnlyNew = isNew,
            PriceFrom = from,
            PriceTo = to,
            Sort = sort,
            Page = page
        });

        var model = new ProductsIndexViewModel
        {
            Result = result,
            Categories = await _catalog.GetCategoriesAsync(culture),
            AgeGroups = await _catalog.GetAgeGroupsAsync(culture),
            CategoryId = category,
            AgeGroupId = age,
            Search = q,
            OnlyNew = isNew,
            PriceFrom = from,
            PriceTo = to,
            Sort = sort
        };

        return View(model);
    }

    public async Task<IActionResult> Details(int id)
    {
        var product = await _catalog.GetProductAsync(id, CurrentCulture.Code);

        if (product is null)
        {
            return NotFound();
        }

        return View(product);
    }
}

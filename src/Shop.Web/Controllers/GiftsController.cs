using Microsoft.AspNetCore.Mvc;
using Shop.Core.Catalog;
using Shop.Web.Models;

namespace Shop.Web.Controllers;

// The gift finder: age, budget (From / To) and kinds of toys, answered on one page. The answers are in the address (GET),
// so a result can be shared or bookmarked, and nothing is stored about the visitor.
public class GiftsController : Controller
{
    private const int ResultCount = 24;

    private readonly ICatalogService _catalog;

    public GiftsController(ICatalogService catalog)
    {
        _catalog = catalog;
    }

    public async Task<IActionResult> Index(int? age, int[]? category, decimal? priceFrom, decimal? priceTo, bool find = false)
    {
        var culture = CurrentCulture.Code;
        var ageGroups = await _catalog.GetAgeGroupsAsync(culture);
        var categories = await _catalog.GetCategoriesAsync(culture);

        // Only things that exist are used; anything else typed into the address is ignored.
        int? ageId = ageGroups.Any(a => a.Id == age) ? age : null;
        var categoryIds = categories.Where(c => category?.Contains(c.Id) == true).Select(c => c.Id).ToList();
        var (from, to) = PriceRange.Normalize(priceFrom, priceTo);

        var asked = find || ageId.HasValue || categoryIds.Count > 0 || from.HasValue || to.HasValue;

        IReadOnlyList<ProductListItem> results = Array.Empty<ProductListItem>();
        if (asked)
        {
            results = await _catalog.FindAsync(new ProductQuery
            {
                Culture = culture,
                AgeGroupId = ageId,
                CategoryIds = categoryIds,
                PriceFrom = from,
                PriceTo = to,
                OnlyInStock = true,
                Sort = ProductSort.BestSelling
            }, ResultCount);
        }

        return View(new GiftsViewModel
        {
            AgeGroups = ageGroups,
            Categories = categories,
            AgeGroupId = ageId,
            CategoryIds = categoryIds,
            PriceFrom = from,
            PriceTo = to,
            Searched = asked,
            Results = results
        });
    }
}

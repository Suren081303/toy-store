using Microsoft.AspNetCore.Mvc;
using Shop.Core.Catalog;

namespace Shop.Web.Controllers;

public class CategoriesController : Controller
{
    private readonly ICatalogService _catalog;

    public CategoriesController(ICatalogService catalog)
    {
        _catalog = catalog;
    }

    public async Task<IActionResult> Index()
    {
        return View(await _catalog.GetCategoriesAsync(CurrentCulture.Code));
    }
}

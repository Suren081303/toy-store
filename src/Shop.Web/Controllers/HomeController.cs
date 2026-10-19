using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Shop.Core.Catalog;
using Shop.Web.Legal;
using Shop.Web.Models;

namespace Shop.Web.Controllers;

public class HomeController : Controller
{
    private const int HomeNewArrivalsCount = 4;

    private readonly ICatalogService _catalog;
    private readonly LegalPages _legal;

    public HomeController(ICatalogService catalog, LegalPages legal)
    {
        _catalog = catalog;
        _legal = legal;
    }

    public async Task<IActionResult> Index()
    {
        var culture = CurrentCulture.Code;

        var model = new HomeViewModel
        {
            Categories = await _catalog.GetCategoriesAsync(culture),
            AgeGroups = await _catalog.GetAgeGroupsAsync(culture),
            NewArrivals = await _catalog.GetNewArrivalsAsync(culture, HomeNewArrivalsCount)
        };

        return View(model);
    }

    public async Task<IActionResult> Privacy()
    {
        return View(await _legal.BuildAsync());
    }

    // Shown for empty error responses such as 404 (see UseStatusCodePagesWithReExecute in Program.cs).
    // The page keeps the original status code and picks a fitting message.
    [Route("not-found")]
    public IActionResult StatusPage()
    {
        var status = HttpContext.Features.Get<IStatusCodeReExecuteFeature>()?.OriginalStatusCode ?? StatusCodes.Status404NotFound;
        Response.StatusCode = status;

        var (title, text) = status switch
        {
            StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden => ("Msg_AccessDeniedTitle", "Msg_AccessDeniedText"),
            StatusCodes.Status429TooManyRequests => ("Msg_TooManyTitle", "Msg_TooManyText"),
            >= 500 => ("Msg_ErrorTitle", "Msg_ErrorText"),
            _ => ("NotFoundTitle", "NotFoundText")
        };

        ViewData["StatusCode"] = status == StatusCodes.Status404NotFound ? "404" : status.ToString();
        ViewData["TitleKey"] = title;
        ViewData["TextKey"] = text;
        return View("NotFound");
    }

    // The page for an unhandled error (UseExceptionHandler("/error") in Program.cs). It is also reachable as /hy|ru|en/error.
    // It does not use the shop's layout, so it still works when the database is down.
    [Route("error")]
    [Route("{culture:regex(^(hy|ru|en)$)}/error")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        Response.StatusCode = StatusCodes.Status500InternalServerError;
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}

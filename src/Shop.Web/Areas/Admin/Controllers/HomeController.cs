using Microsoft.AspNetCore.Mvc;
using Shop.Core.Admin;

namespace Shop.Web.Areas.Admin.Controllers;

public class HomeController : AdminControllerBase
{
    private readonly IAdminDashboardService _dashboard;

    public HomeController(IAdminDashboardService dashboard)
    {
        _dashboard = dashboard;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        return View(await _dashboard.GetAsync(CurrentCulture.Code));
    }
}

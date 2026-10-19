using Microsoft.AspNetCore.Mvc;
using Shop.Core.Admin;

namespace Shop.Web.Areas.Admin.Controllers;

public class HomeController : AdminControllerBase
{
    private readonly IAdminOrderService _orders;

    public HomeController(IAdminOrderService orders)
    {
        _orders = orders;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        return View(await _orders.GetDashboardAsync());
    }
}

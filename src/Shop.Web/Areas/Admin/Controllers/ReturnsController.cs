using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Shop.Core.Admin;
using Shop.Core.Orders;
using Shop.Web.Areas.Admin.Models;

namespace Shop.Web.Areas.Admin.Controllers;

public class ReturnsController : AdminControllerBase
{
    private readonly IAdminReturnService _returns;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public ReturnsController(IAdminReturnService returns, IHtmlLocalizer<SharedResource> localizer)
    {
        _returns = returns;
        _l = localizer;
    }

    [HttpGet]
    public async Task<IActionResult> Index(ReturnStatus? status, int page = 1)
    {
        return View(new AdminReturnsViewModel(status, await _returns.ListAsync(status, page)));
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var detail = await _returns.GetAsync(id);
        if (detail is null)
        {
            return NotFound();
        }

        return View(detail);
    }

    [HttpPost]
    public async Task<IActionResult> Change(int id, ReturnStatus status, string? note, bool restock)
    {
        var result = await _returns.ChangeStatusAsync(id, status, note, restock, AdminId);

        switch (result)
        {
            case ChangeReturnResult.Changed:
                Success(_l["Admin_Msg_ReturnChanged"].Value);
                break;
            case ChangeReturnResult.NotFound:
                return NotFound();
            default:
                Failure(_l["Admin_Msg_NotAllowed"].Value);
                break;
        }

        return RedirectToAction(nameof(Details), new { id });
    }
}

using Microsoft.AspNetCore.Mvc;
using Shop.Core.Admin;
using Shop.Web.Areas.Admin.Models;

namespace Shop.Web.Areas.Admin.Controllers;

public class AuditController : AdminControllerBase
{
    private readonly IAdminAuditService _audit;

    public AuditController(IAdminAuditService audit)
    {
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> Index(AuditFilter filter)
    {
        return View(new AdminAuditViewModel(filter, await _audit.ListAsync(filter), await _audit.EntityTypesAsync()));
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Shop.Core.Admin;
using Shop.Web.Areas.Admin.Models;

namespace Shop.Web.Areas.Admin.Controllers;

public class AgeGroupsController : AdminControllerBase
{
    private readonly IAdminAgeGroupService _groups;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public AgeGroupsController(IAdminAgeGroupService groups, IHtmlLocalizer<SharedResource> localizer)
    {
        _groups = groups;
        _l = localizer;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        return View(await _groups.ListAsync(CurrentCulture.Code));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int? id)
    {
        if (id is null)
        {
            return View(new AdminAgeGroupViewModel { SortOrder = (await _groups.ListAsync(CurrentCulture.Code)).Count + 1 });
        }

        var form = await _groups.GetAsync(id.Value);
        if (form is null)
        {
            return NotFound();
        }

        return View(new AdminAgeGroupViewModel
        {
            Id = form.Id,
            NameHy = form.NameHy,
            NameRu = form.NameRu,
            NameEn = form.NameEn,
            SortOrder = form.SortOrder,
            IsActive = form.IsActive
        });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(AdminAgeGroupViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var id = await _groups.SaveAsync(new AdminAgeGroupForm
        {
            Id = model.Id,
            NameHy = model.NameHy,
            NameRu = model.NameRu ?? string.Empty,
            NameEn = model.NameEn ?? string.Empty,
            SortOrder = model.SortOrder,
            IsActive = model.IsActive
        }, AdminId);

        if (id is null)
        {
            return NotFound();
        }

        Success(_l["Admin_Msg_Saved"].Value);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _groups.DeleteAsync(id, AdminId);

        switch (result)
        {
            case DeleteStatus.Deleted:
                Success(_l["Admin_Msg_Deleted"].Value);
                break;
            case DeleteStatus.NotFound:
                return NotFound();
            default:
                Failure(_l["Admin_Msg_AgeGroupInUse"].Value);
                break;
        }

        return RedirectToAction(nameof(Index));
    }
}

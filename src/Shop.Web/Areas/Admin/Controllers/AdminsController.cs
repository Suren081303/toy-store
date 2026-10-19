using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Shop.Core.Admin;
using Shop.Web.Areas.Admin.Models;

namespace Shop.Web.Areas.Admin.Controllers;

// Who has the Admin role. All admins can do everything; there is only this one role.
public class AdminsController : AdminControllerBase
{
    private readonly IAdminAccountService _accounts;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public AdminsController(IAdminAccountService accounts, IHtmlLocalizer<SharedResource> localizer)
    {
        _accounts = accounts;
        _l = localizer;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        return View(new AdminAccountsViewModel(await _accounts.ListAsync(), AdminId));
    }

    [HttpPost]
    public async Task<IActionResult> Add(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !new EmailAddressAttribute().IsValid(email))
        {
            Failure(_l["Val_Email"].Value);
            return RedirectToAction(nameof(Index));
        }

        var result = await _accounts.GrantAsync(email, AdminId);

        switch (result)
        {
            case GrantAdminResult.Granted:
                Success(_l.GetString("Admin_Adm_Granted", email.Trim()).Value);
                break;
            case GrantAdminResult.AlreadyAdmin:
                Failure(_l["Admin_Adm_AlreadyAdmin"].Value);
                break;
            case GrantAdminResult.NotConfirmed:
                Failure(_l["Admin_Adm_NotConfirmed"].Value);
                break;
            default:
                Failure(_l["Admin_Adm_NotFound"].Value);
                break;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Remove(string id)
    {
        var result = await _accounts.RevokeAsync(id, AdminId);

        switch (result)
        {
            case RevokeAdminResult.Revoked:
                Success(_l["Admin_Adm_Revoked"].Value);
                break;
            case RevokeAdminResult.NotFound:
                return NotFound();
            case RevokeAdminResult.Yourself:
                Failure(_l["Admin_Adm_Yourself"].Value);
                break;
            case RevokeAdminResult.ConfiguredAdmin:
                Failure(_l["Admin_Adm_Configured"].Value);
                break;
            case RevokeAdminResult.LastAdmin:
                Failure(_l["Admin_Adm_Last"].Value);
                break;
            default:
                Failure(_l["Admin_Msg_NotAllowed"].Value);
                break;
        }

        return RedirectToAction(nameof(Index));
    }
}

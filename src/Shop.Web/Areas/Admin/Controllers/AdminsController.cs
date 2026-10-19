using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Shop.Core.Admin;
using Shop.Web.Areas.Admin.Models;

namespace Shop.Web.Areas.Admin.Controllers;

// Who has the Admin role. All admins can do everything; there is only this one role.
// Admin accounts are made here (not by promoting a customer): the email is confirmed at once and the account is for the panel only.
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

    [HttpGet]
    public IActionResult Create()
    {
        return View("Form", new AdminAccountForm());
    }

    [HttpPost]
    public async Task<IActionResult> Create(AdminAccountForm form)
    {
        // A new admin needs a password to sign in with. (On edit an empty password means "keep it".)
        if (string.IsNullOrEmpty(form.Password))
        {
            ModelState.AddModelError(nameof(form.Password), _l["Val_Required"].Value);
        }

        if (!ModelState.IsValid)
        {
            return View("Form", form);
        }

        var result = await _accounts.CreateAsync(form.ToInput(), AdminId);

        if (result.Status != AdminSaveStatus.Saved)
        {
            ShowRefusal(result);
            return View("Form", form);
        }

        Success(_l.GetString("Admin_Adm_Created", form.Email.Trim()).Value);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(string id)
    {
        var row = await _accounts.GetAsync(id);

        return row is null ? NotFound() : View("Form", AdminAccountForm.From(row, AdminId));
    }

    [HttpPost]
    public async Task<IActionResult> Edit(AdminAccountForm form)
    {
        if (form.Id is null)
        {
            return NotFound();
        }

        // The "main admin" and "you" marks are not posted; read them again for the page.
        var row = await _accounts.GetAsync(form.Id);
        if (row is null)
        {
            return NotFound();
        }

        form.IsConfigured = row.IsConfigured;
        form.IsYou = row.Id == AdminId;

        if (!ModelState.IsValid)
        {
            return View("Form", form);
        }

        var result = await _accounts.UpdateAsync(form.Id, form.ToInput(), AdminId);

        if (result.Status == AdminSaveStatus.NotFound || result.Status == AdminSaveStatus.NotAnAdmin)
        {
            return NotFound();
        }

        if (result.Status != AdminSaveStatus.Saved)
        {
            ShowRefusal(result);
            return View("Form", form);
        }

        Success(_l["Admin_Adm_Updated"].Value);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(string id)
    {
        var result = await _accounts.DeleteAsync(id, AdminId);

        switch (result)
        {
            case DeleteAdminResult.Deleted:
                // An admin who deleted their own account is signed out at once.
                if (id == AdminId)
                {
                    await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                    return Redirect("/" + CurrentCulture.Code);
                }

                Success(_l["Admin_Adm_Deleted"].Value);
                break;
            case DeleteAdminResult.NotFound:
            case DeleteAdminResult.NotAnAdmin:
                return NotFound();
            case DeleteAdminResult.ConfiguredAdmin:
                Failure(_l["Admin_Adm_Configured"].Value);
                break;
            case DeleteAdminResult.LastAdmin:
                Failure(_l["Admin_Adm_Last"].Value);
                break;
        }

        return RedirectToAction(nameof(Index));
    }

    // Puts what Identity or the rules said next to the field it is about.
    private void ShowRefusal(AdminSaveResult result)
    {
        switch (result.Status)
        {
            case AdminSaveStatus.EmailTaken:
                ModelState.AddModelError("Email", _l["Admin_Adm_EmailTaken"].Value);
                break;
            case AdminSaveStatus.ConfiguredEmail:
                ModelState.AddModelError("Email", _l["Admin_Adm_ConfiguredEmail"].Value);
                break;
            case AdminSaveStatus.WeakPassword:
                foreach (var error in result.Errors ?? [])
                {
                    ModelState.AddModelError("Password", error);
                }

                break;
        }
    }
}

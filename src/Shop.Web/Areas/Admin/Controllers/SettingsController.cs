using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Shop.Core.Admin;
using Shop.Web.Areas.Admin.Models;

namespace Shop.Web.Areas.Admin.Controllers;

public class SettingsController : AdminControllerBase
{
    private readonly IAdminSettingsService _settings;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public SettingsController(IAdminSettingsService settings, IHtmlLocalizer<SharedResource> localizer)
    {
        _settings = settings;
        _l = localizer;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        return View(await BuildAsync(null));
    }

    [HttpPost]
    public async Task<IActionResult> Save(AdminSettingsViewModel form)
    {
        if (!ModelState.IsValid)
        {
            return View(nameof(Index), await BuildAsync(form));
        }

        await _settings.SaveSettingsAsync(new AdminSettingsInput
        {
            DeliveryFee = form.DeliveryFee,
            FreeDeliveryFrom = form.FreeDeliveryFrom,
            DeliveryDaysAhead = form.DeliveryDaysAhead,
            DeliveryFirstDayOffset = form.DeliveryFirstDayOffset,
            ReturnWindowDays = form.ReturnWindowDays,
            BankName = form.BankName,
            BankAccount = form.BankAccount,
            BankRecipient = form.BankRecipient
        }, AdminId);

        Success(_l["Admin_Msg_Saved"].Value);
        return RedirectToAction(nameof(Index));
    }

    // Adds a slot (id empty) or changes one.
    [HttpPost]
    public async Task<IActionResult> SaveSlot(int? id, string? label, int sortOrder, bool isActive)
    {
        label = label?.Trim();
        if (string.IsNullOrEmpty(label) || label.Length > 50 || sortOrder is < 0 or > 1000)
        {
            Failure(_l["Admin_Msg_BadSlot"].Value);
            return RedirectToAction(nameof(Index));
        }

        var result = await _settings.SaveSlotAsync(id, label, sortOrder, isActive, AdminId);

        switch (result)
        {
            case SaveSlotResult.Saved:
                Success(_l["Admin_Msg_Saved"].Value);
                break;
            case SaveSlotResult.NotFound:
                return NotFound();
            default:
                Failure(_l["Admin_Msg_LastSlot"].Value);
                break;
        }

        return RedirectToAction(nameof(Index));
    }

    // The page: what was just typed (when it was refused), or what is saved. The time slots are always the saved ones.
    private async Task<AdminSettingsViewModel> BuildAsync(AdminSettingsViewModel? typed)
    {
        var current = await _settings.GetAsync();
        var form = typed ?? new AdminSettingsViewModel
        {
            DeliveryFee = current.Settings.DeliveryFee,
            FreeDeliveryFrom = current.Settings.FreeDeliveryFrom,
            DeliveryDaysAhead = current.Settings.DeliveryDaysAhead,
            DeliveryFirstDayOffset = current.Settings.DeliveryFirstDayOffset,
            ReturnWindowDays = current.Settings.ReturnWindowDays,
            BankName = current.Settings.BankName,
            BankAccount = current.Settings.BankAccount,
            BankRecipient = current.Settings.BankRecipient
        };

        form.Slots = current.Slots;
        return form;
    }
}

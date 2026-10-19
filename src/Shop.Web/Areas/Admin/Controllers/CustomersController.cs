using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Shop.Core.Admin;
using Shop.Web.Areas.Admin.Models;

namespace Shop.Web.Areas.Admin.Controllers;

// The customers: a list with search, and for each customer a panel with block / unblock, a personal discount and a message.
// There is no page for one customer: the panel is part of the list, and every action comes back to the same list with the panel open.
public class CustomersController : AdminControllerBase
{
    private readonly IAdminCustomerService _customers;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public CustomersController(IAdminCustomerService customers, IHtmlLocalizer<SharedResource> localizer)
    {
        _customers = customers;
        _l = localizer;
    }

    [HttpGet]
    public async Task<IActionResult> Index(AdminCustomerFilter filter)
    {
        return View(new AdminCustomersViewModel(filter, await _customers.ListAsync(filter)));
    }

    [HttpPost]
    public async Task<IActionResult> Block(string id, string? email, string? returnUrl)
    {
        var result = await _customers.BlockAsync(id, AdminId);

        return Finish(result, id, returnUrl, _l.GetString("Admin_Cust_Blocked", email ?? string.Empty).Value);
    }

    [HttpPost]
    public async Task<IActionResult> Unblock(string id, string? email, string? returnUrl)
    {
        var result = await _customers.UnblockAsync(id, AdminId);

        return Finish(result, id, returnUrl, _l.GetString("Admin_Cust_Unblocked", email ?? string.Empty).Value);
    }

    // percent empty or 0: remove the discount.
    [HttpPost]
    public async Task<IActionResult> Discount(string id, int? percent, DateOnly? until, string? returnUrl)
    {
        var result = await _customers.SetDiscountAsync(id, percent, until, AdminId);
        var removed = percent is null or 0;

        return Finish(result, id, returnUrl, _l[removed ? "Admin_Cust_DiscountRemoved" : "Admin_Cust_DiscountSaved"].Value);
    }

    [HttpPost]
    public async Task<IActionResult> Message(string id, string? email, string? subject, string? body, string? returnUrl)
    {
        var result = await _customers.SendMessageAsync(id, subject ?? string.Empty, body ?? string.Empty, CurrentCulture.Code, AdminId);

        return Finish(result, id, returnUrl, _l.GetString("Admin_Cust_MessageQueued", email ?? string.Empty).Value);
    }

    // The green or red bar, then back to the list with this customer's panel open.
    private IActionResult Finish(CustomerActionStatus result, string id, string? returnUrl, string successText)
    {
        switch (result)
        {
            case CustomerActionStatus.Done:
                Success(successText);
                break;
            case CustomerActionStatus.NotFound:
            case CustomerActionStatus.NotACustomer:
                return NotFound();
            default:
                Failure(_l["Admin_Cust_" + result].Value);
                break;
        }

        var list = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl.Split('#')[0]
            : Url.Action(nameof(Index))!;

        return LocalRedirect(list + "#more-" + id);
    }
}

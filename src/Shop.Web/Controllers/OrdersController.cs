using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Shop.Core.Orders;
using Shop.Web.Identity;
using Shop.Web.Models;

namespace Shop.Web.Controllers;

public class OrdersController : Controller
{
    private readonly IOrderService _orders;
    private readonly OrderAccess _access;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public OrdersController(IOrderService orders, OrderAccess access, IHtmlLocalizer<SharedResource> localizer)
    {
        _orders = orders;
        _access = access;
        _l = localizer;
    }

    // The signed-in customer's orders.
    [Authorize]
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        return View(await _orders.ListForUserAsync(UserId!));
    }

    // One order: for its owner (signed in), or for anyone who has the secret key from the confirmation email.
    [HttpGet]
    public async Task<IActionResult> Details(int number, string? key)
    {
        var order = await ResolveAsync(number, key);

        if (order is null)
        {
            return NotFound();
        }

        ViewData["Key"] = key;
        return View(order);
    }

    // Guests without the email link: order number + the email used at checkout.
    [HttpGet]
    public IActionResult Lookup()
    {
        return View(new LookupViewModel());
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Account)]
    public async Task<IActionResult> Lookup(LookupViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var order = await _orders.FindByEmailAsync(model.Number!.Value, model.Email);

        if (order is null)
        {
            // The same message whether the number or the email was wrong.
            ModelState.AddModelError(string.Empty, _l["Lookup_NotFound"].Value);
            return View(model);
        }

        return RedirectToAction(nameof(Details), new { number = order.Number, key = order.AccessKey });
    }

    [HttpPost]
    public async Task<IActionResult> RequestReturn(int number, string? key, string? reason)
    {
        var order = await ResolveAsync(number, key);

        if (order is null)
        {
            return NotFound();
        }

        var result = await _orders.RequestReturnAsync(number, reason ?? string.Empty);

        TempData["OrderMessage"] = result switch
        {
            ReturnRequestResult.Created => _l["Return_Created"].Value,
            ReturnRequestResult.InvalidReason => _l["Return_InvalidReason"].Value,
            ReturnRequestResult.WindowClosed => _l["Return_WindowClosed"].Value,
            ReturnRequestResult.AlreadyRequested => _l["Return_AlreadyRequested"].Value,
            _ => _l["Return_NotPossible"].Value
        };

        return RedirectToAction(nameof(Details), new { number, key });
    }

    private string? UserId => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private Task<OrderSummary?> ResolveAsync(int number, string? key) => _access.ResolveAsync(User, number, key);
}

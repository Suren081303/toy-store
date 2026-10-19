using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Shop.Core.Carts;

namespace Shop.Web.Controllers;

public class CartController : Controller
{
    private readonly ICartService _cart;
    private readonly CartOwnerResolver _owners;
    private readonly GuestCookies _cookies;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public CartController(
        ICartService cart,
        CartOwnerResolver owners,
        GuestCookies cookies,
        IHtmlLocalizer<SharedResource> localizer)
    {
        _cart = cart;
        _owners = owners;
        _cookies = cookies;
        _l = localizer;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var cart = await _cart.GetAsync(_owners.Resolve(HttpContext), CurrentCulture.Code);
        return View(cart);
    }

    // Called by the "Add to cart" buttons. The page script asks for JSON; without the script the form posts normally and lands on the cart page.
    [HttpPost]
    public async Task<IActionResult> Add(int productId, int quantity = 1)
    {
        var result = await _cart.AddAsync(_owners.Resolve(HttpContext), productId, quantity);

        if (result.NewGuestCartId is Guid guestCartId)
        {
            _cookies.WriteCartId(Request, Response, guestCartId);
        }

        var message = Describe(result);

        if (WantsJson())
        {
            return Json(new
            {
                status = result.Status.ToString(),
                count = result.Count,
                quantity = result.Quantity,
                limit = result.Limit,
                message
            });
        }

        TempData["CartMessage"] = message;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> SetQuantity(int productId, int quantity)
    {
        var result = await _cart.SetQuantityAsync(_owners.Resolve(HttpContext), productId, quantity);

        if (result.Status is CartChangeStatus.LimitReached or CartChangeStatus.NotAvailable)
        {
            TempData["CartMessage"] = Describe(result);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Remove(int productId)
    {
        await _cart.RemoveAsync(_owners.Resolve(HttpContext), productId);
        return RedirectToAction(nameof(Index));
    }

    private string Describe(CartChange result)
    {
        return result.Status switch
        {
            CartChangeStatus.Added => _l["Cart_AddedToast"].Value,
            CartChangeStatus.LimitReached => _l.GetString("Cart_LimitReached", result.Limit).Value,
            CartChangeStatus.TooManyLines => _l["Cart_TooMany"].Value,
            _ => _l["Cart_NotAvailable"].Value
        };
    }

    private bool WantsJson() => Request.Headers.Accept.ToString().Contains("application/json");
}

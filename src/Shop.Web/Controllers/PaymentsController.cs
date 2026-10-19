using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Microsoft.Extensions.Options;
using Shop.Core.Orders;
using Shop.Core.Payments;
using Shop.Web.Models;
using Shop.Web.Payments;

namespace Shop.Web.Controllers;

public class PaymentsController : Controller
{
    private readonly IPaymentService _payments;
    private readonly OrderAccess _access;
    private readonly PaymentOptions _options;
    private readonly TimeProvider _time;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public PaymentsController(
        IPaymentService payments,
        OrderAccess access,
        IOptions<PaymentOptions> options,
        TimeProvider time,
        IHtmlLocalizer<SharedResource> localizer)
    {
        _payments = payments;
        _access = access;
        _options = options.Value;
        _time = time;
        _l = localizer;
    }

    // "Pay now": starts a new payment attempt and sends the customer to the bank.
    [HttpPost]
    public async Task<IActionResult> Start(int number, string? key)
    {
        var order = await _access.ResolveAsync(User, number, key);
        if (order is null)
        {
            return NotFound();
        }

        var result = await _payments.StartCardPaymentAsync(
            number, CurrentCulture.Code, Url.PaymentReturn(Request, number, order.AccessKey), Url.PaymentCallback(Request));

        if (result.Status == StartPaymentStatus.Started)
        {
            return Redirect(result.RedirectUrl!);
        }

        TempData["OrderMessage"] = _l["Pay_NotPossible"].Value;
        return RedirectToAction("Details", "Orders", new { number, key });
    }

    // Where the bank sends the customer back. The result comes from our own database (the bank's signed answer),
    // never from this address, because anyone can type an address.
    [HttpGet]
    public async Task<IActionResult> Return(int number, string? key)
    {
        var order = await _access.ResolveAsync(User, number, key);
        if (order is null)
        {
            return NotFound();
        }

        if (order.Status is OrderStatus.Ordered or OrderStatus.Packed or OrderStatus.Delivered)
        {
            return RedirectToAction("Done", "Checkout", new { number, key = order.AccessKey });
        }

        if (order.Status == OrderStatus.Cancelled)
        {
            return View(new PaymentReturnViewModel(order, order.PaymentStatus == PaymentStatus.Paid ? "Late" : "Expired", false));
        }

        var attempt = await _payments.GetLatestAttemptAsync(number);
        var state = attempt switch
        {
            PaymentAttemptStatus.Failed => "Failed",
            PaymentAttemptStatus.Cancelled => "Cancelled",
            _ => "Waiting"
        };

        var canPay = state != "Waiting"
            && _payments.CardPaymentAvailable
            && order.PaymentMethod == PaymentMethod.Card
            && !PaymentRules.IsExpired(order.PaymentMethod, order.CreatedAt, _time.GetUtcNow().UtcDateTime, _options);

        return View(new PaymentReturnViewModel(order, state, canPay));
    }

    // The bank's server sends its signed answer here (no cookies, no anti-forgery token: the signature is the proof).
    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Callback()
    {
        // The refusals have a short text body on purpose: an empty 4xx would be re-run as the friendly "not found" page,
        // which is meant for visitors, not for the bank's server.
        if (!Request.HasFormContentType)
        {
            return BadRequest("form expected");
        }

        var fields = Request.Form.ToDictionary(f => f.Key, f => f.Value.ToString());
        var result = await _payments.HandleCallbackAsync(fields);

        return result.Status switch
        {
            CallbackStatus.InvalidSignature => BadRequest("invalid signature"),
            CallbackStatus.AmountMismatch => BadRequest("amount mismatch"),
            CallbackStatus.UnknownReference => NotFound("unknown reference"),
            _ => Ok()
        };
    }
}

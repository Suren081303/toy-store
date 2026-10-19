using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Shop.Core.Payments;
using Shop.Web.Models;
using Shop.Web.Payments;

namespace Shop.Web.Controllers;

// The pretend bank's payment page (Development only). It does what a real bank does: shows the amount, lets the
// customer pay or refuse, sends the shop a signed answer, and sends the customer back.
public class FakeBankController : Controller
{
    private readonly IPaymentService _payments;
    private readonly PaymentOptions _options;
    private readonly IPaymentProvider? _provider;

    public FakeBankController(
        IPaymentService payments,
        IOptions<PaymentOptions> options,
        IPaymentProvider? provider = null)
    {
        _payments = payments;
        _options = options.Value;
        _provider = provider;
    }

    private bool IsActive => _provider is FakePaymentProvider;

    [HttpGet]
    public async Task<IActionResult> Index(string reference, string? returnUrl)
    {
        var attempt = IsActive ? await _payments.GetAttemptAsync(reference) : null;
        if (attempt is null)
        {
            return NotFound();
        }

        return View(new FakeBankViewModel(attempt, returnUrl));
    }

    [HttpPost]
    public async Task<IActionResult> Complete(string reference, string result, string? returnUrl)
    {
        var attempt = IsActive ? await _payments.GetAttemptAsync(reference) : null;
        if (attempt is null || result is not ("Paid" or "Failed" or "Cancelled"))
        {
            return NotFound();
        }

        // The "bank" sends the shop a signed answer, exactly like the callback address receives from a real bank.
        var amount = attempt.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        await _payments.HandleCallbackAsync(new Dictionary<string, string>
        {
            ["reference"] = reference,
            ["status"] = result,
            ["amount"] = amount,
            ["signature"] = FakePaymentProvider.Sign(_options.FakeSecret, reference, result, amount)
        });

        // Back to the shop, but only to our own address.
        if (Uri.TryCreate(returnUrl, UriKind.Absolute, out var back) && back.Authority == Request.Host.Value)
        {
            return Redirect(back.ToString());
        }

        return RedirectToAction("Index", "Home");
    }
}

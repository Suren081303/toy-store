using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Shop.Core.Carts;
using Shop.Core.Entities;
using Shop.Core.Orders;
using Shop.Core.Payments;
using Shop.Core.Phones;
using Shop.Web.Models;
using Shop.Web.Payments;
using Shop.Web.Security;

namespace Shop.Web.Controllers;

[AdminCannotBuy]
public class CheckoutController : Controller
{
    private readonly ICartService _cart;
    private readonly IOrderService _orders;
    private readonly IStoreSettingsService _settings;
    private readonly IPaymentService _payments;
    private readonly CartOwnerResolver _owners;
    private readonly UserManager<ApplicationUser> _users;
    private readonly TimeProvider _time;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public CheckoutController(
        ICartService cart,
        IOrderService orders,
        IStoreSettingsService settings,
        IPaymentService payments,
        CartOwnerResolver owners,
        UserManager<ApplicationUser> users,
        TimeProvider time,
        IHtmlLocalizer<SharedResource> localizer)
    {
        _cart = cart;
        _orders = orders;
        _settings = settings;
        _payments = payments;
        _owners = owners;
        _users = users;
        _time = time;
        _l = localizer;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var cart = await _cart.GetAsync(_owners.Resolve(HttpContext), CurrentCulture.Code);

        var redirect = RedirectIfNotReady(cart);
        if (redirect is not null)
        {
            return redirect;
        }

        var model = new CheckoutViewModel { CheckoutId = Guid.NewGuid() };
        await PrefillAsync(model);
        await FillPageDataAsync(model, cart);

        // The first date and time slot are preselected, so most customers only need to check them.
        model.DeliveryDate ??= model.Dates.FirstOrDefault().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        model.DeliverySlotId ??= model.Slots.FirstOrDefault()?.Id;

        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Index(CheckoutViewModel model)
    {
        var owner = _owners.Resolve(HttpContext);
        var cart = await _cart.GetAsync(owner, CurrentCulture.Code);

        // The cart is not checked here: a form sent a second time (Back button, bad connection) finds the cart already empty,
        // and PlaceAsync then answers with the order that was already made. An empty or unavailable cart is handled by its answer.

        // A signed-in customer's email is the account's, whatever the form says.
        if (owner.IsSignedIn)
        {
            var user = await _users.GetUserAsync(User);
            model.Email = user?.Email ?? model.Email;
            ModelState.Remove(nameof(CheckoutViewModel.Email));
        }

        var settings = await _settings.GetAsync();
        var today = ShopClock.Today(_time);

        DateOnly deliveryDate = default;
        if (!DateOnly.TryParseExact(model.DeliveryDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out deliveryDate)
            || !DeliveryRules.IsAllowedDate(deliveryDate, today, settings.DeliveryFirstDayOffset, settings.DeliveryDaysAhead))
        {
            ModelState.Remove(nameof(CheckoutViewModel.DeliveryDate));
            ModelState.AddModelError(nameof(CheckoutViewModel.DeliveryDate), _l["Checkout_BadDate"].Value);
        }

        if (!Regions.IsValid(model.Region))
        {
            ModelState.Remove(nameof(CheckoutViewModel.Region));
            ModelState.AddModelError(nameof(CheckoutViewModel.Region), _l["Checkout_BadRegion"].Value);
        }

        if (!settings.Slots.Any(s => s.Id == model.DeliverySlotId))
        {
            ModelState.Remove(nameof(CheckoutViewModel.DeliverySlotId));
            ModelState.AddModelError(nameof(CheckoutViewModel.DeliverySlotId), _l["Checkout_BadSlot"].Value);
        }

        // Card is offered only when a card provider is configured; an unknown value is refused too.
        if (!Enum.IsDefined(model.PaymentMethod) || (model.PaymentMethod == PaymentMethod.Card && !_payments.CardPaymentAvailable))
        {
            ModelState.AddModelError(nameof(CheckoutViewModel.PaymentMethod), _l["Checkout_PaymentUnavailable"].Value);
        }

        if (!ModelState.IsValid)
        {
            var notReady = RedirectIfNotReady(cart);
            if (notReady is not null)
            {
                return notReady;
            }

            await FillPageDataAsync(model, cart);
            return View(model);
        }

        PhoneNumbers.TryNormalize(model.Phone, out var phone);

        var result = await _orders.PlaceAsync(owner, new CheckoutInput
        {
            CheckoutId = model.CheckoutId,
            CustomerName = NormalizeName(model.CustomerName),
            Email = model.Email.Trim(),
            Phone = phone,
            Region = model.Region,
            City = model.City.Trim(),
            Street = model.Street.Trim(),
            Building = model.Building.Trim(),
            Apartment = model.Apartment?.Trim(),
            Comment = model.Comment?.Trim(),
            DeliveryDate = deliveryDate,
            DeliverySlotId = model.DeliverySlotId!.Value,
            PaymentMethod = model.PaymentMethod,
            ExpectedTotal = model.ExpectedTotal
        }, CurrentCulture.Code);

        switch (result.Status)
        {
            case PlaceOrderStatus.Placed:
                var order = result.Order!;

                // Card: straight to the bank. The emails are queued when the payment is confirmed.
                if (order.PaymentMethod == PaymentMethod.Card)
                {
                    var started = await _payments.StartCardPaymentAsync(
                        order.Number, CurrentCulture.Code, Url.PaymentReturn(Request, order.Number, order.AccessKey), Url.PaymentCallback(Request));

                    return started.Status == StartPaymentStatus.Started
                        ? Redirect(started.RedirectUrl!)
                        : RedirectToAction(nameof(Done), new { number = order.Number, key = order.AccessKey });
                }

                return RedirectToAction(nameof(Done), new { number = order.Number, key = order.AccessKey });

            case PlaceOrderStatus.AlreadyPlaced:
                return RedirectToAction(nameof(Done), new { number = result.Order!.Number, key = result.Order.AccessKey });

            case PlaceOrderStatus.EmptyCart:
                return RedirectToAction("Index", "Cart");

            case PlaceOrderStatus.UnavailableItems:
                TempData["CartMessage"] = _l["Checkout_UnavailableItems"].Value;
                return RedirectToAction("Index", "Cart");

            case PlaceOrderStatus.OutOfStock:
                TempData["CartMessage"] = string.Join(' ', result.Problems!.Select(p =>
                    _l.GetString("Checkout_OutOfStock", p.Name, p.Available).Value));
                return RedirectToAction("Index", "Cart");

            case PlaceOrderStatus.PriceChanged:
                // Show the new total and ask the customer to confirm it by pressing the button again.
                ModelState.Remove(nameof(CheckoutViewModel.ExpectedTotal));
                ModelState.AddModelError(string.Empty, _l.GetString("Checkout_PriceChanged", Money.Format(result.NewTotal)).Value);
                await FillPageDataAsync(model, cart);
                return View(model);

            default:
                ModelState.AddModelError(string.Empty, _l["Checkout_BadDelivery"].Value);
                await FillPageDataAsync(model, cart);
                return View(model);
        }
    }

    // The page the customer lands on after ordering, and the page the link in the email opens.
    [HttpGet]
    public async Task<IActionResult> Done(int number, string? key)
    {
        var order = string.IsNullOrEmpty(key) ? null : await _orders.FindByKeyAsync(number, key);

        if (order is null)
        {
            return NotFound();
        }

        return View(order);
    }

    // An empty cart, or a cart with products that can no longer be bought, goes back to the cart page.
    private IActionResult? RedirectIfNotReady(CartView cart)
    {
        if (cart.Lines.Count == 0)
        {
            return RedirectToAction("Index", "Cart");
        }

        if (cart.HasUnavailableLines)
        {
            TempData["CartMessage"] = _l["Checkout_UnavailableItems"].Value;
            return RedirectToAction("Index", "Cart");
        }

        return null;
    }

    // Signed-in customers: name, phone and email from the account, the address from their last order.
    private async Task PrefillAsync(CheckoutViewModel model)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var user = await _users.GetUserAsync(User);
        if (user is null)
        {
            return;
        }

        model.CustomerName = user.FullName;
        model.Email = user.Email ?? string.Empty;
        model.Phone = user.PhoneNumber ?? string.Empty;

        var last = await _orders.GetLastAddressAsync(user.Id);
        if (last is not null)
        {
            model.CustomerName = last.CustomerName;
            model.Phone = last.Phone;
            model.Region = last.Region;
            model.City = last.City;
            model.Street = last.Street;
            model.Building = last.Building;
            model.Apartment = last.Apartment;
        }
    }

    // The cart, delivery options, fee and total shown on the page.
    private async Task FillPageDataAsync(CheckoutViewModel model, CartView cart)
    {
        var settings = await _settings.GetAsync();

        model.Cart = cart;
        model.IsSignedIn = User.Identity?.IsAuthenticated == true;
        model.CardAvailable = _payments.CardPaymentAvailable;
        model.DeliveryFee = DeliveryRules.GetFee(cart.Total, settings.DeliveryFee, settings.FreeDeliveryFrom);
        model.FreeDeliveryFrom = settings.FreeDeliveryFrom;
        model.Total = cart.Total + model.DeliveryFee;
        model.Dates = DeliveryRules.GetDeliveryDates(ShopClock.Today(_time), settings.DeliveryFirstDayOffset, settings.DeliveryDaysAhead);
        model.Slots = settings.Slots;

        // The hidden field must carry the total shown now (it may have changed).
        ModelState.Remove(nameof(CheckoutViewModel.ExpectedTotal));
        model.ExpectedTotal = model.Total;
    }

    private static string NormalizeName(string name)
    {
        return string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}

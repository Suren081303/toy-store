using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Shop.Core.Admin;
using Shop.Core.Carts;
using Shop.Core.Orders;
using Shop.Core.Phones;
using Shop.Web.Areas.Admin.Models;

namespace Shop.Web.Areas.Admin.Controllers;

// An order made for a customer who phoned or came in. Three steps: the customer, the items, delivery and payment.
// The items wait in the admin's own "staff cart" (the cart of the admin's account), which is priced with the discount of the
// customer the order is for. Placing the order uses the same code as the website, so stock, prices and delivery rules are the same.
public class PhoneOrdersController : AdminControllerBase
{
    private const string Guest = "guest";
    private const int SearchResults = 10;

    private readonly IAdminCustomerService _customers;
    private readonly ICartService _cart;
    private readonly IAdminProductService _products;
    private readonly IOrderService _orders;
    private readonly IStoreSettingsService _settings;
    private readonly TimeProvider _time;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public PhoneOrdersController(
        IAdminCustomerService customers,
        ICartService cart,
        IAdminProductService products,
        IOrderService orders,
        IStoreSettingsService settings,
        TimeProvider time,
        IHtmlLocalizer<SharedResource> localizer)
    {
        _customers = customers;
        _cart = cart;
        _products = products;
        _orders = orders;
        _settings = settings;
        _time = time;
        _l = localizer;
    }

    private CartOwner StaffCart => new(AdminId, null);

    // ---- Step 1: the customer ----

    [HttpGet]
    public async Task<IActionResult> Index(string? q)
    {
        var matches = string.IsNullOrWhiteSpace(q)
            ? []
            : (await _customers.ListAsync(new AdminCustomerFilter { Query = q })).Items.Take(SearchResults).ToList();

        return View(new PhoneOrderStartViewModel(q, matches, await _cart.CountAsync(StaffCart)));
    }

    // ---- Step 2: the items ----

    [HttpGet]
    public async Task<IActionResult> Items(string customer, string? pq)
    {
        var (found, account) = await ResolveAsync(customer);
        if (!found)
        {
            return NotFound();
        }

        var cart = await _cart.GetForCustomerAsync(StaffCart, CurrentCulture.Code, account?.Id);

        var products = new List<PhoneOrderProduct>();
        if (!string.IsNullOrWhiteSpace(pq))
        {
            var rows = await _products.ListAsync(new AdminProductFilter { Query = pq.Trim() }, CurrentCulture.Code);
            products = rows.Items
                .Where(p => !p.IsArchived && p.StockQuantity > 0)
                .Take(SearchResults)
                .Select(p => new PhoneOrderProduct(p.Id, p.Sku, p.Name, p.Brand, p.Price, p.DiscountPercent, p.StockQuantity, p.StockQuantity))
                .ToList();
        }

        return View(new PhoneOrderItemsViewModel(customer, account, pq, products, cart));
    }

    [HttpPost]
    public async Task<IActionResult> AddItem(string customer, string? pq, int productId, int quantity = 1)
    {
        var result = await _cart.AddAsync(StaffCart, productId, Math.Max(1, quantity));
        Report(result);

        return RedirectToAction(nameof(Items), new { customer, pq });
    }

    [HttpPost]
    public async Task<IActionResult> SetQuantity(string customer, string? pq, int productId, int quantity)
    {
        var result = await _cart.SetQuantityAsync(StaffCart, productId, quantity);
        Report(result);

        return RedirectToAction(nameof(Items), new { customer, pq });
    }

    [HttpPost]
    public async Task<IActionResult> RemoveItem(string customer, string? pq, int productId)
    {
        await _cart.RemoveAsync(StaffCart, productId);

        return RedirectToAction(nameof(Items), new { customer, pq });
    }

    [HttpPost]
    public async Task<IActionResult> Clear()
    {
        await _cart.ClearAsync(StaffCart);

        return RedirectToAction(nameof(Index));
    }

    // ---- Step 3: delivery, payment, place ----

    [HttpGet]
    public async Task<IActionResult> Details(string customer)
    {
        var (found, account) = await ResolveAsync(customer);
        if (!found)
        {
            return NotFound();
        }

        var cart = await _cart.GetForCustomerAsync(StaffCart, CurrentCulture.Code, account?.Id);
        if (!cart.Lines.Any(l => l.IsAvailable))
        {
            Failure(_l["PO_EmptyCart"].Value);
            return RedirectToAction(nameof(Items), new { customer });
        }

        var form = new PhoneOrderForm
        {
            CheckoutId = Guid.NewGuid(),
            Customer = customer,
            Language = CurrentCulture.Code,
            CustomerName = account?.FullName ?? string.Empty,
            Email = account?.Email,
            Phone = account?.Phone ?? string.Empty
        };

        // A registered customer: the contact and address of their last order.
        if (account is not null && await _orders.GetLastAddressAsync(account.Id) is { } last)
        {
            form.CustomerName = last.CustomerName;
            form.Phone = last.Phone;
            form.Region = last.Region;
            form.City = last.City;
            form.Street = last.Street;
            form.Building = last.Building;
            form.Apartment = last.Apartment;
        }

        await FillPageDataAsync(form, cart, account);

        // The first day and time are preselected.
        form.DeliveryDate = form.Dates.FirstOrDefault().ToString("yyyy-MM-dd");
        form.DeliverySlotId = form.Slots.FirstOrDefault()?.Id;

        return View(form);
    }

    [HttpPost]
    public async Task<IActionResult> Place(PhoneOrderForm form)
    {
        var (found, account) = await ResolveAsync(form.Customer);
        if (!found)
        {
            return NotFound();
        }

        var cart = await _cart.GetForCustomerAsync(StaffCart, CurrentCulture.Code, account?.Id);
        var settings = await _settings.GetAsync();
        var today = ShopClock.Today(_time);

        DateOnly day = default;
        if (!OrderFormRules.TryParseDate(form.DeliveryDate, out day)
            || !DeliveryRules.IsAllowedDate(day, today, settings.DeliveryFirstDayOffset, settings.DeliveryDaysAhead))
        {
            ModelState.Remove(nameof(form.DeliveryDate));
            ModelState.AddModelError(nameof(form.DeliveryDate), _l["Checkout_BadDate"].Value);
        }

        if (!Regions.IsValid(form.Region))
        {
            ModelState.Remove(nameof(form.Region));
            ModelState.AddModelError(nameof(form.Region), _l["Checkout_BadRegion"].Value);
        }

        if (!settings.Slots.Any(s => s.Id == form.DeliverySlotId))
        {
            ModelState.Remove(nameof(form.DeliverySlotId));
            ModelState.AddModelError(nameof(form.DeliverySlotId), _l["Checkout_BadSlot"].Value);
        }

        // Cash or bank transfer only: nobody is at the bank's page to pay by card.
        if (form.PaymentMethod is not (PaymentMethod.CashOnDelivery or PaymentMethod.BankTransfer))
        {
            ModelState.AddModelError(nameof(form.PaymentMethod), _l["Checkout_PaymentUnavailable"].Value);
        }

        if (!new[] { "hy", "ru", "en" }.Contains(form.Language))
        {
            form.Language = "hy";
        }

        if (!ModelState.IsValid)
        {
            await FillPageDataAsync(form, cart, account);
            return View("Details", form);
        }

        PhoneNumbers.TryNormalize(form.Phone, out var phone);

        var result = await _orders.PlaceForAdminAsync(AdminId, account?.Id, new CheckoutInput
        {
            CheckoutId = form.CheckoutId,
            CustomerName = string.Join(' ', form.CustomerName.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            Email = (form.Email ?? string.Empty).Trim(),
            Phone = phone,
            Region = form.Region,
            City = form.City.Trim(),
            Street = form.Street.Trim(),
            Building = form.Building.Trim(),
            Apartment = form.Apartment?.Trim(),
            Comment = form.Comment?.Trim(),
            DeliveryDate = day,
            DeliverySlotId = form.DeliverySlotId!.Value,
            PaymentMethod = form.PaymentMethod,
            ExpectedTotal = form.ExpectedTotal
        }, form.Language);

        switch (result.Status)
        {
            case PlaceOrderStatus.Placed:
            case PlaceOrderStatus.AlreadyPlaced:
                Success(_l.GetString("PO_Placed", result.Order!.Number).Value);
                return RedirectToAction("Details", "Orders", new { number = result.Order.Number });

            case PlaceOrderStatus.EmptyCart:
                Failure(_l["PO_EmptyCart"].Value);
                return RedirectToAction(nameof(Items), new { customer = form.Customer });

            case PlaceOrderStatus.UnavailableItems:
                Failure(_l["PO_Unavailable"].Value);
                return RedirectToAction(nameof(Items), new { customer = form.Customer });

            case PlaceOrderStatus.OutOfStock:
                Failure(string.Join(' ', result.Problems!.Select(p => _l.GetString("Checkout_OutOfStock", p.Name, p.Available).Value)));
                return RedirectToAction(nameof(Items), new { customer = form.Customer });

            case PlaceOrderStatus.PriceChanged:
                // Show the new total and ask for the button to be pressed again.
                ModelState.Remove(nameof(form.ExpectedTotal));
                ModelState.AddModelError(string.Empty, _l.GetString("Checkout_PriceChanged", Money.Format(result.NewTotal)).Value);
                break;

            default:
                ModelState.AddModelError(string.Empty, _l["Checkout_BadDelivery"].Value);
                break;
        }

        await FillPageDataAsync(form, cart, account);
        return View("Details", form);
    }

    // ---- Helpers ----

    // "guest" (or nothing): a customer without an account. Otherwise the account must exist and be a customer.
    private async Task<(bool Found, PhoneOrderCustomer? Account)> ResolveAsync(string? customer)
    {
        if (string.IsNullOrEmpty(customer) || customer == Guest)
        {
            return (true, null);
        }

        var account = await _customers.FindAsync(customer);

        return (account is not null, account);
    }

    private async Task FillPageDataAsync(PhoneOrderForm form, CartView cart, PhoneOrderCustomer? account)
    {
        var settings = await _settings.GetAsync();
        await OrderFormRules.FillPageDataAsync(_settings, _time, form);

        // The hidden "expected total" is read from the model, not from what was posted before.
        ModelState.Remove(nameof(form.ExpectedTotal));

        form.Account = account;
        form.Cart = cart;
        form.DeliveryFee = DeliveryRules.GetFee(cart.Total, settings.DeliveryFee, settings.FreeDeliveryFrom);
        form.Total = cart.Total + form.DeliveryFee;
        form.ExpectedTotal = form.Total;
    }

    private void Report(CartChange result)
    {
        switch (result.Status)
        {
            case CartChangeStatus.LimitReached:
                Failure(_l.GetString("Cart_LimitReached", result.Limit).Value);
                break;
            case CartChangeStatus.TooManyLines:
                Failure(_l["Cart_TooMany"].Value);
                break;
            case CartChangeStatus.NotAvailable:
                Failure(_l["Cart_NotAvailable"].Value);
                break;
        }
    }
}

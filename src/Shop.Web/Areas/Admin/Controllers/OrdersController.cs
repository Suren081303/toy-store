using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Shop.Core.Admin;
using Shop.Core.Orders;
using Shop.Core.Payments;
using Shop.Web.Areas.Admin.Models;

namespace Shop.Web.Areas.Admin.Controllers;

public class OrdersController : AdminControllerBase
{
    private readonly IAdminOrderService _admin;
    private readonly IOrderService _orders;
    private readonly IPaymentService _payments;
    private readonly IStoreSettingsService _settings;
    private readonly TimeProvider _time;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public OrdersController(
        IAdminOrderService admin,
        IOrderService orders,
        IPaymentService payments,
        IStoreSettingsService settings,
        TimeProvider time,
        IHtmlLocalizer<SharedResource> localizer)
    {
        _admin = admin;
        _orders = orders;
        _payments = payments;
        _settings = settings;
        _time = time;
        _l = localizer;
    }

    [HttpGet]
    public async Task<IActionResult> Index(AdminOrderFilter filter)
    {
        return View(new AdminOrdersViewModel(filter, await _admin.ListAsync(filter)));
    }

    [HttpGet]
    public async Task<IActionResult> Details(int number)
    {
        var detail = await _admin.GetAsync(number);
        if (detail is null)
        {
            return NotFound();
        }

        return View(detail);
    }

    [HttpPost]
    public async Task<IActionResult> ChangeStatus(int number, OrderStatus status, string? note, string? returnUrl)
    {
        // Only what the page offers: the rules are checked here again, because a form can be sent by hand.
        var detail = await _admin.GetAsync(number);
        if (detail is null)
        {
            return NotFound();
        }

        if (!detail.NextStatuses.Contains(status))
        {
            Failure(_l["Admin_Msg_NotAllowed"].Value);
            return Back(number, returnUrl);
        }

        var result = await _orders.ChangeStatusAsync(number, status, AdminId, note?.Trim(), onlyIfCurrently: detail.Order.Status);

        if (result == ChangeStatusResult.Changed)
        {
            Success(_l["Admin_Msg_StatusChanged"].Value);
        }
        else
        {
            Failure(_l["Admin_Msg_NotAllowed"].Value);
        }

        return Back(number, returnUrl);
    }

    [HttpPost]
    public async Task<IActionResult> MarkReceived(int number, string? returnUrl)
    {
        var result = await _payments.MarkBankTransferPaidAsync(number, AdminId);

        switch (result)
        {
            case MarkPaidResult.Marked:
                Success(_l["Admin_Msg_Received"].Value);
                break;
            case MarkPaidResult.NotFound:
                return NotFound();
            default:
                Failure(_l["Admin_Msg_NotAllowed"].Value);
                break;
        }

        return Back(number, returnUrl);
    }

    [HttpPost]
    public async Task<IActionResult> RefundDone(int paymentId, int number)
    {
        var result = await _admin.MarkRefundDoneAsync(paymentId, AdminId);

        if (result == RefundDoneResult.Done)
        {
            Success(_l["Admin_Msg_RefundDone"].Value);
        }
        else
        {
            Failure(_l["Admin_Msg_NotAllowed"].Value);
        }

        return RedirectToAction(nameof(Details), new { number });
    }

    // ---- Edit ----

    [HttpGet]
    public async Task<IActionResult> Edit(int number)
    {
        var detail = await _admin.GetAsync(number);
        if (detail is null)
        {
            return NotFound();
        }

        if (detail.EditScope == OrderEditScope.None)
        {
            Failure(_l["Admin_Edit_NotEditable"].Value);
            return RedirectToAction(nameof(Details), new { number });
        }

        var order = detail.Order;
        var settings = await _settings.GetAsync();

        var form = new AdminOrderEditForm
        {
            Number = number,
            Scope = detail.EditScope,
            CurrentSlot = order.DeliverySlot,
            CustomerName = order.CustomerName,
            Email = order.Email,
            Phone = order.Phone,
            Region = order.Region,
            City = order.City,
            Street = order.Street,
            Building = order.Building,
            Apartment = order.Apartment,
            Comment = order.Comment,
            DeliveryDate = order.DeliveryDate.ToString("yyyy-MM-dd"),
            DeliverySlotId = settings.Slots.FirstOrDefault(s => s.Label == order.DeliverySlot)?.Id
        };

        await OrderFormRules.FillPageDataAsync(_settings, _time, form, order.DeliveryDate);
        return View(form);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(AdminOrderEditForm form)
    {
        var detail = await _admin.GetAsync(form.Number);
        if (detail is null)
        {
            return NotFound();
        }

        form.Scope = detail.EditScope;
        form.CurrentSlot = detail.Order.DeliverySlot;

        DateOnly day = default;
        if (!OrderFormRules.TryParseDate(form.DeliveryDate, out day))
        {
            ModelState.Remove(nameof(form.DeliveryDate));
            ModelState.AddModelError(nameof(form.DeliveryDate), _l["Checkout_BadDate"].Value);
        }

        if (ModelState.IsValid)
        {
            var result = await _admin.UpdateAsync(form.Number, OrderFormRules.ToEdit(form, day), AdminId);

            switch (result)
            {
                case EditOrderStatus.Saved:
                    Success(_l["Admin_Edit_Saved"].Value);
                    return RedirectToAction(nameof(Details), new { number = form.Number });
                case EditOrderStatus.NoChange:
                    Success(_l["Admin_Edit_NoChange"].Value);
                    return RedirectToAction(nameof(Details), new { number = form.Number });
                case EditOrderStatus.NotFound:
                    return NotFound();
                case EditOrderStatus.NotEditable:
                    Failure(_l["Admin_Edit_NotEditable"].Value);
                    return RedirectToAction(nameof(Details), new { number = form.Number });
                case EditOrderStatus.InvalidContact:
                    ModelState.AddModelError(string.Empty, _l["Admin_Edit_InvalidContact"].Value);
                    break;
                default:
                    ModelState.AddModelError(string.Empty, _l["Admin_Edit_InvalidDelivery"].Value);
                    break;
            }
        }

        await OrderFormRules.FillPageDataAsync(_settings, _time, form, detail.Order.DeliveryDate);
        return View(form);
    }

    // ---- Notes (only admins see them) ----

    [HttpPost]
    public async Task<IActionResult> AddNote(int number, string? text)
    {
        switch (await _admin.AddNoteAsync(number, text ?? string.Empty, AdminId))
        {
            case AddNoteStatus.Added:
                Success(_l["Admin_Notes_Added"].Value);
                break;
            case AddNoteStatus.NotFound:
                return NotFound();
            default:
                Failure(_l["Admin_Notes_Invalid"].Value);
                break;
        }

        return RedirectToAction(nameof(Details), new { number });
    }

    // ---- Packing slips ----

    [HttpGet]
    public async Task<IActionResult> Slip(int number)
    {
        var slip = await _admin.GetSlipAsync(number);

        return slip is null ? NotFound() : View("Slip", new SlipsViewModel([slip], false));
    }

    // The slips of the orders that match the filter, in delivery order (one page break between orders).
    [HttpGet]
    public async Task<IActionResult> Slips(AdminOrderFilter filter)
    {
        const int Max = 100;
        var slips = await _admin.GetSlipsAsync(filter, Max + 1);

        return View("Slip", new SlipsViewModel(slips.Take(Max).ToList(), slips.Count > Max));
    }

    // Back to the list the button was pressed on (only an address on this site), otherwise to the order.
    private IActionResult Back(int number, string? returnUrl)
    {
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Details), new { number });
    }
}

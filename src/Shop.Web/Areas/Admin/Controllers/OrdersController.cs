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
    private readonly IHtmlLocalizer<SharedResource> _l;

    public OrdersController(
        IAdminOrderService admin,
        IOrderService orders,
        IPaymentService payments,
        IHtmlLocalizer<SharedResource> localizer)
    {
        _admin = admin;
        _orders = orders;
        _payments = payments;
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

    // Back to the list the button was pressed on (only an address on this site), otherwise to the order.
    private IActionResult Back(int number, string? returnUrl)
    {
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Details), new { number });
    }
}

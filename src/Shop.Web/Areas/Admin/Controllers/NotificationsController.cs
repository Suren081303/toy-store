using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Shop.Core.Notifications;
using Shop.Web.Areas.Admin.Models;

namespace Shop.Web.Areas.Admin.Controllers;

// The admins' bell. Read state is shared: when one admin reads an item, it is read for everybody.
public class NotificationsController : AdminControllerBase
{
    private readonly IAdminNotificationService _notifications;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public NotificationsController(IAdminNotificationService notifications, IHtmlLocalizer<SharedResource> localizer)
    {
        _notifications = notifications;
        _l = localizer;
    }

    [HttpGet]
    public async Task<IActionResult> Index(bool unread = false, int page = 1)
    {
        return View(new AdminNotificationsViewModel(unread, await _notifications.ListAsync(unread, page)));
    }

    // The page's script asks this every 120 seconds to update the number on the bell.
    [HttpGet]
    public async Task<IActionResult> Summary()
    {
        Response.Headers.CacheControl = "no-store";
        return Json(new { unread = await _notifications.UnreadCountAsync() });
    }

    // Opens what the item is about, and marks the item as read on the way.
    [HttpGet]
    public async Task<IActionResult> Open(int id)
    {
        var item = await _notifications.GetAsync(id);
        if (item is null)
        {
            return NotFound();
        }

        await _notifications.MarkReadAsync(id, AdminId);

        return item.Type switch
        {
            NotificationType.ReturnRequested when item.ReturnId is not null => RedirectToAction("Details", "Returns", new { id = item.ReturnId }),
            NotificationType.LowStock when item.ProductId is not null => RedirectToAction("Edit", "Products", new { id = item.ProductId }),
            _ when item.OrderNumber is not null => RedirectToAction("Details", "Orders", new { number = item.OrderNumber }),
            _ => RedirectToAction(nameof(Index))
        };
    }

    [HttpPost]
    public async Task<IActionResult> MarkRead(int id, bool unread = false)
    {
        await _notifications.MarkReadAsync(id, AdminId);
        return RedirectToAction(nameof(Index), new { unread });
    }

    [HttpPost]
    public async Task<IActionResult> MarkAllRead()
    {
        await _notifications.MarkAllReadAsync(AdminId);
        Success(_l["Admin_Notif_AllRead"].Value);
        return RedirectToAction(nameof(Index));
    }
}

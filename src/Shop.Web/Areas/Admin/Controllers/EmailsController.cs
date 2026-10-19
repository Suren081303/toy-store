using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Shop.Core.Notifications;
using Shop.Web.Areas.Admin.Models;

namespace Shop.Web.Areas.Admin.Controllers;

// The email queue: what waits to be sent, what was sent, and what failed (with the mail server's error).
public class EmailsController : AdminControllerBase
{
    private readonly IAdminEmailService _emails;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public EmailsController(IAdminEmailService emails, IHtmlLocalizer<SharedResource> localizer)
    {
        _emails = emails;
        _l = localizer;
    }

    [HttpGet]
    public async Task<IActionResult> Index(OutboxStatus? status, int page = 1)
    {
        return View(new AdminEmailsViewModel(status, await _emails.ListAsync(status, page)));
    }

    [HttpPost]
    public async Task<IActionResult> Retry(int id, OutboxStatus? status)
    {
        if (await _emails.RetryAsync(id, AdminId))
        {
            Success(_l["Admin_Msg_Retried"].Value);
        }
        else
        {
            Failure(_l["Admin_Msg_NotAllowed"].Value);
        }

        return RedirectToAction(nameof(Index), new { status });
    }
}

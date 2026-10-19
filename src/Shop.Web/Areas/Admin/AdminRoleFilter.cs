using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Shop.Core.Admin;

namespace Shop.Web.Areas.Admin;

// The sign-in cookie says "Admin" for up to 30 days. This filter asks the database on every admin request,
// so an admin who was removed is stopped at once, not when their cookie runs out.
// (A visitor who is not signed in is handled by [Authorize]: it sends them to the sign-in page.)
public class AdminRoleFilter : IAsyncAuthorizationFilter
{
    private readonly IAdminAccountService _accounts;

    public AdminRoleFilter(IAdminAccountService accounts)
    {
        _accounts = accounts;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null || !await _accounts.IsAdminAsync(userId))
        {
            context.Result = new ForbidResult();
        }
    }
}

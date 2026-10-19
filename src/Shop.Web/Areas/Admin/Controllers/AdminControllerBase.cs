using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shop.Core.Identity;

namespace Shop.Web.Areas.Admin.Controllers;

// Every admin page: only for accounts with the Admin role. A visitor who is not signed in goes to the sign-in page,
// a signed-in customer gets "access denied" (see the cookie events in IdentityServiceCollectionExtensions).
// The role is checked twice: from the sign-in cookie, and (AdminRoleFilter) from the database, so removing an admin works at once.
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[ServiceFilter(typeof(AdminRoleFilter))]
public abstract class AdminControllerBase : Controller
{
    protected string AdminId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // The green bar at the top of the next page.
    protected void Success(string message) => TempData["AdminMessage"] = message;

    // The red bar at the top of the next page.
    protected void Failure(string message) => TempData["AdminError"] = message;
}

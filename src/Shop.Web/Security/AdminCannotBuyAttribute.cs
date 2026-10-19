using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Shop.Core.Identity;

namespace Shop.Web.Security;

public static class ShopperExtensions
{
    // Admins run the shop; they look at the pages like a customer but do not shop in it.
    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(Roles.Admin);
}

// Put on the actions that belong to buying: cart, checkout, favorites, "my orders", returns and starting a payment.
// An admin who gets here (by a typed address or a hand-made request) is refused on the server, not only by hidden buttons.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AdminCannotBuyAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.HttpContext.User.IsAdmin())
        {
            return;
        }

        // The cart and heart buttons ask for JSON; everything else is a page.
        if (context.HttpContext.Request.Headers.Accept.ToString().Contains("application/json"))
        {
            context.Result = new JsonResult(new { status = "AdminCannotBuy" }) { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        context.Result = new ViewResult
        {
            ViewName = "~/Views/Shared/AdminCannotBuy.cshtml",
            StatusCode = StatusCodes.Status403Forbidden,
            ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        };
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Shop.Core.Catalog;
using Shop.Core.Favorites;

namespace Shop.Web.Controllers;

// Works for everyone: signed-in customers keep favorites in the database, guests in a cookie.
// When a guest signs in, the cookie list moves into the account (see AccountController.Login).
public class FavoritesController : Controller
{
    private readonly IFavoriteService _favorites;
    private readonly ICatalogService _catalog;
    private readonly GuestCookies _cookies;

    public FavoritesController(IFavoriteService favorites, ICatalogService catalog, GuestCookies cookies)
    {
        _favorites = favorites;
        _catalog = catalog;
        _cookies = cookies;
    }

    public async Task<IActionResult> Index()
    {
        var culture = CurrentCulture.Code;

        var products = User.Identity?.IsAuthenticated == true
            ? await _favorites.GetProductsAsync(UserId, culture)
            : await _catalog.GetProductsByIdsAsync(_cookies.ReadFavorites(Request).ToList(), culture);

        return View(products);
    }

    // Called by the heart buttons (script). Needs the anti-forgery header.
    [HttpPost]
    public async Task<IActionResult> Toggle(int productId)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            var isFavorite = await _favorites.ToggleAsync(UserId, productId);

            return isFavorite is null ? NotFound() : Json(new { isFavorite });
        }

        if (!await _favorites.IsForSaleAsync(productId))
        {
            return NotFound();
        }

        var (ids, isGuestFavorite) = GuestFavoriteList.Toggle(_cookies.ReadFavorites(Request), productId);
        _cookies.WriteFavorites(Request, Response, ids);

        return Json(new { isFavorite = isGuestFavorite });
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
}

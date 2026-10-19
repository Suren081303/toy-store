using System.Security.Claims;
using Shop.Core.Favorites;

namespace Shop.Web;

// Which products this visitor has favorited: the signed-in customer's list from the database, or a guest's list from the cookie.
// Loaded once per request, used by every product card on the page.
public class FavoriteState
{
    private readonly IFavoriteService _favorites;
    private readonly GuestCookies _cookies;
    private HashSet<int>? _ids;

    public FavoriteState(IFavoriteService favorites, GuestCookies cookies)
    {
        _favorites = favorites;
        _cookies = cookies;
    }

    public async Task<bool> IsFavoriteAsync(HttpContext http, int productId)
    {
        if (_ids is null)
        {
            _ids = http.User.Identity?.IsAuthenticated == true
                ? await _favorites.GetIdsAsync(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!)
                : _cookies.ReadFavorites(http.Request).ToHashSet();
        }

        return _ids.Contains(productId);
    }
}

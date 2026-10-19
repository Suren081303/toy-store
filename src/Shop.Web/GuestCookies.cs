using Microsoft.Extensions.Options;
using Shop.Core.Carts;
using Shop.Core.Favorites;

namespace Shop.Web;

// The two functional cookies for visitors who are not signed in: the cart id (30 days) and the favorites list (180 days).
// Both are set only when the visitor adds something, are HttpOnly, and are not used for tracking.
public class GuestCookies
{
    public const string CartCookieName = "Shop.Cart";

    public const string FavoritesCookieName = "Shop.Favs";

    private static readonly TimeSpan FavoritesLifetime = TimeSpan.FromDays(180);

    private readonly IWebHostEnvironment _environment;
    private readonly CartOptions _cartOptions;

    public GuestCookies(IWebHostEnvironment environment, IOptions<CartOptions> cartOptions)
    {
        _environment = environment;
        _cartOptions = cartOptions.Value;
    }

    public Guid? ReadCartId(HttpRequest request)
    {
        return request.Cookies.TryGetValue(CartCookieName, out var text) && Guid.TryParse(text, out var id) ? id : null;
    }

    public void WriteCartId(HttpRequest request, HttpResponse response, Guid id)
    {
        response.Cookies.Append(CartCookieName, id.ToString(), Options(request, TimeSpan.FromDays(_cartOptions.GuestCartDays)));
    }

    public void ClearCart(HttpRequest request, HttpResponse response)
    {
        response.Cookies.Delete(CartCookieName, Options(request, TimeSpan.Zero));
    }

    public IReadOnlyList<int> ReadFavorites(HttpRequest request)
    {
        return GuestFavoriteList.Parse(request.Cookies[FavoritesCookieName]);
    }

    public void WriteFavorites(HttpRequest request, HttpResponse response, IEnumerable<int> ids)
    {
        response.Cookies.Append(FavoritesCookieName, GuestFavoriteList.Format(ids), Options(request, FavoritesLifetime));
    }

    public void ClearFavorites(HttpRequest request, HttpResponse response)
    {
        response.Cookies.Delete(FavoritesCookieName, Options(request, TimeSpan.Zero));
    }

    private CookieOptions Options(HttpRequest request, TimeSpan lifetime)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = !_environment.IsDevelopment() || request.IsHttps,
            IsEssential = true,
            Path = "/",
            Expires = lifetime == TimeSpan.Zero ? null : DateTimeOffset.UtcNow.Add(lifetime)
        };
    }
}

using System.Security.Claims;
using Shop.Core.Carts;

namespace Shop.Web;

// Whose cart this request is about: the signed-in customer's, or the guest cart named in the cookie.
public class CartOwnerResolver
{
    private readonly GuestCookies _cookies;

    public CartOwnerResolver(GuestCookies cookies)
    {
        _cookies = cookies;
    }

    public CartOwner Resolve(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated == true)
        {
            return new CartOwner(http.User.FindFirstValue(ClaimTypes.NameIdentifier), null);
        }

        return new CartOwner(null, _cookies.ReadCartId(http.Request));
    }
}

// The number shown on the cart badge. Loaded once per request; a visitor without a cart costs no database query.
public class CartState
{
    private readonly ICartService _cart;
    private readonly CartOwnerResolver _owners;
    private int? _count;

    public CartState(ICartService cart, CartOwnerResolver owners)
    {
        _cart = cart;
        _owners = owners;
    }

    public async Task<int> GetCountAsync(HttpContext http)
    {
        if (_count is int known)
        {
            return known;
        }

        var owner = _owners.Resolve(http);
        _count = owner.IsSignedIn || owner.GuestCartId is not null ? await _cart.CountAsync(owner) : 0;
        return _count.Value;
    }
}

using System.Security.Claims;
using Shop.Core.Orders;

namespace Shop.Web;

// Who may open an order: its owner (signed in), or anyone who has the secret key from the confirmation email.
public class OrderAccess
{
    private readonly IOrderService _orders;

    public OrderAccess(IOrderService orders)
    {
        _orders = orders;
    }

    public async Task<OrderSummary?> ResolveAsync(ClaimsPrincipal user, int number, string? key)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is not null)
        {
            var mine = await _orders.FindForUserAsync(userId, number);
            if (mine is not null)
            {
                return mine;
            }
        }

        return string.IsNullOrEmpty(key) ? null : await _orders.FindByKeyAsync(number, key);
    }
}

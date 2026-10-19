namespace Shop.Core.Orders;

public interface IStoreSettingsService
{
    // Delivery fee, free-delivery threshold, delivery days, return window, and the active time slots.
    Task<StoreSettingsView> GetAsync();
}

public interface IOrderService
{
    // Turns the owner's cart into an order, all or nothing: the items are checked, the stock is reserved,
    // the order is saved with a snapshot of names/prices/address, and the cart is emptied.
    Task<PlaceOrderResult> PlaceAsync(Carts.CartOwner owner, CheckoutInput input, string culture);

    // The four ways to open an order. Whoever calls these has already decided that the visitor may see it.
    Task<OrderSummary?> FindForUserAsync(string userId, int number);

    Task<OrderSummary?> FindByKeyAsync(int number, string accessKey);

    Task<OrderSummary?> FindByEmailAsync(int number, string email);

    Task<IReadOnlyList<OrderListItem>> ListForUserAsync(string userId);

    // The contact and address of the customer's latest order, or null when they have none.
    Task<LastAddress?> GetLastAddressAsync(string userId);

    // Changes the status if the rules allow it. Cancelling puts the stock back (once). Delivering a cash order marks it paid.
    // onlyIfCurrently: change only when the order still has this status (so the expiry job cannot cancel an order that was just paid).
    Task<ChangeStatusResult> ChangeStatusAsync(int number, OrderStatus newStatus, string? byUserId, string? note, OrderStatus? onlyIfCurrently = null);

    // Opens an order without any access check. Only for the system itself (payment callbacks), never for a visitor's request.
    Task<OrderSummary?> FindForSystemAsync(int number);

    // Files a return request for a delivered order within the return window. The caller has checked access.
    Task<ReturnRequestResult> RequestReturnAsync(int number, string reason);
}

namespace Shop.Core.Carts;

public interface ICartService
{
    // The cart with prices and stock checked now. If the stock dropped below a quantity, the quantity is reduced (and reported in Notices).
    Task<CartView> GetAsync(CartOwner owner, string culture);

    // The cart priced for another person: an admin's "staff cart" for a phone order is priced with the discount of the customer
    // the order is for (customerUserId), or without any discount when that customer has no account (null).
    Task<CartView> GetForCustomerAsync(CartOwner owner, string culture, string? customerUserId);

    Task<CartChange> AddAsync(CartOwner owner, int productId, int quantity);

    // Sets the quantity of a product that is in the cart. 0 or less removes it.
    Task<CartChange> SetQuantityAsync(CartOwner owner, int productId, int quantity);

    Task RemoveAsync(CartOwner owner, int productId);

    // Total quantity in the cart (for the badge).
    Task<int> CountAsync(CartOwner owner);

    // Moves a guest cart into the customer's cart when they sign in: quantities add up, capped by stock and the limit.
    Task MergeGuestCartAsync(Guid guestCartId, string userId);

    // Empties the cart (after the order was placed).
    Task ClearAsync(CartOwner owner);

    Task<int> DeleteOldGuestCartsAsync(DateTime notTouchedSinceUtc);
}

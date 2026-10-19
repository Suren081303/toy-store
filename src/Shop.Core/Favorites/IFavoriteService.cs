using Shop.Core.Catalog;

namespace Shop.Core.Favorites;

public interface IFavoriteService
{
    // Adds the product to the user's favorites, or removes it when it is already there.
    // Returns true when it is now a favorite, false when it was removed, null when the product does not exist or is not for sale.
    Task<bool?> ToggleAsync(string userId, int productId);

    Task<HashSet<int>> GetIdsAsync(string userId);

    // True when the product exists and can be bought (so it may be favorited).
    Task<bool> IsForSaleAsync(int productId);

    // Adds several products at once (guest favorites moving into the account). Unknown, hidden and already saved ones are skipped.
    // Returns how many were added.
    Task<int> AddManyAsync(string userId, IEnumerable<int> productIds);

    // The user's favorites that are still for sale, most recently added first.
    Task<IReadOnlyList<ProductListItem>> GetProductsAsync(string userId, string culture);
}

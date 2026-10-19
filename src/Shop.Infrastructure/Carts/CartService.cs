using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shop.Core.Carts;
using Shop.Core.Catalog;
using Shop.Core.Entities;
using Shop.Core.Localization;
using Shop.Core.Orders;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Carts;

public class CartService : ICartService
{
    private readonly ShopDbContext _db;
    private readonly CartOptions _options;
    private readonly TimeProvider _time;

    public CartService(ShopDbContext db, IOptions<CartOptions> options, TimeProvider time)
    {
        _db = db;
        _options = options.Value;
        _time = time;
    }

    public async Task<CartView> GetAsync(CartOwner owner, string culture)
    {
        var (customerDiscount, ordersLeft) = await GetCustomerDiscountAsync(owner);
        var cart = await FindCartAsync(owner);

        if (cart is null || cart.Items.Count == 0)
        {
            return CartView.Empty(customerDiscount, ordersLeft);
        }

        var ids = cart.Items.Select(i => i.ProductId).ToList();
        var products = await _db.Products
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Include(p => p.Translations.Where(t => t.Culture == culture || t.Culture == Cultures.Default))
            .Include(p => p.Images.OrderBy(i => i.SortOrder).Take(1))
            .Include(p => p.Category)
            .AsSplitQuery()
            .ToDictionaryAsync(p => p.Id);

        var lines = new List<CartLine>();
        var notices = new List<CartNotice>();
        var changed = false;

        foreach (var item in cart.Items.OrderBy(i => i.AddedAt))
        {
            var product = products[item.ProductId];
            var status = GetStatus(product);
            var limit = GetLimit(product);
            var name = product.Translations.ResolveName(culture);

            // The stock may have dropped since the item was added.
            if (status == CartLineStatus.Available && item.Quantity > limit)
            {
                item.Quantity = limit;
                notices.Add(new CartNotice(CartNoticeKind.QuantityReduced, name, limit));
                changed = true;
            }

            var unitPrice = ProductRules.GetUnitPrice(product.Price, product.DiscountPercent, customerDiscount, out var customerApplied);

            lines.Add(new CartLine(
                product.Id,
                name,
                product.Brand,
                product.Images.FirstOrDefault() is { } photo ? photo.ThumbPath ?? photo.Path : null,
                item.Quantity,
                product.Price,
                unitPrice,
                product.DiscountPercent,
                customerApplied,
                product.StockQuantity,
                limit,
                status));
        }

        if (changed)
        {
            cart.UpdatedAt = Now();
            await _db.SaveChangesAsync();
        }

        return new CartView(lines, notices, customerDiscount, ordersLeft);
    }

    public async Task<CartChange> AddAsync(CartOwner owner, int productId, int quantity)
    {
        var product = await _db.Products.AsNoTracking().Include(p => p.Category).FirstOrDefaultAsync(p => p.Id == productId);

        if (product is null || GetStatus(product) != CartLineStatus.Available)
        {
            return new CartChange(CartChangeStatus.NotAvailable, 0, 0, await CountAsync(owner), null);
        }

        var limit = GetLimit(product);
        var cart = await FindCartAsync(owner);
        Guid? newGuestCartId = null;

        if (cart is null)
        {
            cart = new Cart { Id = Guid.NewGuid(), UserId = owner.UserId, CreatedAt = Now(), UpdatedAt = Now() };
            _db.Carts.Add(cart);

            if (owner.UserId is null)
            {
                newGuestCartId = cart.Id;
            }
        }

        var item = cart.Items.FirstOrDefault(i => i.ProductId == productId);
        if (item is null)
        {
            if (cart.Items.Count >= _options.MaxLines)
            {
                return new CartChange(CartChangeStatus.TooManyLines, 0, limit, await CountCartAsync(cart.Id), null);
            }

            item = new CartItem { CartId = cart.Id, ProductId = productId, Quantity = 0, AddedAt = Now() };
            cart.Items.Add(item);
        }

        var wanted = item.Quantity + Math.Max(1, quantity);
        item.Quantity = Math.Min(wanted, limit);
        cart.UpdatedAt = Now();

        await _db.SaveChangesAsync();

        var status = wanted > limit ? CartChangeStatus.LimitReached : CartChangeStatus.Added;
        return new CartChange(status, item.Quantity, limit, await CountCartAsync(cart.Id), newGuestCartId);
    }

    public async Task<CartChange> SetQuantityAsync(CartOwner owner, int productId, int quantity)
    {
        var cart = await FindCartAsync(owner);
        var item = cart?.Items.FirstOrDefault(i => i.ProductId == productId);

        if (cart is null || item is null)
        {
            return new CartChange(CartChangeStatus.NotInCart, 0, 0, await CountAsync(owner), null);
        }

        if (quantity <= 0)
        {
            _db.CartItems.Remove(item);
            cart.UpdatedAt = Now();
            await _db.SaveChangesAsync();
            return new CartChange(CartChangeStatus.Added, 0, 0, await CountCartAsync(cart.Id), null);
        }

        var product = await _db.Products.AsNoTracking().Include(p => p.Category).FirstAsync(p => p.Id == productId);
        if (GetStatus(product) != CartLineStatus.Available)
        {
            // A product that cannot be bought can only be removed.
            return new CartChange(CartChangeStatus.NotAvailable, item.Quantity, 0, await CountCartAsync(cart.Id), null);
        }

        var limit = GetLimit(product);
        item.Quantity = Math.Min(quantity, limit);
        cart.UpdatedAt = Now();
        await _db.SaveChangesAsync();

        var status = quantity > limit ? CartChangeStatus.LimitReached : CartChangeStatus.Added;
        return new CartChange(status, item.Quantity, limit, await CountCartAsync(cart.Id), null);
    }

    public async Task RemoveAsync(CartOwner owner, int productId)
    {
        await SetQuantityAsync(owner, productId, 0);
    }

    // Total quantity of the products that can be bought now (the same number the cart page shows).
    public async Task<int> CountAsync(CartOwner owner)
    {
        var items = owner.UserId is not null
            ? _db.CartItems.Where(i => i.Cart.UserId == owner.UserId)
            : owner.GuestCartId is Guid guestId
                ? _db.CartItems.Where(i => i.CartId == guestId && i.Cart.UserId == null)
                : null;

        if (items is null)
        {
            return 0;
        }

        return await SumBuyableAsync(items);
    }

    private async Task<int> CountCartAsync(Guid cartId)
    {
        return await SumBuyableAsync(_db.CartItems.Where(i => i.CartId == cartId));
    }

    private static async Task<int> SumBuyableAsync(IQueryable<CartItem> items)
    {
        return await items
            .Where(i => i.Product.StockQuantity > 0 && !i.Product.IsArchived && i.Product.Category.IsActive)
            .SumAsync(i => (int?)i.Quantity) ?? 0;
    }

    public async Task MergeGuestCartAsync(Guid guestCartId, string userId)
    {
        var guest = await _db.Carts.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == guestCartId && c.UserId == null);
        if (guest is null)
        {
            return;
        }

        var userCart = await FindCartAsync(new CartOwner(userId, null));
        if (userCart is null)
        {
            userCart = new Cart { Id = Guid.NewGuid(), UserId = userId, CreatedAt = Now(), UpdatedAt = Now() };
            _db.Carts.Add(userCart);
        }

        var ids = guest.Items.Select(i => i.ProductId).ToList();
        var products = await _db.Products
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Include(p => p.Category)
            .ToDictionaryAsync(p => p.Id);

        foreach (var guestItem in guest.Items)
        {
            var product = products[guestItem.ProductId];
            if (GetStatus(product) != CartLineStatus.Available)
            {
                continue;
            }

            var limit = GetLimit(product);
            var existing = userCart.Items.FirstOrDefault(i => i.ProductId == guestItem.ProductId);

            if (existing is not null)
            {
                existing.Quantity = Math.Min(existing.Quantity + guestItem.Quantity, limit);
            }
            else if (userCart.Items.Count < _options.MaxLines)
            {
                userCart.Items.Add(new CartItem
                {
                    CartId = userCart.Id,
                    ProductId = guestItem.ProductId,
                    Quantity = Math.Min(guestItem.Quantity, limit),
                    AddedAt = guestItem.AddedAt
                });
            }
        }

        userCart.UpdatedAt = Now();
        _db.Carts.Remove(guest);
        await _db.SaveChangesAsync();
    }

    public async Task ClearAsync(CartOwner owner)
    {
        var cart = await FindCartAsync(owner);
        if (cart is null)
        {
            return;
        }

        await _db.CartItems.Where(i => i.CartId == cart.Id).ExecuteDeleteAsync();

        cart.UpdatedAt = Now();
        await _db.SaveChangesAsync();
    }

    public async Task<int> DeleteOldGuestCartsAsync(DateTime notTouchedSinceUtc)
    {
        // The items go with their cart (cascade delete in the database).
        return await _db.Carts
            .Where(c => c.UserId == null && c.UpdatedAt < notTouchedSinceUtc)
            .ExecuteDeleteAsync();
    }

    private async Task<Cart?> FindCartAsync(CartOwner owner)
    {
        if (owner.UserId is not null)
        {
            return await _db.Carts.Include(c => c.Items).FirstOrDefaultAsync(c => c.UserId == owner.UserId);
        }

        if (owner.GuestCartId is Guid guestId)
        {
            // A guest cookie can only open a guest cart, never a customer's cart.
            return await _db.Carts.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == guestId && c.UserId == null);
        }

        return null;
    }

    // A signed-in customer gets the 10% price on their first 5 orders; cancelled orders do not count.
    private async Task<(bool Active, int OrdersLeft)> GetCustomerDiscountAsync(CartOwner owner)
    {
        if (owner.UserId is null)
        {
            return (false, 0);
        }

        var used = await _db.Orders.CountAsync(o => o.UserId == owner.UserId && o.Status != OrderStatus.Cancelled);
        var left = CustomerDiscountRules.OrdersLeft(used);

        return (left > 0, left);
    }

    private static CartLineStatus GetStatus(Product product)
    {
        if (product.IsArchived || !product.Category.IsActive)
        {
            return CartLineStatus.NotForSale;
        }

        return product.StockQuantity > 0 ? CartLineStatus.Available : CartLineStatus.OutOfStock;
    }

    private int GetLimit(Product product) => Math.Min(product.StockQuantity, _options.MaxQuantityPerProduct);

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;
}

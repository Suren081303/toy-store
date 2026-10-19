namespace Shop.Core.Catalog;

public static class ProductRules
{
    // Price after the product discount, rounded to a whole AMD (half rounds up).
    public static decimal GetFinalPrice(decimal price, int discountPercent)
    {
        return Math.Round(price * (100 - discountPercent) / 100m, 0, MidpointRounding.AwayFromZero);
    }

    // Registered customers get 10% off the regular price. It does not stack with a product discount:
    // the item gets whichever price is lower, and the customer price counts as applied only when it is strictly lower.
    public const int CustomerDiscountPercent = 10;

    public static decimal GetUnitPrice(decimal price, int discountPercent, bool customerDiscount, out bool customerDiscountApplied)
    {
        var productPrice = GetFinalPrice(price, discountPercent);
        customerDiscountApplied = false;

        if (!customerDiscount)
        {
            return productPrice;
        }

        var customerPrice = GetFinalPrice(price, CustomerDiscountPercent);
        if (customerPrice < productPrice)
        {
            customerDiscountApplied = true;
            return customerPrice;
        }

        return productPrice;
    }

    // A product is "new" when the admin pinned it, or it was created on or after newSince.
    public static bool IsNew(bool isPinnedAsNew, DateTime createdAt, DateTime newSince)
    {
        return isPinnedAsNew || createdAt >= newSince;
    }
}

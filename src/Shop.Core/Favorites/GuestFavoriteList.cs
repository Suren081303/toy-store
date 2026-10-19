namespace Shop.Core.Favorites;

// A guest's favorites live in a cookie as "12,7,40" (newest first). This is the pure logic for that text.
public static class GuestFavoriteList
{
    public const int MaxItems = 100;

    // Unreadable parts, duplicates and anything beyond MaxItems are ignored.
    public static IReadOnlyList<int> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<int>();
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var id) && id > 0 ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .Take(MaxItems)
            .ToList();
    }

    public static string Format(IEnumerable<int> ids)
    {
        return string.Join(',', ids.Distinct().Take(MaxItems));
    }

    // Adds the product at the front, or removes it when it is already there. Returns the new list and whether it is now a favorite.
    public static (IReadOnlyList<int> Ids, bool IsFavorite) Toggle(IReadOnlyList<int> current, int productId)
    {
        if (current.Contains(productId))
        {
            return (current.Where(id => id != productId).ToList(), false);
        }

        var updated = new List<int> { productId };
        updated.AddRange(current);
        return (updated.Take(MaxItems).ToList(), true);
    }
}

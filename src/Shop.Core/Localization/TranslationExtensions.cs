using Shop.Core.Entities;

namespace Shop.Core.Localization;

public static class TranslationExtensions
{
    // Name in the requested culture; if it is missing or empty, the Armenian (default) name.
    public static string ResolveName<T>(this IEnumerable<T> translations, string culture) where T : ITranslation
    {
        var list = translations.ToList();
        return FirstNotEmpty(
            list.FirstOrDefault(t => t.Culture == culture)?.Name,
            list.FirstOrDefault(t => t.Culture == Cultures.Default)?.Name);
    }

    public static string ResolveDescription(this IEnumerable<ProductTranslation> translations, string culture)
    {
        var list = translations.ToList();
        return FirstNotEmpty(
            list.FirstOrDefault(t => t.Culture == culture)?.Description,
            list.FirstOrDefault(t => t.Culture == Cultures.Default)?.Description);
    }

    private static string FirstNotEmpty(string? preferred, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred;
        }

        return string.IsNullOrWhiteSpace(fallback) ? string.Empty : fallback;
    }
}

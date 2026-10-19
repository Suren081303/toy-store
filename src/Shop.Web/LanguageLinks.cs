using Microsoft.AspNetCore.Diagnostics;

namespace Shop.Web;

public static class LanguageLinks
{
    private static readonly string[] Cultures = ["hy", "ru", "en"];

    // "/hy/Products" + "?q=a" in "ru" -> "/ru/Products?q=a". A path without a language gets one added.
    public static string Build(string path, string queryString, string culture)
    {
        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (segments.Count > 0 && Cultures.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
        {
            segments[0] = culture;
        }
        else
        {
            segments.Insert(0, culture);
        }

        return "/" + string.Join('/', segments) + queryString;
    }

    // The language of an address: "/ru/Products" -> "ru". Addresses without a language use Armenian.
    public static string CultureOf(string path)
    {
        var first = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return first is not null && Cultures.Contains(first, StringComparer.OrdinalIgnoreCase)
            ? first.ToLowerInvariant()
            : "hy";
    }

    // The same, for the page being shown. On the 404 page it uses the address the visitor asked for.
    public static string For(HttpContext http, string culture)
    {
        var reExecute = http.Features.Get<IStatusCodeReExecuteFeature>();
        var path = reExecute?.OriginalPath ?? http.Request.Path.Value ?? "/";
        var query = reExecute?.OriginalQueryString ?? http.Request.QueryString.Value ?? string.Empty;

        return Build(path, query, culture);
    }
}

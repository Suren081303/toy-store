using System.Text;
using Microsoft.AspNetCore.Http;

namespace Shop.Web.Seo;

// Addresses and rules for search engines and link previews.
public static class SeoLinks
{
    // Pages that are about one visitor or one action. Search engines should not list them (the robots.txt asks the same).
    public static readonly string[] PrivateControllers = ["Cart", "Checkout", "Account", "Orders", "Payments", "Favorites", "FakeBank"];

    public static readonly string[] Cultures = ["hy", "ru", "en"];

    // The public address of the site: Shop:BaseUrl when set (correct behind any proxy), otherwise the address this request came to.
    public static string Origin(HttpContext http, IConfiguration configuration)
    {
        if (Uri.TryCreate(configuration["Shop:BaseUrl"]?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return uri.GetLeftPart(UriPartial.Authority);
        }

        return $"{http.Request.Scheme}://{http.Request.Host}";
    }

    public static bool IsBaseUrlSet(IConfiguration configuration) =>
        Uri.TryCreate(configuration["Shop:BaseUrl"]?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    // Visitor pages, the 404/error pages, and the gift finder once it holds answers (many addresses, one page) stay out of search results.
    public static bool IsPrivate(string? controller, string? action, bool hasQuery)
    {
        if (controller is not null && PrivateControllers.Contains(controller, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return (controller == "Home" && action is "StatusPage" or "Error")
            || (controller == "Gifts" && hasQuery);
    }

    // The one address a page should be known by. Filters, sorting, paging and search words are left out, so they do not
    // count as different pages; only the category and age group of the product list are kept.
    public static string Canonical(string origin, HttpRequest request, string? controller, string? action)
    {
        var url = new StringBuilder(origin).Append(request.Path.Value);

        if (controller == "Products" && action == "Index")
        {
            var parts = new List<string>();

            foreach (var key in new[] { "category", "age" })
            {
                if (int.TryParse(request.Query[key], out var id) && id > 0)
                {
                    parts.Add($"{key}={id}");
                }
            }

            if (parts.Count > 0)
            {
                url.Append('?').Append(string.Join('&', parts));
            }
        }

        return url.ToString();
    }

    public static string OgLocale(string culture) => culture switch
    {
        "ru" => "ru_RU",
        "en" => "en_US",
        _ => "hy_AM"
    };

    // A short plain-text summary for a description tag: one line, at most `max` characters, cut at a word.
    public static string Summary(string? text, int max = 160)
    {
        var line = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (line.Length <= max)
        {
            return line;
        }

        var cut = line[..max];
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut).TrimEnd(',', ';', ':', '.', '-') + "…";
    }
}

using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Shop.Core.Catalog;
using Shop.Web.Seo;

namespace Shop.Web.Controllers;

// /robots.txt and /sitemap.xml, for search engines.
public class SeoController : Controller
{
    private static readonly XNamespace Sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";

    private readonly ICatalogService _catalog;
    private readonly IConfiguration _configuration;

    public SeoController(ICatalogService catalog, IConfiguration configuration)
    {
        _catalog = catalog;
        _configuration = configuration;
    }

    [HttpGet("robots.txt")]
    public IActionResult Robots()
    {
        var text = new StringBuilder("User-agent: *\n");

        // Everything is open except the admin panel and the pages that belong to one visitor.
        foreach (var culture in SeoLinks.Cultures)
        {
            text.Append($"Disallow: /{culture}/Admin\n");

            foreach (var controller in SeoLinks.PrivateControllers)
            {
                text.Append($"Disallow: /{culture}/{controller}\n");
            }
        }

        text.Append("Disallow: /not-found\nDisallow: /error\n");

        if (SeoLinks.IsBaseUrlSet(_configuration))
        {
            text.Append($"\nSitemap: {SeoLinks.Origin(HttpContext, _configuration)}/sitemap.xml\n");
        }

        return Content(text.ToString(), "text/plain", Encoding.UTF8);
    }

    // Every public page in all three languages, each pointing to its translations. Needs Shop:BaseUrl: a sitemap with
    // addresses that depend on how the request arrived would be wrong behind a proxy, so without it there is no sitemap.
    [HttpGet("sitemap.xml")]
    [ResponseCache(Duration = 3600)]
    public async Task<IActionResult> SiteMap()
    {
        if (!SeoLinks.IsBaseUrlSet(_configuration))
        {
            return NotFound();
        }

        var origin = SeoLinks.Origin(HttpContext, _configuration);
        var data = await _catalog.GetSitemapDataAsync();

        // One entry per page: the same page in each language is a separate address, listed with its translations.
        var pages = new List<Func<string, string?>>
        {
            culture => Url.Action("Index", "Home", new { culture }),
            culture => Url.Action("Index", "Products", new { culture }),
            culture => Url.Action("Index", "Categories", new { culture }),
            culture => Url.Action("Index", "Gifts", new { culture }),
            culture => Url.Action("Delivery", "Legal", new { culture }),
            culture => Url.Action("Terms", "Legal", new { culture }),
            culture => Url.Action("Privacy", "Home", new { culture })
        };

        pages.AddRange(data.CategoryIds.Select<int, Func<string, string?>>(id => culture => Url.Action("Index", "Products", new { culture, category = id })));
        pages.AddRange(data.ProductIds.Select<int, Func<string, string?>>(id => culture => Url.Action("Details", "Products", new { culture, id })));

        var xhtml = XNamespace.Get("http://www.w3.org/1999/xhtml");
        var urlset = new XElement(Sitemap + "urlset", new XAttribute(XNamespace.Xmlns + "xhtml", xhtml));

        foreach (var page in pages)
        {
            foreach (var culture in SeoLinks.Cultures)
            {
                var entry = new XElement(Sitemap + "url", new XElement(Sitemap + "loc", origin + page(culture)));

                foreach (var other in SeoLinks.Cultures)
                {
                    entry.Add(new XElement(xhtml + "link",
                        new XAttribute("rel", "alternate"), new XAttribute("hreflang", other), new XAttribute("href", origin + page(other))));
                }

                urlset.Add(entry);
            }
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), urlset);
        return Content(document.Declaration + Environment.NewLine + document.Root, "application/xml", Encoding.UTF8);
    }
}

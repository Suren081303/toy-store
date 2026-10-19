using System.Net;
using System.Text.RegularExpressions;

namespace Shop.Tests.Web;

[Collection("Web")]
public class GiftsFlowTests : WebTestBase
{
    public GiftsFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    // The "N items" text of the catalog: the whole number of matching products, not only the first page.
    private static int Total(string html)
    {
        var match = Regex.Match(html, @"catalog__count"">\s*(\d+)");
        Assert.True(match.Success, "No item count on the page");
        return int.Parse(match.Groups[1].Value);
    }

    private static int Cards(string html) => Regex.Matches(html, "<article class=\"card\"").Count;

    private static string VisibleText(string html) =>
        Regex.Replace(Regex.Replace(html, "<script[\\s\\S]*?</script>", " "), "<[^>]+>", " ");

    private async Task<string> GetAsync(string url)
    {
        var response = await NewClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BodyAsync(response);
    }

    [Theory]
    [InlineData("hy")]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task TheGiftFinder_Opens_InEveryLanguage_WithAndWithoutResults_AndHasNoUntranslatedKeys(string culture)
    {
        foreach (var query in new[] { "", "?find=true", "?find=true&priceFrom=1000&priceTo=100000", "?priceFrom=99999999&priceTo=100000000&find=true" })
        {
            var html = await GetAsync($"/{culture}/Gifts{query}");
            var text = VisibleText(html);

            Assert.Empty(Regex.Matches(text, @"\b(Gift|Price|Admin|Val|Items|Status)_?[A-Za-z]*_[A-Za-z_]+\b").Select(m => m.Value).Distinct());
            Assert.Contains("name=\"priceFrom\"", html);
            Assert.Contains("name=\"priceTo\"", html);
        }
    }

    [Fact]
    public async Task WithoutAnswers_OnlyTheQuestionsAreShown()
    {
        var html = await GetAsync("/en/Gifts");

        Assert.Equal(0, Cards(html));
        Assert.DoesNotContain("giftfinder__results", html);
        Assert.Contains("name=\"age\"", html);
        Assert.Contains("type=\"checkbox\" name=\"category\"", html);
    }

    [Fact]
    public async Task SkippingEveryQuestion_ShowsPopularToysThatAreInStock()
    {
        var html = await GetAsync("/en/Gifts?find=true");

        Assert.Contains("Popular gifts", html);
        Assert.InRange(Cards(html), 1, 24);
        Assert.DoesNotContain("Out of stock", html);
    }

    [Fact]
    public async Task TheAnswers_NarrowTheResult_AndStayFilledIn()
    {
        var all = Cards(await GetAsync("/en/Gifts?find=true"));

        var html = await GetAsync("/en/Gifts?find=true&priceFrom=1&priceTo=20000&category=1&category=3");

        Assert.Contains("Ideas for you", html);
        Assert.InRange(Cards(html), 0, all);
        Assert.Contains("value=\"20000\"", html);
        Assert.Matches("name=\"category\" value=\"1\" checked", html);
        Assert.Matches("name=\"category\" value=\"3\" checked", html);
        Assert.Contains("1 – 20,000", WebUtility.HtmlDecode(VisibleText(html))); // the budget in words
    }

    [Theory]
    [InlineData("hy", "?find=true&priceTo=40000", "մինչև 40")]
    [InlineData("ru", "?find=true&priceTo=40000", "до 40")]
    [InlineData("en", "?find=true&priceTo=40000", "up to 40,000")]
    [InlineData("en", "?find=true&priceFrom=10000", "from 10,000")]
    public async Task AOneSidedBudget_IsWrittenOutInWords_WithTheAmountFilledIn(string culture, string query, string expected)
    {
        var text = WebUtility.HtmlDecode(VisibleText(await GetAsync($"/{culture}/Gifts{query}"))).Replace(" ", " ");

        Assert.Contains(expected, text);
        Assert.DoesNotContain("{0}", text);
    }

    [Fact]
    public async Task APriceRangeWritten_TheWrongWayRound_IsSwapped()
    {
        var right = await GetAsync("/en/Gifts?find=true&priceFrom=1&priceTo=30000");
        var swapped = await GetAsync("/en/Gifts?find=true&priceFrom=30000&priceTo=1");

        Assert.Equal(Cards(right), Cards(swapped));
        Assert.Contains("value=\"1\"", swapped);
        Assert.Contains("value=\"30000\"", swapped);
    }

    [Fact]
    public async Task WhenNothingFits_ThePageOffersToDropTheBudget_AndToBrowseEverything()
    {
        var html = await GetAsync("/en/Gifts?find=true&age=1&category=2&priceFrom=900000000&priceTo=1000000000");

        Assert.Equal(0, Cards(html));
        Assert.Contains("Nothing fits", html);
        Assert.Contains("href=\"/en/Gifts?find=true&amp;age=1&amp;category=2\"", html); // same answers, no budget
        Assert.Contains("href=\"/en/Products\"", html);
    }

    [Theory]
    [InlineData("?find=true&age=99999&category=99999")]            // nothing like that exists
    [InlineData("?find=true&age=abc&category=xyz&priceFrom=cheap")] // not numbers
    [InlineData("?find=true&priceFrom=-5&priceTo=-1")]
    [InlineData("?find=true&priceFrom=1e999&priceTo=99999999999999999999999999999")]
    public async Task BadInput_IsIgnored_NotAnError(string query)
    {
        var response = await NewClient().GetAsync("/en/Gifts" + query);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.InRange(Cards(await BodyAsync(response)), 1, 24); // as if nothing had been typed
    }

    [Fact]
    public async Task TheGiftFinder_IsLinkedFromTheHomePage_TheMenu_AndAnEmptySearch()
    {
        var home = await GetAsync("/en");
        Assert.Contains("href=\"/en/Gifts\"", home);
        Assert.Contains("Find a gift", home);

        var empty = await GetAsync("/en/Products?q=" + Guid.NewGuid().ToString("N"));
        Assert.Contains("href=\"/en/Gifts\"", empty);
    }

    // ---- The price boxes in the catalog ----

    [Fact]
    public async Task TheCatalog_HasPriceBoxes_AndFiltersByThem()
    {
        var all = await GetAsync("/en/Products");
        Assert.Contains("name=\"priceFrom\"", all);
        Assert.Contains("name=\"priceTo\"", all);

        var count = Total(all);

        // Nothing costs one dram or less, and everything costs at least one dram.
        Assert.Equal(0, Cards(await GetAsync("/en/Products?priceTo=1")));
        Assert.Equal(count, Total(await GetAsync("/en/Products?priceFrom=1&priceTo=900000000")));

        var cheap = Total(await GetAsync("/en/Products?priceTo=20000"));
        var dear = Total(await GetAsync("/en/Products?priceFrom=20001"));
        Assert.Equal(count, cheap + dear); // the two halves add up, nothing is lost or counted twice
        Assert.True(cheap > 0 && dear > 0, "the demo data has toys on both sides of 20000");

        var filtered = await GetAsync("/en/Products?priceFrom=5000&priceTo=20000");
        Assert.Contains("value=\"5000\"", filtered);
        Assert.Contains("value=\"20000\"", filtered);
    }

    [Fact]
    public async Task TheCatalogPriceFilter_IsKeptWhenLoadingMoreProducts()
    {
        var html = await GetAsync("/en/Products?priceFrom=5000&priceTo=900000000&sort=PriceAsc");

        // The "load more" and "previous" links keep the other filters (only shown when there is more than one page).
        foreach (Match link in Regex.Matches(html, "rel=\"next\"[^>]*href=\"([^\"]+)\"|href=\"([^\"]+)\"[^>]*rel=\"next\""))
        {
            var href = WebUtility.HtmlDecode(link.Groups[1].Success ? link.Groups[1].Value : link.Groups[2].Value);
            Assert.Contains("priceFrom=5000", href);
            Assert.Contains("priceTo=900000000", href);
        }
    }
}

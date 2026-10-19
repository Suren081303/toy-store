using Shop.Web;

namespace Shop.Tests;

public class LanguageLinksTests
{
    [Theory]
    [InlineData("/hy/Products", "?q=bear&page=2", "ru", "/ru/Products?q=bear&page=2")]
    [InlineData("/hy", "", "en", "/en")]
    [InlineData("/ru/Products/Details/5", "", "hy", "/hy/Products/Details/5")]
    [InlineData("/EN/Products", "", "ru", "/ru/Products")]
    public void Build_ReplacesTheLanguageSegmentAndKeepsTheRest(string path, string query, string culture, string expected)
    {
        Assert.Equal(expected, LanguageLinks.Build(path, query, culture));
    }

    [Theory]
    [InlineData("/ru/Products", "ru")]
    [InlineData("/EN", "en")]
    [InlineData("/hy/Account/Login", "hy")]
    [InlineData("/", "hy")]
    [InlineData("/xx/Products", "hy")]
    [InlineData("/not-found", "hy")]
    public void CultureOf_ReadsTheLanguageOfAnAddress_OrFallsBackToArmenian(string path, string expected)
    {
        Assert.Equal(expected, LanguageLinks.CultureOf(path));
    }

    [Theory]
    [InlineData("/", "", "ru", "/ru")]
    [InlineData("/not-found", "", "en", "/en/not-found")]
    public void Build_AddsALanguageWhenThePathHasNone(string path, string query, string culture, string expected)
    {
        Assert.Equal(expected, LanguageLinks.Build(path, query, culture));
    }
}

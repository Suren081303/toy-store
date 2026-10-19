using Shop.Core.Entities;
using Shop.Core.Localization;

namespace Shop.Tests;

public class TranslationExtensionsTests
{
    private static List<CategoryTranslation> Categories(params (string Culture, string Name)[] items)
    {
        return items.Select(i => new CategoryTranslation { Culture = i.Culture, Name = i.Name }).ToList();
    }

    [Fact]
    public void ResolveName_UsesRequestedCulture()
    {
        var translations = Categories(("hy", "Հայ"), ("ru", "Рус"));

        Assert.Equal("Рус", translations.ResolveName("ru"));
    }

    [Fact]
    public void ResolveName_MissingCulture_FallsBackToArmenian()
    {
        var translations = Categories(("hy", "Հայ"));

        Assert.Equal("Հայ", translations.ResolveName("en"));
    }

    [Fact]
    public void ResolveName_EmptyCultureName_FallsBackToArmenian()
    {
        var translations = Categories(("hy", "Հայ"), ("ru", "  "));

        Assert.Equal("Հայ", translations.ResolveName("ru"));
    }

    [Fact]
    public void ResolveName_NothingAvailable_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, Categories().ResolveName("hy"));
    }

    [Fact]
    public void ResolveDescription_EmptyCultureDescription_FallsBackToArmenian()
    {
        var translations = new List<ProductTranslation>
        {
            new() { Culture = "hy", Name = "A", Description = "Հայ նկարագրություն" },
            new() { Culture = "en", Name = "B", Description = "" }
        };

        Assert.Equal("Հայ նկարագրություն", translations.ResolveDescription("en"));
    }
}

using Shop.Core.Localization;

namespace Shop.Tests;

public class PluralsTests
{
    [Theory]
    [InlineData(1, PluralForm.One)]
    [InlineData(21, PluralForm.One)]
    [InlineData(101, PluralForm.One)]
    [InlineData(2, PluralForm.Few)]
    [InlineData(3, PluralForm.Few)]
    [InlineData(4, PluralForm.Few)]
    [InlineData(22, PluralForm.Few)]
    [InlineData(5, PluralForm.Many)]
    [InlineData(11, PluralForm.Many)]
    [InlineData(12, PluralForm.Many)]
    [InlineData(14, PluralForm.Many)]
    [InlineData(20, PluralForm.Many)]
    [InlineData(111, PluralForm.Many)]
    [InlineData(0, PluralForm.Many)]
    public void Russian_UsesThreeForms(int number, PluralForm expected)
    {
        Assert.Equal(expected, Plurals.GetForm("ru", number));
    }

    [Theory]
    [InlineData(1, PluralForm.One)]
    [InlineData(0, PluralForm.Many)]
    [InlineData(2, PluralForm.Many)]
    [InlineData(22, PluralForm.Many)]
    public void English_UsesSingularOnlyForOne(int number, PluralForm expected)
    {
        Assert.Equal(expected, Plurals.GetForm("en", number));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Armenian_UsesTheSameFormForEveryNumber(int number)
    {
        Assert.Equal(PluralForm.Many, Plurals.GetForm("hy", number));
    }
}

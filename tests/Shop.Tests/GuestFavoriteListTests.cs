using Shop.Core.Favorites;

namespace Shop.Tests;

public class GuestFavoriteListTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyValue_ReturnsNothing(string? value)
    {
        Assert.Empty(GuestFavoriteList.Parse(value));
    }

    [Fact]
    public void Parse_ReadsIdsInOrder()
    {
        Assert.Equal(new[] { 5, 3, 9 }, GuestFavoriteList.Parse("5,3,9"));
    }

    [Fact]
    public void Parse_IgnoresGarbageZeroNegativeAndDuplicates()
    {
        Assert.Equal(new[] { 5, 7 }, GuestFavoriteList.Parse("a,,5, 7 ,-1,0,5,99999999999"));
    }

    [Fact]
    public void Parse_KeepsOnlyTheFirstHundred()
    {
        var value = string.Join(',', Enumerable.Range(1, 150));

        var ids = GuestFavoriteList.Parse(value);

        Assert.Equal(GuestFavoriteList.MaxItems, ids.Count);
        Assert.Equal(1, ids[0]);
        Assert.Equal(100, ids[^1]);
    }

    [Fact]
    public void Format_ThenParse_ReturnsTheSameIds()
    {
        var ids = new[] { 12, 7, 40 };

        Assert.Equal(ids, GuestFavoriteList.Parse(GuestFavoriteList.Format(ids)));
        Assert.Equal("12,7,40", GuestFavoriteList.Format(ids));
    }

    [Fact]
    public void Toggle_AddsAtTheFront_ThenRemoves()
    {
        var (added, isFavorite) = GuestFavoriteList.Toggle(new[] { 5, 3 }, 9);
        Assert.True(isFavorite);
        Assert.Equal(new[] { 9, 5, 3 }, added);

        var (removed, isStillFavorite) = GuestFavoriteList.Toggle(added, 5);
        Assert.False(isStillFavorite);
        Assert.Equal(new[] { 9, 3 }, removed);
    }

    [Fact]
    public void Toggle_WhenTheListIsFull_DropsTheOldestOne()
    {
        var full = Enumerable.Range(1, GuestFavoriteList.MaxItems).ToList();

        var (updated, isFavorite) = GuestFavoriteList.Toggle(full, 1000);

        Assert.True(isFavorite);
        Assert.Equal(GuestFavoriteList.MaxItems, updated.Count);
        Assert.Equal(1000, updated[0]);
        Assert.DoesNotContain(GuestFavoriteList.MaxItems, updated);
    }
}

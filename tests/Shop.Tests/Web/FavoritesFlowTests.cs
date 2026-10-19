using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Infrastructure.Data;

namespace Shop.Tests.Web;

[Collection("Web")]
public class FavoritesFlowTests : WebTestBase
{
    public FavoritesFlowTests(ShopWebFixture fixture) : base(fixture)
    {
    }

    private async Task<int> AProductIdAsync(int skip = 0)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        return await db.Products
            .Where(p => !p.IsArchived && p.Category.IsActive)
            .OrderBy(p => p.Id)
            .Skip(skip)
            .Select(p => p.Id)
            .FirstAsync();
    }

    private static Task<HttpResponseMessage> ToggleAsync(HttpClient client, int productId)
    {
        return PostJsonAsync(client, "/en/Favorites/Toggle", new Dictionary<string, string> { ["productId"] = productId.ToString() });
    }

    [Fact]
    public async Task Toggle_AddsAndRemovesAFavorite_AndTheFavoritesPageFollows()
    {
        var client = NewClient();
        await SignedInUserAsync(client);
        var productId = await AProductIdAsync();
        var link = $"/en/Products/Details/{productId}";

        var added = await ToggleAsync(client, productId);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Contains("\"isFavorite\":true", await BodyAsync(added));
        Assert.Contains(link, await client.GetStringAsync("/en/Favorites"));

        // The heart on the product page is filled.
        Assert.Contains("fav-btn is-active", await client.GetStringAsync(link));

        var removed = await ToggleAsync(client, productId);
        Assert.Contains("\"isFavorite\":false", await BodyAsync(removed));
        Assert.DoesNotContain(link, await client.GetStringAsync("/en/Favorites"));
    }

    [Fact]
    public async Task Favorites_BelongToTheirOwner()
    {
        var owner = NewClient();
        await SignedInUserAsync(owner);
        var productId = await AProductIdAsync(skip: 1);
        await ToggleAsync(owner, productId);

        var other = NewClient();
        await SignedInUserAsync(other);

        Assert.DoesNotContain($"/en/Products/Details/{productId}", await other.GetStringAsync("/en/Favorites"));
        Assert.Contains($"/en/Products/Details/{productId}", await owner.GetStringAsync("/en/Favorites"));
    }

    [Fact]
    public async Task Toggle_UnknownProduct_Returns404()
    {
        var client = NewClient();
        await SignedInUserAsync(client);

        var response = await ToggleAsync(client, int.MaxValue);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Toggle_AsAGuest_WorksThroughACookie()
    {
        var client = NewClient();
        var productId = await AProductIdAsync();

        var response = await ToggleAsync(client, productId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"isFavorite\":true", await BodyAsync(response));
        Assert.Equal(productId.ToString(), CookieValue(response, "Shop.Favs"));
    }

    [Fact]
    public async Task FavoritesPage_AsAGuest_ShowsTheGuestList_AndAnInviteToSignIn()
    {
        var client = NewClient();
        var productId = await AProductIdAsync(skip: 2);
        await ToggleAsync(client, productId);

        var response = await client.GetAsync("/en/Favorites");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await BodyAsync(response);
        Assert.Contains($"/en/Products/Details/{productId}", html);
        Assert.Contains("Sign in to keep your favorites", html);
    }

    [Fact]
    public async Task TheHeartForAGuest_IsAToggleButton_AndShowsTheGuestsChoice()
    {
        var client = NewClient();
        var productId = await AProductIdAsync(skip: 3);

        var before = await client.GetStringAsync($"/en/Products/Details/{productId}");
        Assert.Contains($"data-fav-toggle=\"{productId}\"", before);
        Assert.DoesNotContain("fav-btn is-active", before);

        await ToggleAsync(client, productId);

        Assert.Contains("fav-btn is-active", await client.GetStringAsync($"/en/Products/Details/{productId}"));
    }

    [Fact]
    public async Task AGuestsFavorites_AreRemovedFromTheCookie_WhenTheyToggleThemOff()
    {
        var client = NewClient();
        var productId = await AProductIdAsync(skip: 4);
        await ToggleAsync(client, productId);

        var removed = await ToggleAsync(client, productId);

        Assert.Contains("\"isFavorite\":false", await BodyAsync(removed));
        Assert.Equal(string.Empty, CookieValue(removed, "Shop.Favs"));
    }
}

using Microsoft.EntityFrameworkCore;
using Shop.Core.Entities;
using Shop.Infrastructure.Data;

namespace Shop.Tests;

public class ShopDbContextTests
{
    [Fact]
    public void Model_ContainsApplicationUser()
    {
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer("Server=unused;Database=unused")
            .Options;

        using var context = new ShopDbContext(options);

        Assert.NotNull(context.Model.FindEntityType(typeof(ApplicationUser)));
    }
}

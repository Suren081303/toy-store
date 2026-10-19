using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Shop.Core.Entities;
using Shop.Core.Identity;

namespace Shop.Infrastructure.Identity;

public static class IdentitySeeder
{
    // Creates the Admin and Customer roles, and the first admin account when Admin:Email and Admin:Password are configured.
    public static async Task SeedAsync(
        RoleManager<IdentityRole> roles,
        UserManager<ApplicationUser> users,
        AdminOptions admin,
        ILogger logger)
    {
        foreach (var role in new[] { Roles.Admin, Roles.Customer })
        {
            if (!await roles.RoleExistsAsync(role))
            {
                var created = await roles.CreateAsync(new IdentityRole(role));
                ThrowIfFailed(created, $"create role {role}");
            }
        }

        if (string.IsNullOrWhiteSpace(admin.Email) || string.IsNullOrWhiteSpace(admin.Password))
        {
            logger.LogWarning("Admin:Email or Admin:Password is not configured, so no admin account was created.");
            return;
        }

        var user = await users.FindByEmailAsync(admin.Email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = admin.Email,
                Email = admin.Email,
                EmailConfirmed = true,
                FullName = "Administrator",
                CreatedAt = DateTime.UtcNow
            };

            ThrowIfFailed(await users.CreateAsync(user, admin.Password), $"create admin {admin.Email}");
        }

        if (!await users.IsInRoleAsync(user, Roles.Admin))
        {
            ThrowIfFailed(await users.AddToRoleAsync(user, Roles.Admin), $"add {admin.Email} to the Admin role");
        }
    }

    private static void ThrowIfFailed(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not {action}: " + string.Join("; ", result.Errors.Select(e => $"{e.Code} {e.Description}")));
        }
    }
}

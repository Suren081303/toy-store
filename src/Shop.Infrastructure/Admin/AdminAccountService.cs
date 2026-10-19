using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shop.Core.Admin;
using Shop.Core.Entities;
using Shop.Core.Identity;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Admin;

public class AdminAccountService : IAdminAccountService
{
    private readonly ShopDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly AdminOptions _options;
    private readonly TimeProvider _time;

    public AdminAccountService(ShopDbContext db, UserManager<ApplicationUser> users, IOptions<AdminOptions> options, TimeProvider time)
    {
        _db = db;
        _users = users;
        _options = options.Value;
        _time = time;
    }

    public async Task<IReadOnlyList<AdminAccountRow>> ListAsync()
    {
        var admins = await _users.GetUsersInRoleAsync(Roles.Admin);

        return admins
            .OrderBy(u => u.CreatedAt).ThenBy(u => u.Email)
            .Select(u => new AdminAccountRow(u.Id, u.Email ?? string.Empty, u.FullName, u.CreatedAt, IsConfigured(u)))
            .ToList();
    }

    public async Task<GrantAdminResult> GrantAsync(string email, string byAdminId)
    {
        var user = await _users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            return GrantAdminResult.NotFound;
        }

        if (!user.EmailConfirmed)
        {
            return GrantAdminResult.NotConfirmed;
        }

        // The role and the audit line are saved together (UserManager uses this same database connection).
        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _db.AcquireAsync("admins");

        if (await _users.IsInRoleAsync(user, Roles.Admin))
        {
            return GrantAdminResult.AlreadyAdmin;
        }

        ThrowIfFailed(await _users.AddToRoleAsync(user, Roles.Admin), $"give {user.Email} the Admin role");

        await AuditWriter.AddAsync(_db, byAdminId, Now(), "Admin.Granted", "User", user.Id, user.Email);
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return GrantAdminResult.Granted;
    }

    public async Task<RevokeAdminResult> RevokeAsync(string userId, string byAdminId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();

        // One removal at a time: two admins removing each other at the same moment cannot leave nobody.
        await _db.AcquireAsync("admins");

        var user = await _users.FindByIdAsync(userId);
        if (user is null)
        {
            return RevokeAdminResult.NotFound;
        }

        if (!await _users.IsInRoleAsync(user, Roles.Admin))
        {
            return RevokeAdminResult.NotAnAdmin;
        }

        if (user.Id == byAdminId)
        {
            return RevokeAdminResult.Yourself;
        }

        if (IsConfigured(user))
        {
            return RevokeAdminResult.ConfiguredAdmin;
        }

        if ((await _users.GetUsersInRoleAsync(Roles.Admin)).Count <= 1)
        {
            return RevokeAdminResult.LastAdmin;
        }

        ThrowIfFailed(await _users.RemoveFromRoleAsync(user, Roles.Admin), $"remove {user.Email} from the Admin role");

        // Signs the person out of their other sessions as soon as the site checks the stamp.
        ThrowIfFailed(await _users.UpdateSecurityStampAsync(user), $"update the security stamp of {user.Email}");

        await AuditWriter.AddAsync(_db, byAdminId, Now(), "Admin.Revoked", "User", user.Id, user.Email);
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return RevokeAdminResult.Revoked;
    }

    public Task<bool> IsAdminAsync(string userId)
    {
        return _db.UserRoles.AnyAsync(ur => ur.UserId == userId && _db.Roles.Any(r => r.Id == ur.RoleId && r.Name == Roles.Admin));
    }

    private bool IsConfigured(ApplicationUser user)
    {
        return !string.IsNullOrWhiteSpace(_options.Email)
            && string.Equals(user.Email, _options.Email, StringComparison.OrdinalIgnoreCase);
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private static void ThrowIfFailed(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not {action}: " + string.Join("; ", result.Errors.Select(e => $"{e.Code} {e.Description}")));
        }
    }
}

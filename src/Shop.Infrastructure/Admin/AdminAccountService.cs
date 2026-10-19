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
            .Select(ToRow)
            .ToList();
    }

    public async Task<AdminAccountRow?> GetAsync(string userId)
    {
        var user = await _users.FindByIdAsync(userId);

        return user is not null && await _users.IsInRoleAsync(user, Roles.Admin) ? ToRow(user) : null;
    }

    public async Task<AdminSaveResult> CreateAsync(AdminAccountInput input, string byAdminId)
    {
        var email = input.Email.Trim();

        if (await _users.FindByEmailAsync(email) is not null)
        {
            return new AdminSaveResult(AdminSaveStatus.EmailTaken);
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            // Created by an admin in the panel: nobody has to open an email first.
            EmailConfirmed = true,
            FullName = input.FullName.Trim(),
            PhoneNumber = input.Phone.Trim(),
            CreatedAt = Now()
        };

        // The account, its role and the audit line are saved together (UserManager uses this same database connection).
        await using var transaction = await _db.Database.BeginTransactionAsync();

        var created = await _users.CreateAsync(user, input.Password ?? string.Empty);
        if (!created.Succeeded)
        {
            return Refused(created);
        }

        ThrowIfFailed(await _users.AddToRoleAsync(user, Roles.Admin), $"give {email} the Admin role");

        await AuditWriter.AddAsync(_db, byAdminId, Now(), "Admin.Created", "User", user.Id, email);
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return new AdminSaveResult(AdminSaveStatus.Saved, user.Id);
    }

    public async Task<AdminSaveResult> UpdateAsync(string userId, AdminAccountInput input, string byAdminId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();

        var user = await _users.FindByIdAsync(userId);
        if (user is null)
        {
            return new AdminSaveResult(AdminSaveStatus.NotFound);
        }

        if (!await _users.IsInRoleAsync(user, Roles.Admin))
        {
            return new AdminSaveResult(AdminSaveStatus.NotAnAdmin);
        }

        var email = input.Email.Trim();
        var oldEmail = user.Email ?? string.Empty;
        var emailChanged = !string.Equals(oldEmail, email, StringComparison.OrdinalIgnoreCase);

        if (emailChanged && IsConfigured(user))
        {
            return new AdminSaveResult(AdminSaveStatus.ConfiguredEmail);
        }

        if (emailChanged && await _users.FindByEmailAsync(email) is { } other && other.Id != user.Id)
        {
            return new AdminSaveResult(AdminSaveStatus.EmailTaken);
        }

        var changed = new List<string>();
        var name = input.FullName.Trim();
        var phone = input.Phone.Trim();

        if (user.FullName != name)
        {
            user.FullName = name;
            changed.Add("name");
        }

        if ((user.PhoneNumber ?? string.Empty) != phone)
        {
            user.PhoneNumber = phone;
            changed.Add("phone");
        }

        if (emailChanged)
        {
            // The email is also the user name (the sign-in name), so both change together.
            user.Email = email;
            user.UserName = email;
            changed.Add($"email ({oldEmail} -> {email})");
        }

        var saved = await _users.UpdateAsync(user);
        if (!saved.Succeeded)
        {
            return Refused(saved);
        }

        var passwordChanged = !string.IsNullOrEmpty(input.Password);
        if (passwordChanged)
        {
            var token = await _users.GeneratePasswordResetTokenAsync(user);
            var reset = await _users.ResetPasswordAsync(user, token, input.Password!);
            if (!reset.Succeeded)
            {
                return Refused(reset);
            }

            changed.Add("password");
        }

        // A new password or email signs the person out of their other sessions (not yourself: you are in the middle of working).
        if ((passwordChanged || emailChanged) && user.Id != byAdminId)
        {
            ThrowIfFailed(await _users.UpdateSecurityStampAsync(user), $"update the security stamp of {user.Email}");
        }

        if (changed.Count > 0)
        {
            // Never the password itself, only that it was changed.
            await AuditWriter.AddAsync(_db, byAdminId, Now(), "Admin.Updated", "User", user.Id, string.Join("; ", changed));
            await _db.SaveChangesAsync();
        }

        await transaction.CommitAsync();

        return new AdminSaveResult(AdminSaveStatus.Saved, user.Id);
    }

    public async Task<DeleteAdminResult> DeleteAsync(string userId, string byAdminId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();

        // One deletion at a time: two admins deleting each other at the same moment cannot leave nobody.
        await _db.AcquireAsync("admins");

        var user = await _users.FindByIdAsync(userId);
        if (user is null)
        {
            return DeleteAdminResult.NotFound;
        }

        if (!await _users.IsInRoleAsync(user, Roles.Admin))
        {
            return DeleteAdminResult.NotAnAdmin;
        }

        if (IsConfigured(user))
        {
            return DeleteAdminResult.ConfiguredAdmin;
        }

        if ((await _users.GetUsersInRoleAsync(Roles.Admin)).Count <= 1)
        {
            return DeleteAdminResult.LastAdmin;
        }

        // The audit line keeps the email, so the history still says who it was. Orders the account placed stay (the database
        // clears their user link); favorites and the cart go with the account.
        await AuditWriter.AddAsync(_db, byAdminId, Now(), "Admin.Deleted", "User", user.Id, user.Email);
        ThrowIfFailed(await _users.DeleteAsync(user), $"delete {user.Email}");
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return DeleteAdminResult.Deleted;
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

    private AdminAccountRow ToRow(ApplicationUser u) =>
        new(u.Id, u.Email ?? string.Empty, u.FullName, u.PhoneNumber ?? string.Empty, u.CreatedAt, IsConfigured(u));

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    // Identity said no. A taken email and a weak password are things the admin can fix on the page; anything else is a bug.
    private static AdminSaveResult Refused(IdentityResult result)
    {
        if (result.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName"))
        {
            return new AdminSaveResult(AdminSaveStatus.EmailTaken);
        }

        if (result.Errors.All(e => e.Code.StartsWith("Password", StringComparison.Ordinal)))
        {
            return new AdminSaveResult(AdminSaveStatus.WeakPassword, Errors: result.Errors.Select(e => e.Description).ToList());
        }

        throw new InvalidOperationException(
            "Identity refused the change: " + string.Join("; ", result.Errors.Select(e => $"{e.Code} {e.Description}")));
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

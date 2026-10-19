namespace Shop.Core.Admin;

// ---- Admin accounts ----

// IsConfigured: this is the account named in the configuration (Admin:Email). The site gives it the Admin role again at every start
// (and creates it again if it is missing), so it cannot be deleted and its email cannot be changed in the panel.
public record AdminAccountRow(string Id, string Email, string FullName, string Phone, DateTime CreatedAt, bool IsConfigured);

// What the admin types on the "new admin" and "edit admin" pages. Password: required for a new admin; on edit,
// empty means "keep the current password".
public class AdminAccountInput
{
    public string FullName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public string? Password { get; set; }
}

public enum AdminSaveStatus
{
    Saved,

    // Another account already uses this email.
    EmailTaken,

    // Identity refused the password (too short, no digit, ...); the reasons are in AdminSaveResult.Errors.
    WeakPassword,

    NotFound,

    NotAnAdmin,

    // The main admin's email is the one in the site configuration, so it cannot be changed here.
    ConfiguredEmail
}

public record AdminSaveResult(AdminSaveStatus Status, string? UserId = null, IReadOnlyList<string>? Errors = null);

public enum DeleteAdminResult
{
    Deleted,

    NotFound,

    NotAnAdmin,

    ConfiguredAdmin,

    // The last admin would be gone (an admin may delete their own account, but not when they are the last one).
    LastAdmin
}

public interface IAdminAccountService
{
    Task<IReadOnlyList<AdminAccountRow>> ListAsync();

    Task<AdminAccountRow?> GetAsync(string userId);

    // A new admin account: email confirmed at once (nobody has to open an email), only the Admin role (it is not a customer account).
    Task<AdminSaveResult> CreateAsync(AdminAccountInput input, string byAdminId);

    // Name, phone, email and (optionally) a new password. A new password or email signs the person out of their other sessions.
    Task<AdminSaveResult> UpdateAsync(string userId, AdminAccountInput input, string byAdminId);

    // Deletes the account. Orders it placed stay (they keep the contact details written on them), favorites and the cart go.
    Task<DeleteAdminResult> DeleteAsync(string userId, string byAdminId);

    // Is this user an admin right now? Read from the database, not from the sign-in cookie, so a removed admin is stopped at once.
    Task<bool> IsAdminAsync(string userId);
}

// ---- Audit log ----

public record AuditRow(int Id, DateTime At, string UserEmail, string Action, string EntityType, string EntityId, string? Details);

public class AuditFilter
{
    // Part of the admin's email, the action, the entity id or the details.
    public string? Query { get; set; }

    // For example "Order", "Product", "Settings".
    public string? EntityType { get; set; }

    // Armenian calendar days.
    public DateOnly? From { get; set; }

    public DateOnly? To { get; set; }

    public int Page { get; set; } = 1;
}

public interface IAdminAuditService
{
    // Newest first, 50 per page.
    Task<PagedResult<AuditRow>> ListAsync(AuditFilter filter);

    // The kinds of things that appear in the log (for the filter box).
    Task<IReadOnlyList<string>> EntityTypesAsync();
}

namespace Shop.Core.Admin;

// ---- Admin accounts ----

// IsConfigured: this is the account named in the configuration (Admin:Email). The site gives it the Admin role again at every start,
// so it cannot be removed in the panel.
public record AdminAccountRow(string Id, string Email, string FullName, DateTime CreatedAt, bool IsConfigured);

public enum GrantAdminResult
{
    Granted,

    // No account with this email.
    NotFound,

    // The account's email was never confirmed, so it is not proven to belong to this person.
    NotConfirmed,

    AlreadyAdmin
}

public enum RevokeAdminResult
{
    Revoked,

    NotFound,

    NotAnAdmin,

    // An admin cannot remove their own access (another admin can do it).
    Yourself,

    ConfiguredAdmin,

    // The last admin would be gone.
    LastAdmin
}

public interface IAdminAccountService
{
    Task<IReadOnlyList<AdminAccountRow>> ListAsync();

    // Gives an existing, email-confirmed account the Admin role.
    Task<GrantAdminResult> GrantAsync(string email, string byAdminId);

    Task<RevokeAdminResult> RevokeAsync(string userId, string byAdminId);

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

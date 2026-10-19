using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;

namespace Shop.Web.Identity;

// Identity's error messages in the visitor's language. Keys in the resource files are "Err_" + the error code.
public class LocalizedIdentityErrorDescriber : IdentityErrorDescriber
{
    private readonly IStringLocalizer<SharedResource> _localizer;

    public LocalizedIdentityErrorDescriber(IStringLocalizer<SharedResource> localizer)
    {
        _localizer = localizer;
    }

    private IdentityError Error(string code, params object[] arguments)
    {
        return new IdentityError { Code = code, Description = _localizer["Err_" + code, arguments] };
    }

    public override IdentityError DefaultError() => Error(nameof(DefaultError));

    public override IdentityError ConcurrencyFailure() => Error(nameof(ConcurrencyFailure));

    public override IdentityError PasswordMismatch() => Error(nameof(PasswordMismatch));

    public override IdentityError InvalidToken() => Error(nameof(InvalidToken));

    public override IdentityError LoginAlreadyAssociated() => Error(nameof(LoginAlreadyAssociated));

    public override IdentityError InvalidUserName(string? userName) => Error(nameof(InvalidUserName));

    public override IdentityError InvalidEmail(string? email) => Error(nameof(InvalidEmail));

    public override IdentityError DuplicateUserName(string userName) => Error(nameof(DuplicateEmail));

    public override IdentityError DuplicateEmail(string email) => Error(nameof(DuplicateEmail));

    public override IdentityError InvalidRoleName(string? role) => Error(nameof(DefaultError));

    public override IdentityError DuplicateRoleName(string role) => Error(nameof(DefaultError));

    public override IdentityError UserAlreadyHasPassword() => Error(nameof(DefaultError));

    public override IdentityError UserLockoutNotEnabled() => Error(nameof(DefaultError));

    public override IdentityError UserAlreadyInRole(string role) => Error(nameof(DefaultError));

    public override IdentityError UserNotInRole(string role) => Error(nameof(DefaultError));

    public override IdentityError PasswordTooShort(int length) => Error(nameof(PasswordTooShort), length);

    public override IdentityError PasswordRequiresNonAlphanumeric() => Error(nameof(DefaultError));

    public override IdentityError PasswordRequiresDigit() => Error(nameof(PasswordRequiresDigit));

    public override IdentityError PasswordRequiresLower() => Error(nameof(DefaultError));

    public override IdentityError PasswordRequiresUpper() => Error(nameof(DefaultError));

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => Error(nameof(DefaultError));

    public override IdentityError RecoveryCodeRedemptionFailed() => Error(nameof(DefaultError));
}

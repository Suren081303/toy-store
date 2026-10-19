using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;
using Shop.Core.Entities;

namespace Shop.Web.Identity;

// Password rule: at least one letter (any alphabet) and one digit. The length is checked by Identity's own validator.
public class LetterAndDigitPasswordValidator : IPasswordValidator<ApplicationUser>
{
    private readonly IStringLocalizer<SharedResource> _localizer;

    public LetterAndDigitPasswordValidator(IStringLocalizer<SharedResource> localizer)
    {
        _localizer = localizer;
    }

    public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user, string? password)
    {
        var errors = new List<IdentityError>();

        if (string.IsNullOrEmpty(password) || !password.Any(char.IsLetter))
        {
            errors.Add(new IdentityError { Code = "PasswordRequiresLetter", Description = _localizer["Err_PasswordRequiresLetter"] });
        }

        if (string.IsNullOrEmpty(password) || !password.Any(char.IsDigit))
        {
            errors.Add(new IdentityError { Code = "PasswordRequiresDigit", Description = _localizer["Err_PasswordRequiresDigit"] });
        }

        return Task.FromResult(errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed(errors.ToArray()));
    }
}

using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Localization;

namespace Shop.Web.Validation;

// MVC translates the error text of the built-in attributes (Required, EmailAddress...) by itself,
// but not of custom ones. This base class translates ErrorMessage (a key in SharedResource.*.resx) when the check fails.
public abstract class LocalizedValidationAttribute : ValidationAttribute
{
    protected abstract bool IsValidValue(object? value);

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (IsValidValue(value))
        {
            return ValidationResult.Success;
        }

        var localizer = (IStringLocalizer<SharedResource>)validationContext.GetService(typeof(IStringLocalizer<SharedResource>))!;
        var message = ErrorMessage is null ? FormatErrorMessage(validationContext.DisplayName) : localizer[ErrorMessage, validationContext.DisplayName].Value;

        return new ValidationResult(message);
    }
}

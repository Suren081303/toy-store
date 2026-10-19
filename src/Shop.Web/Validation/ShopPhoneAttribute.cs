using Shop.Core.Phones;

namespace Shop.Web.Validation;

// A valid Armenian number (091 123456, +374 91 123456) or an international one starting with + or 00.
[AttributeUsage(AttributeTargets.Property)]
public class ShopPhoneAttribute : LocalizedValidationAttribute
{
    protected override bool IsValidValue(object? value)
    {
        // An empty value is left to [Required].
        if (value is null or "")
        {
            return true;
        }

        return value is string text && PhoneNumbers.TryNormalize(text, out _);
    }
}

namespace Shop.Web.Validation;

// For a consent checkbox that must be ticked.
[AttributeUsage(AttributeTargets.Property)]
public class MustBeTrueAttribute : LocalizedValidationAttribute
{
    protected override bool IsValidValue(object? value) => value is true;
}

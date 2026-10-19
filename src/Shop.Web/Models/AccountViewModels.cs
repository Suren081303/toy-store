using System.ComponentModel.DataAnnotations;
using Shop.Web.Validation;

namespace Shop.Web.Models;

// ErrorMessage and Display Name values are keys in Resources/SharedResource.*.resx; they are translated when shown.

public class RegisterViewModel
{
    [Display(Name = "Label_FullName")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(100, ErrorMessage = "Val_MaxLength")]
    public string FullName { get; set; } = string.Empty;

    [Display(Name = "Label_Email")]
    [Required(ErrorMessage = "Val_Required")]
    [EmailAddress(ErrorMessage = "Val_Email")]
    [StringLength(256, ErrorMessage = "Val_MaxLength")]
    public string Email { get; set; } = string.Empty;

    [Display(Name = "Label_Phone")]
    [Required(ErrorMessage = "Val_Required")]
    [ShopPhone(ErrorMessage = "Val_Phone")]
    public string Phone { get; set; } = string.Empty;

    [Display(Name = "Label_Password")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(100, ErrorMessage = "Val_MaxLength")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Label_AcceptTerms")]
    [MustBeTrue(ErrorMessage = "Val_Terms")]
    public bool AcceptTerms { get; set; }
}

public class LoginViewModel
{
    [Display(Name = "Label_Email")]
    [Required(ErrorMessage = "Val_Required")]
    [EmailAddress(ErrorMessage = "Val_Email")]
    public string Email { get; set; } = string.Empty;

    [Display(Name = "Label_Password")]
    [Required(ErrorMessage = "Val_Required")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Label_RememberMe")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }

    // True after a correct password for an account whose email is not confirmed yet (shows the "resend" link).
    public bool ShowResend { get; set; }
}

public class EmailOnlyViewModel
{
    [Display(Name = "Label_Email")]
    [Required(ErrorMessage = "Val_Required")]
    [EmailAddress(ErrorMessage = "Val_Email")]
    public string Email { get; set; } = string.Empty;
}

public class ResetPasswordViewModel
{
    public string Email { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    [Display(Name = "Label_NewPassword")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(100, ErrorMessage = "Val_MaxLength")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;
}

public class ProfileViewModel
{
    [Display(Name = "Label_FullName")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(100, ErrorMessage = "Val_MaxLength")]
    public string FullName { get; set; } = string.Empty;

    [Display(Name = "Label_Phone")]
    [Required(ErrorMessage = "Val_Required")]
    [ShopPhone(ErrorMessage = "Val_Phone")]
    public string Phone { get; set; } = string.Empty;

    // Shown but not editable.
    public string Email { get; set; } = string.Empty;
}

public class ChangePasswordViewModel
{
    [Display(Name = "Label_CurrentPassword")]
    [Required(ErrorMessage = "Val_Required")]
    [DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Display(Name = "Label_NewPassword")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(100, ErrorMessage = "Val_MaxLength")]
    [DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;
}

// A simple result page: title, text and a few buttons/links. Resource keys are resolved by the controller.
public class MessageViewModel
{
    public string Title { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<MessageLink> Links { get; init; } = Array.Empty<MessageLink>();
}

public record MessageLink(string Label, string Url);

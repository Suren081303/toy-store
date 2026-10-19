using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Shop.Core.Carts;
using Shop.Core.Orders;
using Shop.Web.Validation;

namespace Shop.Web.Models;

// ErrorMessage and Display Name values are keys in Resources/SharedResource.*.resx; they are translated when shown.
public class CheckoutViewModel
{
    // ---- Fields posted by the form ----

    public Guid CheckoutId { get; set; }

    [Display(Name = "Label_FullName")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(100, ErrorMessage = "Val_MaxLength")]
    public string CustomerName { get; set; } = string.Empty;

    [Display(Name = "Label_Email")]
    [Required(ErrorMessage = "Val_Required")]
    [EmailAddress(ErrorMessage = "Val_Email")]
    [StringLength(256, ErrorMessage = "Val_MaxLength")]
    public string Email { get; set; } = string.Empty;

    [Display(Name = "Label_Phone")]
    [Required(ErrorMessage = "Val_Required")]
    [ShopPhone(ErrorMessage = "Val_Phone")]
    public string Phone { get; set; } = string.Empty;

    [Display(Name = "Label_Region")]
    [Required(ErrorMessage = "Val_Required")]
    public string Region { get; set; } = string.Empty;

    [Display(Name = "Label_City")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(100, ErrorMessage = "Val_MaxLength")]
    public string City { get; set; } = string.Empty;

    [Display(Name = "Label_Street")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(150, ErrorMessage = "Val_MaxLength")]
    public string Street { get; set; } = string.Empty;

    [Display(Name = "Label_Building")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(30, ErrorMessage = "Val_MaxLength")]
    public string Building { get; set; } = string.Empty;

    [Display(Name = "Label_Apartment")]
    [StringLength(30, ErrorMessage = "Val_MaxLength")]
    public string? Apartment { get; set; }

    [Display(Name = "Label_OrderComment")]
    [StringLength(500, ErrorMessage = "Val_MaxLength")]
    public string? Comment { get; set; }

    // "yyyy-MM-dd" from the date list.
    [Display(Name = "Label_DeliveryDate")]
    [Required(ErrorMessage = "Val_Required")]
    public string? DeliveryDate { get; set; }

    [Display(Name = "Label_DeliverySlot")]
    [Required(ErrorMessage = "Val_Required")]
    public int? DeliverySlotId { get; set; }

    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.CashOnDelivery;

    [Display(Name = "Label_AcceptTerms")]
    [MustBeTrue(ErrorMessage = "Val_Terms")]
    public bool AcceptTerms { get; set; }

    // The total shown on the page, posted back so a price change in between can be noticed.
    public decimal ExpectedTotal { get; set; }

    // ---- Shown on the page, never posted ----

    [BindNever, ValidateNever]
    public CartView Cart { get; set; } = CartView.Empty(false);

    [BindNever, ValidateNever]
    public decimal DeliveryFee { get; set; }

    [BindNever, ValidateNever]
    public decimal FreeDeliveryFrom { get; set; }

    [BindNever, ValidateNever]
    public decimal Total { get; set; }

    [BindNever, ValidateNever]
    public IReadOnlyList<DateOnly> Dates { get; set; } = Array.Empty<DateOnly>();

    [BindNever, ValidateNever]
    public IReadOnlyList<SlotOption> Slots { get; set; } = Array.Empty<SlotOption>();

    [BindNever, ValidateNever]
    public bool IsSignedIn { get; set; }

    // False when no card provider is configured: the card option is then not shown.
    [BindNever, ValidateNever]
    public bool CardAvailable { get; set; }
}

public class LookupViewModel
{
    [Display(Name = "Label_OrderNumber")]
    [Required(ErrorMessage = "Val_Required")]
    [Range(1, int.MaxValue, ErrorMessage = "Val_Required")]
    public int? Number { get; set; }

    [Display(Name = "Label_Email")]
    [Required(ErrorMessage = "Val_Required")]
    [EmailAddress(ErrorMessage = "Val_Email")]
    public string Email { get; set; } = string.Empty;
}

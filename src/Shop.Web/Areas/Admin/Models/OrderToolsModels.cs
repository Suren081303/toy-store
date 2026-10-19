using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Shop.Core.Admin;
using Shop.Core.Carts;
using Shop.Core.Orders;
using Shop.Web.Validation;

namespace Shop.Web.Areas.Admin.Models;

// The delivery days and times an admin may pick (the same rules as the checkout), and the form values for contact and address.
// Shared by the "Edit order" page and the last step of a phone order.
public class OrderContactAndDeliveryForm
{
    [Display(Name = "Label_FullName")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(100, ErrorMessage = "Val_MaxLength")]
    public string CustomerName { get; set; } = string.Empty;

    // Optional for an admin's order: no email means no emails to the customer.
    [Display(Name = "Label_Email")]
    [EmailAddress(ErrorMessage = "Val_Email")]
    [StringLength(256, ErrorMessage = "Val_MaxLength")]
    public string? Email { get; set; }

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

    // ---- Shown on the page, never posted ----

    [BindNever, ValidateNever]
    public IReadOnlyList<DateOnly> Dates { get; set; } = Array.Empty<DateOnly>();

    [BindNever, ValidateNever]
    public IReadOnlyList<SlotOption> Slots { get; set; } = Array.Empty<SlotOption>();
}

public class AdminOrderEditForm : OrderContactAndDeliveryForm
{
    public int Number { get; set; }

    [BindNever, ValidateNever]
    public OrderEditScope Scope { get; set; }

    [BindNever, ValidateNever]
    public string CurrentSlot { get; set; } = string.Empty;
}

// Step 3 of a phone order.
public class PhoneOrderForm : OrderContactAndDeliveryForm
{
    public Guid CheckoutId { get; set; }

    // The customer's account id, or "guest" for a customer without an account.
    public string Customer { get; set; } = "guest";

    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.CashOnDelivery;

    // The language of the customer's emails: hy, ru or en.
    public string Language { get; set; } = "hy";

    [Display(Name = "PO_Terms")]
    [MustBeTrue(ErrorMessage = "Val_Terms")]
    public bool AcceptTerms { get; set; }

    // The total shown on the page, posted back so a price change in between is noticed.
    public decimal ExpectedTotal { get; set; }

    [BindNever, ValidateNever]
    public CartView Cart { get; set; } = CartView.Empty(false);

    [BindNever, ValidateNever]
    public decimal DeliveryFee { get; set; }

    [BindNever, ValidateNever]
    public decimal Total { get; set; }

    [BindNever, ValidateNever]
    public PhoneOrderCustomer? Account { get; set; }
}

public record PhoneOrderStartViewModel(string? Query, IReadOnlyList<AdminCustomerRow> Matches, int StaffCartItems);

public record PhoneOrderProduct(int Id, string Sku, string Name, string Brand, decimal Price, int DiscountPercent, int Stock, int MaxQuantity);

// Step 2 of a phone order. Customer: the account id or "guest". Account: null for a customer without an account.
public record PhoneOrderItemsViewModel(
    string Customer,
    PhoneOrderCustomer? Account,
    string? ProductQuery,
    IReadOnlyList<PhoneOrderProduct> Products,
    CartView Cart);

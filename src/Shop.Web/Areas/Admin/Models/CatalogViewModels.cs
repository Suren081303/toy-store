using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Shop.Core.Admin;

namespace Shop.Web.Areas.Admin.Models;

// ErrorMessage and Display Name values are keys in Resources/SharedResource.*.resx; they are translated when shown.

public class AdminCategoryViewModel
{
    public int? Id { get; set; }

    [Display(Name = "Admin_NameHy")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(100, ErrorMessage = "Val_MaxLength")]
    public string NameHy { get; set; } = string.Empty;

    [Display(Name = "Admin_NameRu")]
    [StringLength(100, ErrorMessage = "Val_MaxLength")]
    public string? NameRu { get; set; }

    [Display(Name = "Admin_NameEn")]
    [StringLength(100, ErrorMessage = "Val_MaxLength")]
    public string? NameEn { get; set; }

    [Display(Name = "Admin_SlotOrder")]
    [Range(0, 1000, ErrorMessage = "Val_Range")]
    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    // The saved picture (kept when no new file is chosen).
    public string? ImagePath { get; set; }

    // A newly chosen picture.
    [ValidateNever]
    public IFormFile? Image { get; set; }
}

public class AdminAgeGroupViewModel
{
    public int? Id { get; set; }

    [Display(Name = "Admin_NameHy")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(100, ErrorMessage = "Val_MaxLength")]
    public string NameHy { get; set; } = string.Empty;

    [Display(Name = "Admin_NameRu")]
    [StringLength(100, ErrorMessage = "Val_MaxLength")]
    public string? NameRu { get; set; }

    [Display(Name = "Admin_NameEn")]
    [StringLength(100, ErrorMessage = "Val_MaxLength")]
    public string? NameEn { get; set; }

    [Display(Name = "Admin_SlotOrder")]
    [Range(0, 1000, ErrorMessage = "Val_Range")]
    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;
}

public record AdminProductsViewModel(
    AdminProductFilter Filter,
    PagedResult<AdminProductRow> Result,
    IReadOnlyList<AdminOption> Categories);

public class AdminProductViewModel
{
    public int? Id { get; set; }

    [Display(Name = "Admin_Prod_Sku")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(50, ErrorMessage = "Val_MaxLength")]
    [RegularExpression(@"^[A-Za-z0-9._\-]+$", ErrorMessage = "Admin_Val_Sku")]
    public string Sku { get; set; } = string.Empty;

    [Display(Name = "Admin_Prod_Brand")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(100, ErrorMessage = "Val_MaxLength")]
    public string Brand { get; set; } = string.Empty;

    [Display(Name = "Admin_Prod_Category")]
    [Range(1, int.MaxValue, ErrorMessage = "Val_Required")]
    public int CategoryId { get; set; }

    [Display(Name = "Admin_Prod_Price")]
    [Range(1, 10_000_000, ErrorMessage = "Val_Range")]
    [DisplayFormat(DataFormatString = "{0:0.##}", ApplyFormatInEditMode = true)]
    public decimal Price { get; set; }

    [Display(Name = "Admin_Prod_Discount")]
    [Range(0, 100, ErrorMessage = "Val_Range")]
    public int DiscountPercent { get; set; }

    [Display(Name = "Admin_Prod_Stock")]
    [Range(0, 100_000, ErrorMessage = "Val_Range")]
    public int StockQuantity { get; set; }

    [Display(Name = "Admin_NameHy")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(200, ErrorMessage = "Val_MaxLength")]
    public string NameHy { get; set; } = string.Empty;

    [Display(Name = "Admin_NameRu")]
    [StringLength(200, ErrorMessage = "Val_MaxLength")]
    public string? NameRu { get; set; }

    [Display(Name = "Admin_NameEn")]
    [StringLength(200, ErrorMessage = "Val_MaxLength")]
    public string? NameEn { get; set; }

    [Display(Name = "Admin_DescHy")]
    [StringLength(4000, ErrorMessage = "Val_MaxLength")]
    public string? DescriptionHy { get; set; }

    [Display(Name = "Admin_DescRu")]
    [StringLength(4000, ErrorMessage = "Val_MaxLength")]
    public string? DescriptionRu { get; set; }

    [Display(Name = "Admin_DescEn")]
    [StringLength(4000, ErrorMessage = "Val_MaxLength")]
    public string? DescriptionEn { get; set; }

    public List<int> AgeGroupIds { get; set; } = new();

    public bool IsPinnedAsNew { get; set; }

    public bool IsArchived { get; set; }

    public string? RowVersion { get; set; }

    // ---- Shown on the page, never posted ----

    [BindNever, ValidateNever]
    public IReadOnlyList<AdminOption> Categories { get; set; } = Array.Empty<AdminOption>();

    [BindNever, ValidateNever]
    public IReadOnlyList<AdminOption> AgeGroups { get; set; } = Array.Empty<AdminOption>();

    [BindNever, ValidateNever]
    public IReadOnlyList<AdminProductImage> Images { get; set; } = Array.Empty<AdminProductImage>();

    public AdminProductForm ToForm() => new()
    {
        Id = Id,
        Sku = Sku,
        Brand = Brand,
        CategoryId = CategoryId,
        Price = Price,
        DiscountPercent = DiscountPercent,
        StockQuantity = StockQuantity,
        NameHy = NameHy,
        NameRu = NameRu ?? string.Empty,
        NameEn = NameEn ?? string.Empty,
        DescriptionHy = DescriptionHy ?? string.Empty,
        DescriptionRu = DescriptionRu ?? string.Empty,
        DescriptionEn = DescriptionEn ?? string.Empty,
        AgeGroupIds = AgeGroupIds,
        IsPinnedAsNew = IsPinnedAsNew,
        IsArchived = IsArchived,
        RowVersion = RowVersion
    };

    public static AdminProductViewModel From(AdminProductForm form) => new()
    {
        Id = form.Id,
        Sku = form.Sku,
        Brand = form.Brand,
        CategoryId = form.CategoryId,
        Price = form.Price,
        DiscountPercent = form.DiscountPercent,
        StockQuantity = form.StockQuantity,
        NameHy = form.NameHy,
        NameRu = form.NameRu,
        NameEn = form.NameEn,
        DescriptionHy = form.DescriptionHy,
        DescriptionRu = form.DescriptionRu,
        DescriptionEn = form.DescriptionEn,
        AgeGroupIds = form.AgeGroupIds,
        IsPinnedAsNew = form.IsPinnedAsNew,
        IsArchived = form.IsArchived,
        RowVersion = form.RowVersion
    };
}

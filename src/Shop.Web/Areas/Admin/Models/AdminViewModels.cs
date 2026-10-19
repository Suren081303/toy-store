using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Shop.Core.Admin;
using Shop.Core.Notifications;
using Shop.Core.Orders;

namespace Shop.Web.Areas.Admin.Models;

public record AdminOrdersViewModel(AdminOrderFilter Filter, PagedResult<AdminOrderRow> Result);

public record AdminReturnsViewModel(ReturnStatus? Status, PagedResult<AdminReturnRow> Result);

// CurrentAdminId: the signed-in admin (their own row has no "Remove" button).
public record AdminAccountsViewModel(IReadOnlyList<AdminAccountRow> Admins, string CurrentAdminId);

public record AdminNotificationsViewModel(bool UnreadOnly, PagedResult<NotificationRow> Result);

public record AdminEmailsViewModel(OutboxStatus? Status, PagedResult<EmailRow> Result);

public record AdminAuditViewModel(AuditFilter Filter, PagedResult<AuditRow> Result, IReadOnlyList<string> EntityTypes);

// ErrorMessage and Display Name values are keys in Resources/SharedResource.*.resx; they are translated when shown.
public class AdminSettingsViewModel
{
    // Shown on the page, never posted.
    [BindNever, ValidateNever]
    public IReadOnlyList<AdminSlot> Slots { get; set; } = Array.Empty<AdminSlot>();

    [Display(Name = "Admin_DeliveryFee")]
    [Range(0, 1_000_000, ErrorMessage = "Val_Range")]
    [DisplayFormat(DataFormatString = "{0:0.##}", ApplyFormatInEditMode = true)]
    public decimal DeliveryFee { get; set; }

    [Display(Name = "Admin_FreeFrom")]
    [Range(0, 100_000_000, ErrorMessage = "Val_Range")]
    [DisplayFormat(DataFormatString = "{0:0.##}", ApplyFormatInEditMode = true)]
    public decimal FreeDeliveryFrom { get; set; }

    [Display(Name = "Admin_DaysAhead")]
    [Range(1, 60, ErrorMessage = "Val_Range")]
    public int DeliveryDaysAhead { get; set; }

    [Display(Name = "Admin_FirstDay")]
    [Range(0, 14, ErrorMessage = "Val_Range")]
    public int DeliveryFirstDayOffset { get; set; }

    [Display(Name = "Admin_ReturnWindow")]
    [Range(0, 90, ErrorMessage = "Val_Range")]
    public int ReturnWindowDays { get; set; }

    [Display(Name = "Admin_BankName")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(200, ErrorMessage = "Val_MaxLength")]
    public string BankName { get; set; } = string.Empty;

    [Display(Name = "Admin_BankAccount")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(50, ErrorMessage = "Val_MaxLength")]
    public string BankAccount { get; set; } = string.Empty;

    [Display(Name = "Admin_BankRecipient")]
    [Required(ErrorMessage = "Val_Required")]
    [StringLength(200, ErrorMessage = "Val_MaxLength")]
    public string BankRecipient { get; set; } = string.Empty;
}

// Preset: what the period picker shows as chosen. CustomWasInvalid: the typed range was refused and the last 30 days are shown.
public record AdminAnalyticsViewModel(AnalyticsReport Report, PeriodPreset Preset, bool CustomWasInvalid);

// One KPI card. Before = null: no comparison is shown. GoodWhenUp: false for things like cancellations (a rise is not good news).
public record AnalyticsKpi(string Label, string Value, decimal Now, decimal? Before, bool GoodWhenUp = true, string? Note = null);

// One breakdown table: label + figures of each row.
public record AnalyticsTable(string Title, IReadOnlyList<(string Label, BreakdownRow Row)> Rows, bool ShowUnits = false);

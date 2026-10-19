using Shop.Core.Admin;

namespace Shop.Web.Areas.Admin.Models;

// The form of "change many products". Scope is "selected" (the ticked products, Ids) or "filter" (every product that matches the
// filter fields, not only the page shown). On the preview page the exact ids are sent back, so what is applied is what was shown.
public class BulkForm
{
    public string Scope { get; set; } = "selected";

    public List<int> Ids { get; set; } = [];

    // The product list's filter (for Scope = "filter") and the address to come back to.
    public string? Query { get; set; }

    public int? CategoryId { get; set; }

    public ProductVisibility Show { get; set; } = ProductVisibility.Active;

    public bool LowStock { get; set; }

    public string? Back { get; set; }

    // The change.
    public BulkAction Action { get; set; }

    public decimal? Value { get; set; }

    public int? TargetCategoryId { get; set; }

    public BulkChange ToChange() => new() { Action = Action, Value = Value, CategoryId = TargetCategoryId };

    public AdminProductFilter ToFilter() => new() { Query = Query, CategoryId = CategoryId, Show = Show, LowStock = LowStock };
}

public record BulkPreviewViewModel(BulkForm Form, IReadOnlyList<int> Ids, BulkPreview Preview);

// The import page lists the numbers of the categories and age groups, because the file uses numbers.
public record ImportPageViewModel(IReadOnlyList<AdminOption> Categories, IReadOnlyList<AdminOption> AgeGroups);

public record ImportPreviewViewModel(ImportPreview Preview, string Csv, string FileName);

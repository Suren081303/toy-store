using System.Text;

namespace Shop.Core.Admin;

// ---- Duplicate a product ----

public enum DuplicateProductStatus
{
    Created,

    NotFound,

    // Empty or longer than 50 characters.
    InvalidSku,

    DuplicateSku
}

public record DuplicateProductResult(DuplicateProductStatus Status, int? NewId = null);

// ---- Change many products at once ----

public enum BulkAction
{
    SetDiscount,

    // Value is a percent, for example 10 (dearer) or -5 (cheaper). The result is rounded to a whole dram.
    ChangePricePercent,

    SetPrice,

    SetStock,

    Archive,

    Restore,

    MoveCategory
}

public static class BulkRules
{
    // At most this many products in one change (and one import).
    public const int MaxProducts = 1000;

    public const decimal MaxPrice = 100_000_000m;

    public const int MaxStock = 100_000;

    public const decimal MinPercentChange = -90m;

    public const decimal MaxPercentChange = 1000m;
}

public class BulkChange
{
    public BulkAction Action { get; set; }

    // The number for SetDiscount, ChangePricePercent, SetPrice and SetStock.
    public decimal? Value { get; set; }

    // The category for MoveCategory.
    public int? CategoryId { get; set; }
}

// One product in the preview. Before and After are short plain texts of the one value that changes (for example "10000" and "11000").
// Problem is the translation key of what is wrong with this product (null = fine).
public record BulkRow(int Id, string Sku, string Name, string Before, string After, bool Changes, string? Problem);

// ErrorKey: the change itself is not valid (for example a discount of 150), so there are no rows.
public record BulkPreview(IReadOnlyList<BulkRow> Rows, string? ErrorKey)
{
    public bool IsValid => ErrorKey is null && Rows.Count > 0 && Rows.All(r => r.Problem is null);

    public int ChangedCount => Rows.Count(r => r.Changes);
}

public enum BulkApplyStatus
{
    Applied,

    // The change or some product is not valid (see the preview); nothing was saved.
    Invalid,

    // A product was changed meanwhile (an order, another admin); nothing was saved.
    Conflict,

    NothingSelected,

    TooMany
}

public record BulkApplyResult(BulkApplyStatus Status, int Changed = 0, int Unchanged = 0);

// ---- CSV import and export ----

// The columns of the products file. Sku is the key: a row with a known SKU updates that product, an unknown SKU makes a new one.
// A column may be left out of the file (then it is not touched); an empty cell keeps what the product has.
public static class ProductCsv
{
    public static readonly IReadOnlyList<string> Columns =
    [
        "Sku", "Brand", "CategoryId", "Price", "DiscountPercent", "Stock", "Archived", "NewArrival",
        "NameHy", "NameRu", "NameEn", "DescriptionHy", "DescriptionRu", "DescriptionEn", "AgeGroupIds"
    ];

    public const int MaxRows = BulkRules.MaxProducts;

    // The uploaded file may not be bigger than this.
    public const int MaxFileBytes = 2 * 1024 * 1024;
}

public enum ImportAction
{
    Create,

    Update,

    Unchanged,

    Error
}

// What is wrong, as a translation key and up to three values for its {0} {1} {2}.
public record ImportProblem(string Key, string? A = null, string? B = null, string? C = null);

// Row is the number of the data row (the first row after the header is 1).
public record ImportRow(int Row, string Sku, ImportAction Action, IReadOnlyList<string> Changes, IReadOnlyList<ImportProblem> Problems);

public record ImportPreview(IReadOnlyList<ImportRow> Rows, IReadOnlyList<ImportProblem> FileProblems)
{
    public int Creates => Rows.Count(r => r.Action == ImportAction.Create);

    public int Updates => Rows.Count(r => r.Action == ImportAction.Update);

    public int Unchanged => Rows.Count(r => r.Action == ImportAction.Unchanged);

    public int ErrorRows => Rows.Count(r => r.Action == ImportAction.Error);

    // Nothing is applied unless the whole file is fine.
    public bool IsValid => FileProblems.Count == 0 && ErrorRows == 0 && Rows.Count > 0;
}

public enum ImportApplyStatus
{
    Applied,

    // The file has problems (see the preview); nothing was saved.
    Invalid,

    // A product was changed while the import ran; nothing was saved.
    Conflict
}

public record ImportApplyResult(ImportApplyStatus Status, int Created = 0, int Updated = 0, int Unchanged = 0);

public interface IAdminProductToolsService
{
    // A copy of a product: the same names, descriptions, brand, category, price, discount and age groups; stock 0; hidden
    // (archived) until the admin has checked it. Photos are copied by the caller (files), see IImageStore.CopyAsync.
    Task<DuplicateProductResult> DuplicateAsync(int sourceId, string newSku, string adminId);

    // The ids of the products that match a filter, newest first, at most max + 1 of them (so the caller can tell "too many").
    Task<IReadOnlyList<int>> FilterIdsAsync(AdminProductFilter filter, int max);

    Task<BulkPreview> PreviewBulkAsync(IReadOnlyList<int> ids, BulkChange change, string culture);

    // All or nothing. One audit line per changed product.
    Task<BulkApplyResult> ApplyBulkAsync(IReadOnlyList<int> ids, BulkChange change, string adminId);

    // Every product (archived too) as a CSV file in the format of the import. Audited.
    Task<byte[]> ExportCsvAsync(string adminId);

    Task<ImportPreview> PreviewImportAsync(string csvText);

    // All or nothing: the same checks as the preview, then everything is saved in one transaction.
    Task<ImportApplyResult> ApplyImportAsync(string csvText, string adminId);
}

// ---- Reading a CSV file ----

public static class CsvReader
{
    // The rows of a CSV text, each a list of cells. Understands quotes ("a,b", "say ""hi"""), line breaks inside quotes, CR LF / LF / CR
    // line ends, a byte-order mark, and a semicolon or tab instead of a comma (the first line decides). Empty lines are skipped.
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var delimiter = DetectDelimiter(text);
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var cellStarted = false;

        void EndCell()
        {
            row.Add(cell.ToString());
            cell.Clear();
            cellStarted = false;
        }

        void EndRow()
        {
            EndCell();

            if (row.Any(c => !string.IsNullOrWhiteSpace(c)))
            {
                rows.Add(row);
            }

            row = [];
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            if (c == '"' && !cellStarted)
            {
                inQuotes = true;
                cellStarted = true;
            }
            else if (c == delimiter)
            {
                EndCell();
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                EndRow();
            }
            else
            {
                cell.Append(c);
                cellStarted = true;
            }
        }

        if (cellStarted || cell.Length > 0 || row.Count > 0)
        {
            EndRow();
        }

        return rows;
    }

    // The writer puts an apostrophe before a text that a spreadsheet could read as a formula (= + - @). Reading undoes that.
    public static string Unprotect(string cell)
    {
        return cell.Length >= 2 && cell[0] == '\'' && cell[1] is '=' or '+' or '-' or '@' ? cell[1..] : cell;
    }

    // Comma, semicolon or tab: whichever is most frequent in the first line (outside quotes). Comma when in doubt.
    private static char DetectDelimiter(string text)
    {
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0 };
        var inQuotes = false;

        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && (c == '\n' || c == '\r'))
            {
                break;
            }
            else if (!inQuotes && counts.ContainsKey(c))
            {
                counts[c]++;
            }
        }

        return counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key == ',' ? 0 : 1).First().Key;
    }
}

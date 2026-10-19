using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Entities;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Admin;

// Catalog tools for the seller: duplicate a product, change many products at once, export and import the catalog as CSV.
// Changes are all or nothing (one transaction) and every product that changes gets its own audit line.
public class AdminProductToolsService : IAdminProductToolsService
{
    private const string LockName = "products-bulk";
    private static readonly string[] Cultures = ["hy", "ru", "en"];

    private readonly ShopDbContext _db;
    private readonly TimeProvider _time;

    public AdminProductToolsService(ShopDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    // ---- Duplicate ----

    public async Task<DuplicateProductResult> DuplicateAsync(int sourceId, string newSku, string adminId)
    {
        var sku = (newSku ?? string.Empty).Trim();
        if (sku.Length is 0 or > 50)
        {
            return new DuplicateProductResult(DuplicateProductStatus.InvalidSku);
        }

        var source = await _db.Products.AsNoTracking()
            .Include(p => p.Translations)
            .Include(p => p.AgeGroups)
            .FirstOrDefaultAsync(p => p.Id == sourceId);

        if (source is null)
        {
            return new DuplicateProductResult(DuplicateProductStatus.NotFound);
        }

        if (await _db.Products.AnyAsync(p => p.Sku == sku))
        {
            return new DuplicateProductResult(DuplicateProductStatus.DuplicateSku);
        }

        var now = Now();
        var copy = new Product
        {
            Sku = sku,
            Brand = source.Brand,
            CategoryId = source.CategoryId,
            Price = source.Price,
            DiscountPercent = source.DiscountPercent,
            // New goods are not on the shelf yet: stock 0 and hidden until the admin has checked the copy.
            StockQuantity = 0,
            IsArchived = true,
            IsPinnedAsNew = false,
            CreatedAt = now
        };

        foreach (var t in source.Translations)
        {
            copy.Translations.Add(new ProductTranslation { Culture = t.Culture, Name = t.Name, Description = t.Description });
        }

        var groupIds = source.AgeGroups.Select(a => a.Id).ToList();
        foreach (var group in await _db.AgeGroups.Where(a => groupIds.Contains(a.Id)).ToListAsync())
        {
            copy.AgeGroups.Add(group);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        _db.Products.Add(copy);
        await _db.SaveChangesAsync();

        // The id exists only now, so the audit line is a second small save in the same transaction.
        await AuditWriter.AddAsync(_db, adminId, now, "Product.Duplicated", "Product", copy.Id.ToString(), $"{sku} from {source.Sku}");
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return new DuplicateProductResult(DuplicateProductStatus.Created, copy.Id);
    }

    // ---- Which products ----

    public async Task<IReadOnlyList<int>> FilterIdsAsync(AdminProductFilter filter, int max)
    {
        var query = _db.Products.AsNoTracking().AsQueryable();

        query = filter.Show switch
        {
            ProductVisibility.Active => query.Where(p => !p.IsArchived),
            ProductVisibility.Archived => query.Where(p => p.IsArchived),
            _ => query
        };

        if (filter.CategoryId is not null)
        {
            query = query.Where(p => p.CategoryId == filter.CategoryId);
        }

        if (filter.LowStock)
        {
            query = query.Where(p => p.StockQuantity <= StockLevels.Low);
        }

        var text = filter.Query?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            query = query.Where(p => p.Sku.Contains(text) || p.Brand.Contains(text) || p.Translations.Any(t => t.Name.Contains(text)));
        }

        return await query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).Select(p => p.Id).Take(max + 1).ToListAsync();
    }

    // ---- Change many ----

    // What is wrong with the change itself (before looking at any product), as a translation key; null when it is fine.
    private async Task<string?> CheckChangeAsync(BulkChange change)
    {
        var value = change.Value;

        switch (change.Action)
        {
            case BulkAction.SetDiscount:
                return value is { } d && d == Math.Truncate(d) && d is >= 0 and <= 100 ? null : "Admin_Bulk_BadValue";

            case BulkAction.ChangePricePercent:
                return value is { } p && p is >= BulkRules.MinPercentChange and <= BulkRules.MaxPercentChange ? null : "Admin_Bulk_BadValue";

            case BulkAction.SetPrice:
                return value is { } price && price > 0 && price <= BulkRules.MaxPrice && price == Math.Round(price, 2) ? null : "Admin_Bulk_BadValue";

            case BulkAction.SetStock:
                return value is { } s && s == Math.Truncate(s) && s is >= 0 and <= BulkRules.MaxStock ? null : "Admin_Bulk_BadValue";

            case BulkAction.MoveCategory:
                return change.CategoryId is { } id && await _db.Categories.AnyAsync(c => c.Id == id) ? null : "Admin_Bulk_NoCategory";

            case BulkAction.Archive:
            case BulkAction.Restore:
                return null;

            default:
                return "Admin_Bulk_BadValue";
        }
    }

    // The new value of a product for the change, and what is wrong with it (if anything). Before/After are short plain texts.
    private static (string Before, string After, bool Changes, string? Problem) Compute(Product p, BulkChange change, string categoryBefore, string categoryAfter)
    {
        static string Plain(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        switch (change.Action)
        {
            case BulkAction.SetDiscount:
            {
                var now = (int)change.Value!.Value;
                return (p.DiscountPercent.ToString(CultureInfo.InvariantCulture), now.ToString(CultureInfo.InvariantCulture), p.DiscountPercent != now, null);
            }

            case BulkAction.ChangePricePercent:
            {
                var now = Math.Round(p.Price * (1 + change.Value!.Value / 100m), 0, MidpointRounding.AwayFromZero);
                var problem = now < 1 ? "Admin_Bulk_Problem_PriceLow" : now > BulkRules.MaxPrice ? "Admin_Bulk_Problem_PriceHigh" : null;
                return (Plain(p.Price), Plain(now), p.Price != now, problem);
            }

            case BulkAction.SetPrice:
            {
                var now = change.Value!.Value;
                return (Plain(p.Price), Plain(now), p.Price != now, null);
            }

            case BulkAction.SetStock:
            {
                var now = (int)change.Value!.Value;
                return (p.StockQuantity.ToString(CultureInfo.InvariantCulture), now.ToString(CultureInfo.InvariantCulture), p.StockQuantity != now, null);
            }

            case BulkAction.Archive:
                return (p.IsArchived ? "archived" : "shown", "archived", !p.IsArchived, null);

            case BulkAction.Restore:
                return (p.IsArchived ? "archived" : "shown", "shown", p.IsArchived, null);

            default: // MoveCategory
                return (categoryBefore, categoryAfter, p.CategoryId != change.CategoryId, null);
        }
    }

    private static void ApplyTo(Product p, BulkChange change)
    {
        switch (change.Action)
        {
            case BulkAction.SetDiscount:
                p.DiscountPercent = (int)change.Value!.Value;
                break;
            case BulkAction.ChangePricePercent:
                p.Price = Math.Round(p.Price * (1 + change.Value!.Value / 100m), 0, MidpointRounding.AwayFromZero);
                break;
            case BulkAction.SetPrice:
                p.Price = change.Value!.Value;
                break;
            case BulkAction.SetStock:
                p.StockQuantity = (int)change.Value!.Value;
                break;
            case BulkAction.Archive:
                p.IsArchived = true;
                break;
            case BulkAction.Restore:
                p.IsArchived = false;
                break;
            default:
                p.CategoryId = change.CategoryId!.Value;
                break;
        }
    }

    private async Task<Dictionary<int, string>> CategoryNamesAsync(string culture)
    {
        var categories = await _db.Categories.AsNoTracking().Select(c => new { c.Id, Names = c.Translations.Select(t => new { t.Culture, t.Name }).ToList() }).ToListAsync();

        return categories.ToDictionary(c => c.Id, c => PickName(c.Names.Select(n => (n.Culture, n.Name)), culture));
    }

    private static string PickName(IEnumerable<(string Culture, string Name)> names, string culture)
    {
        var list = names.ToList();

        return list.FirstOrDefault(n => n.Culture == culture).Name
            ?? list.FirstOrDefault(n => n.Culture == "hy").Name
            ?? list.FirstOrDefault().Name
            ?? string.Empty;
    }

    public async Task<BulkPreview> PreviewBulkAsync(IReadOnlyList<int> ids, BulkChange change, string culture)
    {
        var error = await CheckChangeAsync(change);
        if (error is not null)
        {
            return new BulkPreview([], error);
        }

        var wanted = ids.Distinct().ToList();
        var products = await _db.Products.AsNoTracking()
            .Where(p => wanted.Contains(p.Id))
            .Select(p => new
            {
                Product = p,
                Names = p.Translations.Select(t => new { t.Culture, t.Name }).ToList()
            })
            .ToListAsync();

        var categories = change.Action == BulkAction.MoveCategory ? await CategoryNamesAsync(culture) : [];
        var after = change.CategoryId is { } c && categories.TryGetValue(c, out var name) ? name : string.Empty;
        var byId = products.ToDictionary(p => p.Product.Id);

        var rows = new List<BulkRow>();
        foreach (var id in wanted.Where(byId.ContainsKey))
        {
            var p = byId[id];
            var before = categories.TryGetValue(p.Product.CategoryId, out var beforeName) ? beforeName : string.Empty;
            var (was, now, changes, problem) = Compute(p.Product, change, before, after);
            rows.Add(new BulkRow(id, p.Product.Sku, PickName(p.Names.Select(n => (n.Culture, n.Name)), culture), was, now, changes, problem));
        }

        return new BulkPreview(rows, null);
    }

    public async Task<BulkApplyResult> ApplyBulkAsync(IReadOnlyList<int> ids, BulkChange change, string adminId)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0)
        {
            return new BulkApplyResult(BulkApplyStatus.NothingSelected);
        }

        if (wanted.Count > BulkRules.MaxProducts)
        {
            return new BulkApplyResult(BulkApplyStatus.TooMany);
        }

        if (await CheckChangeAsync(change) is not null)
        {
            return new BulkApplyResult(BulkApplyStatus.Invalid);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();

        // One bulk change or import at a time.
        await _db.AcquireAsync(LockName);

        var products = await _db.Products.Where(p => wanted.Contains(p.Id)).ToListAsync();
        if (products.Count == 0)
        {
            return new BulkApplyResult(BulkApplyStatus.NothingSelected);
        }

        var categories = change.Action == BulkAction.MoveCategory ? await CategoryNamesAsync("hy") : [];
        var after = change.CategoryId is { } c && categories.TryGetValue(c, out var name) ? name : string.Empty;

        var plan = products
            .Select(p => (Product: p, Result: Compute(p, change, categories.GetValueOrDefault(p.CategoryId, string.Empty), after)))
            .ToList();

        if (plan.Any(x => x.Result.Problem is not null))
        {
            return new BulkApplyResult(BulkApplyStatus.Invalid);
        }

        var now = Now();
        var email = await _db.Users.Where(u => u.Id == adminId).Select(u => u.Email).FirstOrDefaultAsync() ?? string.Empty;
        var changed = 0;

        foreach (var (product, result) in plan.Where(x => x.Result.Changes))
        {
            ApplyTo(product, change);
            AuditWriter.Add(_db, adminId, email, now, "Product.Bulk", "Product", product.Id.ToString(), $"{change.Action}: {result.Before} -> {result.After}");
            changed++;
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Not an error to hide: a product changed while this ran (an order took stock, or another admin saved). The admin is told.
            return new BulkApplyResult(BulkApplyStatus.Conflict);
        }

        await transaction.CommitAsync();

        return new BulkApplyResult(BulkApplyStatus.Applied, changed, plan.Count - changed);
    }

    // ---- Export ----

    public async Task<byte[]> ExportCsvAsync(string adminId)
    {
        var products = await _db.Products.AsNoTracking()
            .Include(p => p.Translations)
            .Include(p => p.AgeGroups)
            .OrderBy(p => p.Sku)
            .ToListAsync();

        string Text(Product p, string culture, bool name) =>
            p.Translations.FirstOrDefault(t => t.Culture == culture) is { } t ? (name ? t.Name : t.Description) : string.Empty;

        var file = CsvWriter.Build(
            ProductCsv.Columns,
            products.Select(p => (IReadOnlyList<object?>)new object?[]
            {
                p.Sku, p.Brand, p.CategoryId, p.Price, p.DiscountPercent, p.StockQuantity, p.IsArchived ? "yes" : "no", p.IsPinnedAsNew ? "yes" : "no",
                Text(p, "hy", true), Text(p, "ru", true), Text(p, "en", true),
                Text(p, "hy", false), Text(p, "ru", false), Text(p, "en", false),
                string.Join(';', p.AgeGroups.Select(a => a.Id).OrderBy(i => i))
            }));

        await AuditWriter.AddAsync(_db, adminId, Now(), "Export.Products", "Product", "export", $"{products.Count} products");
        await _db.SaveChangesAsync();

        return file;
    }

    // ---- Import ----

    public async Task<ImportPreview> PreviewImportAsync(string csvText)
    {
        var analysis = await AnalyzeAsync(csvText, track: false);

        return new ImportPreview(analysis.Rows.Select(r => r.Summary).ToList(), analysis.FileProblems);
    }

    public async Task<ImportApplyResult> ApplyImportAsync(string csvText, string adminId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _db.AcquireAsync(LockName);

        var analysis = await AnalyzeAsync(csvText, track: true);
        var preview = new ImportPreview(analysis.Rows.Select(r => r.Summary).ToList(), analysis.FileProblems);
        if (!preview.IsValid)
        {
            return new ImportApplyResult(ImportApplyStatus.Invalid);
        }

        var now = Now();
        var email = await _db.Users.Where(u => u.Id == adminId).Select(u => u.Email).FirstOrDefaultAsync() ?? string.Empty;

        foreach (var row in analysis.Rows)
        {
            row.Apply?.Invoke(now);
        }

        try
        {
            await _db.SaveChangesAsync(); // the new products get their ids here
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ImportApplyResult(ImportApplyStatus.Conflict);
        }

        foreach (var row in analysis.Rows.Where(r => r.Product is not null && r.Summary.Action is ImportAction.Create or ImportAction.Update))
        {
            var details = row.Summary.Action == ImportAction.Create ? "created" : string.Join("; ", row.Summary.Changes);
            AuditWriter.Add(_db, adminId, email, now, "Product.Imported", "Product", row.Product!.Id.ToString(), $"{row.Summary.Sku}: {details}");
        }

        AuditWriter.Add(_db, adminId, email, now, "Import.Products", "Product", "import", $"{preview.Creates} created, {preview.Updates} updated, {preview.Unchanged} unchanged");
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return new ImportApplyResult(ImportApplyStatus.Applied, preview.Creates, preview.Updates, preview.Unchanged);
    }

    // One analysed data row: its summary for the preview, the product it is about, and (for a valid row) what to do to apply it.
    private sealed class RowPlan
    {
        public required ImportRow Summary { get; init; }

        public Product? Product { get; init; }

        public Action<DateTime>? Apply { get; init; }
    }

    private sealed record Analysis(IReadOnlyList<RowPlan> Rows, IReadOnlyList<ImportProblem> FileProblems);

    private async Task<Analysis> AnalyzeAsync(string csvText, bool track)
    {
        var rows = CsvReader.Parse(csvText ?? string.Empty);
        var fileProblems = new List<ImportProblem>();

        if (rows.Count == 0)
        {
            fileProblems.Add(new ImportProblem("Admin_Imp_P_Empty"));
            return new Analysis([], fileProblems);
        }

        // ---- The header ----
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows[0].Count; i++)
        {
            var name = rows[0][i].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var known = ProductCsv.Columns.FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
            if (known is null)
            {
                fileProblems.Add(new ImportProblem("Admin_Imp_P_UnknownColumn", name));
            }
            else if (!columns.TryAdd(known, i))
            {
                fileProblems.Add(new ImportProblem("Admin_Imp_P_DuplicateColumn", known));
            }
        }

        if (!columns.ContainsKey("Sku"))
        {
            fileProblems.Add(new ImportProblem("Admin_Imp_P_NoSku"));
        }

        var data = rows.Skip(1).ToList();
        if (data.Count == 0 && fileProblems.Count == 0)
        {
            fileProblems.Add(new ImportProblem("Admin_Imp_P_Empty"));
        }

        if (data.Count > ProductCsv.MaxRows)
        {
            fileProblems.Add(new ImportProblem("Admin_Imp_P_TooMany", ProductCsv.MaxRows.ToString(CultureInfo.InvariantCulture)));
        }

        if (fileProblems.Count > 0)
        {
            return new Analysis([], fileProblems);
        }

        // ---- What exists ----
        string Cell(IReadOnlyList<string> row, string column) =>
            columns.TryGetValue(column, out var index) && index < row.Count ? row[index].Trim() : string.Empty;

        var skus = data.Select(r => Cell(r, "Sku")).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existingQuery = track ? _db.Products.AsQueryable() : _db.Products.AsNoTracking();
        var existing = (await existingQuery
                .Include(p => p.Translations)
                .Include(p => p.AgeGroups)
                .Where(p => skus.Contains(p.Sku))
                .ToListAsync())
            .ToDictionary(p => p.Sku, StringComparer.OrdinalIgnoreCase);

        var categoryIds = (await _db.Categories.AsNoTracking().Select(c => c.Id).ToListAsync()).ToHashSet();
        var ageGroups = await _db.AgeGroups.ToDictionaryAsync(a => a.Id);

        var firstRowOfSku = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<RowPlan>();
        var number = 0;

        foreach (var row in data)
        {
            number++;
            plans.Add(AnalyzeRow(number, row, Cell, columns, existing, categoryIds, ageGroups, firstRowOfSku, track));
        }

        return new Analysis(plans, fileProblems);
    }

    private RowPlan AnalyzeRow(
        int number,
        IReadOnlyList<string> row,
        Func<IReadOnlyList<string>, string, string> cell,
        Dictionary<string, int> columns,
        Dictionary<string, Product> existing,
        HashSet<int> categoryIds,
        Dictionary<int, AgeGroup> ageGroups,
        Dictionary<string, int> firstRowOfSku,
        bool track)
    {
        var problems = new List<ImportProblem>();
        var changes = new List<string>();
        var sku = cell(row, "Sku");

        if (columns.Count > 0 && row.Skip(columns.Values.Max() + 1).Any(c => !string.IsNullOrWhiteSpace(c)))
        {
            problems.Add(new ImportProblem("Admin_Imp_R_ExtraCells"));
        }

        if (sku.Length == 0)
        {
            problems.Add(new ImportProblem("Admin_Imp_R_NoSku"));
        }
        else if (sku.Length > 50)
        {
            problems.Add(new ImportProblem("Admin_Imp_R_TooLong", "Sku", "50"));
        }
        else if (!firstRowOfSku.TryAdd(sku, number))
        {
            problems.Add(new ImportProblem("Admin_Imp_R_Duplicate", sku, firstRowOfSku[sku].ToString(CultureInfo.InvariantCulture)));
        }

        existing.TryGetValue(sku, out var product);
        var isNew = product is null;
        string Value(string column) => cell(row, column);
        bool Present(string column) => columns.ContainsKey(column) && Value(column).Length > 0;

        // ---- Each cell: parse and check; nothing is changed yet ----
        string? brand = null;
        int? categoryId = null;
        decimal? price = null;
        int? discount = null;
        int? stock = null;
        bool? archived = null;
        bool? pinned = null;
        List<int>? ageIds = null;

        if (Present("Brand"))
        {
            brand = Value("Brand");
            if (brand.Length > 100)
            {
                problems.Add(new ImportProblem("Admin_Imp_R_TooLong", "Brand", "100"));
            }
        }

        if (Present("CategoryId"))
        {
            if (!int.TryParse(Value("CategoryId"), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                problems.Add(new ImportProblem("Admin_Imp_R_Invalid", "CategoryId", Value("CategoryId")));
            }
            else if (!categoryIds.Contains(id))
            {
                problems.Add(new ImportProblem("Admin_Imp_R_NotFound", "CategoryId", Value("CategoryId")));
            }
            else
            {
                categoryId = id;
            }
        }

        if (Present("Price"))
        {
            if (!decimal.TryParse(Value("Price"), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) || parsed != Math.Round(parsed, 2))
            {
                problems.Add(new ImportProblem("Admin_Imp_R_Invalid", "Price", Value("Price")));
            }
            else if (parsed <= 0 || parsed > BulkRules.MaxPrice)
            {
                problems.Add(new ImportProblem("Admin_Imp_R_Range", "Price", "1", BulkRules.MaxPrice.ToString("0", CultureInfo.InvariantCulture)));
            }
            else
            {
                price = parsed;
            }
        }

        if (Present("DiscountPercent"))
        {
            if (!int.TryParse(Value("DiscountPercent"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
            {
                problems.Add(new ImportProblem("Admin_Imp_R_Invalid", "DiscountPercent", Value("DiscountPercent")));
            }
            else if (parsed is < 0 or > 100)
            {
                problems.Add(new ImportProblem("Admin_Imp_R_Range", "DiscountPercent", "0", "100"));
            }
            else
            {
                discount = parsed;
            }
        }

        if (Present("Stock"))
        {
            if (!int.TryParse(Value("Stock"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
            {
                problems.Add(new ImportProblem("Admin_Imp_R_Invalid", "Stock", Value("Stock")));
            }
            else if (parsed is < 0 or > BulkRules.MaxStock)
            {
                problems.Add(new ImportProblem("Admin_Imp_R_Range", "Stock", "0", BulkRules.MaxStock.ToString(CultureInfo.InvariantCulture)));
            }
            else
            {
                stock = parsed;
            }
        }

        foreach (var column in new[] { "Archived", "NewArrival" })
        {
            if (!Present(column))
            {
                continue;
            }

            var token = Value(column).ToLowerInvariant();
            bool? flag = token is "yes" or "y" or "true" or "1" ? true : token is "no" or "n" or "false" or "0" ? false : null;
            if (flag is null)
            {
                problems.Add(new ImportProblem("Admin_Imp_R_Bool", column, Value(column)));
            }
            else if (column == "Archived")
            {
                archived = flag;
            }
            else
            {
                pinned = flag;
            }
        }

        if (Present("AgeGroupIds"))
        {
            ageIds = [];
            foreach (var part in Value("AgeGroupIds").Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    problems.Add(new ImportProblem("Admin_Imp_R_Invalid", "AgeGroupIds", part));
                }
                else if (!ageGroups.ContainsKey(id))
                {
                    problems.Add(new ImportProblem("Admin_Imp_R_NotFound", "AgeGroupIds", part));
                }
                else if (!ageIds.Contains(id))
                {
                    ageIds.Add(id);
                }
            }
        }

        var names = new Dictionary<string, string?>();
        var descriptions = new Dictionary<string, string?>();
        foreach (var culture in Cultures)
        {
            var suffix = char.ToUpperInvariant(culture[0]) + culture[1..];
            var nameColumn = "Name" + suffix;
            var descriptionColumn = "Description" + suffix;

            names[culture] = Present(nameColumn) ? Value(nameColumn) : null;
            descriptions[culture] = Present(descriptionColumn) ? CsvReader.Unprotect(row[columns[descriptionColumn]]).Trim() : null;

            if (names[culture]?.Length > 200)
            {
                problems.Add(new ImportProblem("Admin_Imp_R_TooLong", nameColumn, "200"));
            }

            if (descriptions[culture]?.Length > 4000)
            {
                problems.Add(new ImportProblem("Admin_Imp_R_TooLong", descriptionColumn, "4000"));
            }

            if (names[culture] is { } n)
            {
                names[culture] = CsvReader.Unprotect(n);
            }

            // A description needs a name in that language (a translation is a name plus a description).
            var hasTranslation = product?.Translations.Any(t => t.Culture == culture) == true;
            if (descriptions[culture] is not null && names[culture] is null && !hasTranslation)
            {
                problems.Add(new ImportProblem("Admin_Imp_R_DescNeedsName", descriptionColumn));
            }
        }

        // ---- A new product needs its basics ----
        if (isNew)
        {
            foreach (var (column, given) in new[] { ("Brand", brand is not null), ("CategoryId", categoryId is not null), ("Price", price is not null), ("NameHy", names["hy"] is not null) })
            {
                // A value that was given but is wrong is already reported above; only a missing one is reported here.
                if (!given && !Present(column))
                {
                    problems.Add(new ImportProblem("Admin_Imp_R_Required", column));
                }
            }
        }

        var summarySku = sku.Length == 0 ? "-" : sku;
        if (problems.Count > 0)
        {
            return new RowPlan { Summary = new ImportRow(number, summarySku, ImportAction.Error, changes, problems) };
        }

        // ---- Create ----
        if (isNew)
        {
            changes.Add("created");
            var created = new Product
            {
                Sku = sku,
                Brand = brand!,
                CategoryId = categoryId!.Value,
                Price = price!.Value,
                DiscountPercent = discount ?? 0,
                StockQuantity = stock ?? 0,
                // A product made by an import stays hidden until the admin has looked at it, unless the file says otherwise.
                IsArchived = archived ?? true,
                IsPinnedAsNew = pinned ?? false
            };

            return new RowPlan
            {
                Summary = new ImportRow(number, sku, ImportAction.Create, changes, problems),
                Product = created,
                Apply = now =>
                {
                    created.CreatedAt = now;
                    foreach (var culture in Cultures)
                    {
                        if (names[culture] is { } name)
                        {
                            created.Translations.Add(new ProductTranslation { Culture = culture, Name = name, Description = descriptions[culture] ?? string.Empty });
                        }
                    }

                    foreach (var id in ageIds ?? [])
                    {
                        created.AgeGroups.Add(ageGroups[id]);
                    }

                    _db.Products.Add(created);
                }
            };
        }

        // ---- Update: only what differs ----
        void Diff<T>(string label, T was, T now)
        {
            if (!EqualityComparer<T>.Default.Equals(was, now))
            {
                changes.Add($"{label} {Show(was)} -> {Show(now)}");
            }
        }

        static string Show<T>(T value) => value is decimal d ? d.ToString("0.##", CultureInfo.InvariantCulture) : value?.ToString() ?? string.Empty;

        var current = product!;
        if (brand is not null) Diff("Brand", current.Brand, brand);
        if (categoryId is not null) Diff("Category", current.CategoryId, categoryId.Value);
        if (price is not null) Diff("Price", current.Price, price.Value);
        if (discount is not null) Diff("Discount", current.DiscountPercent, discount.Value);
        if (stock is not null) Diff("Stock", current.StockQuantity, stock.Value);
        if (archived is not null) Diff("Archived", current.IsArchived, archived.Value);
        if (pinned is not null) Diff("NewArrival", current.IsPinnedAsNew, pinned.Value);

        if (ageIds is not null)
        {
            Diff("AgeGroups", string.Join(';', current.AgeGroups.Select(a => a.Id).OrderBy(i => i)), string.Join(';', ageIds.OrderBy(i => i)));
        }

        var translationEdits = new List<(string Culture, string? Name, string? Description)>();
        foreach (var culture in Cultures)
        {
            var t = current.Translations.FirstOrDefault(x => x.Culture == culture);
            var suffix = char.ToUpperInvariant(culture[0]) + culture[1..];

            if (names[culture] is { } name && (t is null || t.Name != name))
            {
                changes.Add("Name" + suffix);
                translationEdits.Add((culture, name, descriptions[culture]));
            }
            else if (descriptions[culture] is { } description && (t?.Description ?? string.Empty) != description)
            {
                changes.Add("Description" + suffix);
                translationEdits.Add((culture, null, description));
            }
        }

        if (changes.Count == 0)
        {
            return new RowPlan { Summary = new ImportRow(number, sku, ImportAction.Unchanged, changes, problems), Product = current };
        }

        return new RowPlan
        {
            Summary = new ImportRow(number, sku, ImportAction.Update, changes, problems),
            Product = current,
            Apply = track
                ? _ =>
                {
                    if (brand is not null) current.Brand = brand;
                    if (categoryId is not null) current.CategoryId = categoryId.Value;
                    if (price is not null) current.Price = price.Value;
                    if (discount is not null) current.DiscountPercent = discount.Value;
                    if (stock is not null) current.StockQuantity = stock.Value;
                    if (archived is not null) current.IsArchived = archived.Value;
                    if (pinned is not null) current.IsPinnedAsNew = pinned.Value;

                    if (ageIds is not null)
                    {
                        current.AgeGroups.Clear();
                        foreach (var id in ageIds)
                        {
                            current.AgeGroups.Add(ageGroups[id]);
                        }
                    }

                    foreach (var (culture, name, description) in translationEdits)
                    {
                        var t = current.Translations.FirstOrDefault(x => x.Culture == culture);
                        if (t is null)
                        {
                            current.Translations.Add(new ProductTranslation { Culture = culture, Name = name!, Description = description ?? string.Empty });
                        }
                        else
                        {
                            if (name is not null) t.Name = name;
                            if (description is not null) t.Description = description;
                        }
                    }
                }
                : null
        };
    }
}

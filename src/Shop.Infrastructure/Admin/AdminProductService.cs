using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Entities;
using Shop.Core.Localization;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Admin;

public class AdminProductService : IAdminProductService
{
    private const int PageSize = 20;

    private readonly ShopDbContext _db;
    private readonly TimeProvider _time;

    public AdminProductService(ShopDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    // ---- List ----

    public async Task<PagedResult<AdminProductRow>> ListAsync(AdminProductFilter filter, string culture)
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

        var total = await query.CountAsync();
        var page = Math.Max(1, filter.Page);

        var rows = await query
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(p => new
            {
                p.Id,
                p.Sku,
                p.Brand,
                p.Price,
                p.DiscountPercent,
                p.StockQuantity,
                p.IsArchived,
                Names = p.Translations.Select(t => new { t.Culture, t.Name }).ToList(),
                CategoryNames = p.Category.Translations.Select(t => new { t.Culture, t.Name }).ToList(),
                Image = p.Images.OrderBy(i => i.SortOrder).Select(i => i.ThumbPath ?? i.Path).FirstOrDefault()
            })
            .ToListAsync();

        var items = rows
            .Select(r => new AdminProductRow(
                r.Id, r.Sku, Pick(r.Names.Select(n => (n.Culture, n.Name)), culture), r.Brand,
                Pick(r.CategoryNames.Select(n => (n.Culture, n.Name)), culture),
                r.Price, r.DiscountPercent, r.StockQuantity, r.IsArchived, r.Image))
            .ToList();

        return new PagedResult<AdminProductRow>(items, page, PageSize, total);
    }

    // ---- One product ----

    public async Task<AdminProduct?> GetAsync(int id)
    {
        var product = await _db.Products.AsNoTracking()
            .Include(p => p.Translations)
            .Include(p => p.AgeGroups)
            .Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (product is null)
        {
            return null;
        }

        var form = new AdminProductForm
        {
            Id = product.Id,
            Sku = product.Sku,
            Brand = product.Brand,
            CategoryId = product.CategoryId,
            Price = product.Price,
            DiscountPercent = product.DiscountPercent,
            StockQuantity = product.StockQuantity,
            NameHy = Translation(product, "hy")?.Name ?? string.Empty,
            NameRu = Translation(product, "ru")?.Name ?? string.Empty,
            NameEn = Translation(product, "en")?.Name ?? string.Empty,
            DescriptionHy = Translation(product, "hy")?.Description ?? string.Empty,
            DescriptionRu = Translation(product, "ru")?.Description ?? string.Empty,
            DescriptionEn = Translation(product, "en")?.Description ?? string.Empty,
            AgeGroupIds = product.AgeGroups.Select(a => a.Id).OrderBy(i => i).ToList(),
            IsPinnedAsNew = product.IsPinnedAsNew,
            IsArchived = product.IsArchived,
            RowVersion = Convert.ToBase64String(product.RowVersion)
        };

        var images = product.Images
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Id)
            .Select(i => new AdminProductImage(i.Id, i.Path, i.ThumbPath, i.SortOrder))
            .ToList();

        return new AdminProduct(form, images);
    }

    public async Task<SaveProductResult> SaveAsync(AdminProductForm form, string adminId)
    {
        if (string.IsNullOrWhiteSpace(form.NameHy))
        {
            throw new ArgumentException("The Armenian name is required.", nameof(form));
        }

        var sku = form.Sku.Trim();
        var now = _time.GetUtcNow().UtcDateTime;

        if (!await _db.Categories.AnyAsync(c => c.Id == form.CategoryId))
        {
            return new SaveProductResult(SaveProductStatus.UnknownCategory);
        }

        var excludeId = form.Id ?? 0;
        if (await _db.Products.AnyAsync(p => p.Sku == sku && p.Id != excludeId))
        {
            return new SaveProductResult(SaveProductStatus.DuplicateSku);
        }

        Product product;
        string details;
        var isNew = form.Id is null;

        if (isNew)
        {
            product = new Product { CreatedAt = now };
            _db.Products.Add(product);
            details = $"{sku}, {form.NameHy.Trim()}";
        }
        else
        {
            var existing = await _db.Products
                .Include(p => p.Translations)
                .Include(p => p.AgeGroups)
                .FirstOrDefaultAsync(p => p.Id == form.Id);
            if (existing is null)
            {
                return new SaveProductResult(SaveProductStatus.NotFound);
            }

            // The version the form was opened with. If an order (or another admin) changed the product since, the save is refused,
            // so the admin does not overwrite the new stock with an old number.
            byte[] openedWith;
            try
            {
                openedWith = Convert.FromBase64String(form.RowVersion ?? string.Empty);
            }
            catch (FormatException)
            {
                return new SaveProductResult(SaveProductStatus.Conflict);
            }

            if (openedWith.Length == 0)
            {
                return new SaveProductResult(SaveProductStatus.Conflict);
            }

            product = existing;
            _db.Entry(product).Property(p => p.RowVersion).OriginalValue = openedWith;
            details = Describe(product, form, sku);

            // Make sure the product row itself is written (and so the version is checked) even when only a translation changed.
            _db.Entry(product).Property(p => p.Brand).IsModified = true;
        }

        product.Sku = sku;
        product.Brand = form.Brand.Trim();
        product.CategoryId = form.CategoryId;
        product.Price = form.Price;
        product.DiscountPercent = form.DiscountPercent;
        product.StockQuantity = form.StockQuantity;
        product.IsPinnedAsNew = form.IsPinnedAsNew;
        product.IsArchived = form.IsArchived;

        SetTranslation(product, "hy", form.NameHy, form.DescriptionHy);
        SetTranslation(product, "ru", form.NameRu, form.DescriptionRu);
        SetTranslation(product, "en", form.NameEn, form.DescriptionEn);

        var wanted = form.AgeGroupIds.Distinct().ToList();
        var groups = await _db.AgeGroups.Where(a => wanted.Contains(a.Id)).ToListAsync();
        product.AgeGroups.Clear();
        foreach (var group in groups)
        {
            product.AgeGroups.Add(group);
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Not an error to hide: it is the "changed meanwhile" answer, and the admin is told so on the page.
            return new SaveProductResult(SaveProductStatus.Conflict);
        }

        // A new product has an id only after the first save, so its audit line is a second small save.
        await AuditWriter.AddAsync(_db, adminId, now, isNew ? "Product.Created" : "Product.Changed", "Product", product.Id.ToString(), details);
        await _db.SaveChangesAsync();

        return new SaveProductResult(SaveProductStatus.Saved, product.Id);
    }

    public async Task<bool> SetStockAsync(int productId, int stock, string adminId)
    {
        var before = await _db.Products.Where(p => p.Id == productId).Select(p => (int?)p.StockQuantity).FirstOrDefaultAsync();
        if (before is null)
        {
            return false;
        }

        // The number is set directly (the admin counted the shelf), in one statement, so an order at the same moment is not lost.
        await _db.Products.Where(p => p.Id == productId).ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, stock));

        await AuditWriter.AddAsync(_db, adminId, _time.GetUtcNow().UtcDateTime, "Product.StockSet", "Product", productId.ToString(), $"Stock {before} -> {stock}");
        await _db.SaveChangesAsync();

        return true;
    }

    // ---- Photos ----

    public Task<int> CountImagesAsync(int productId) => _db.ProductImages.CountAsync(i => i.ProductId == productId);

    public async Task<ImageChangeStatus> AddImagesAsync(int productId, IReadOnlyList<StoredImage> images, string adminId)
    {
        if (!await _db.Products.AnyAsync(p => p.Id == productId))
        {
            return ImageChangeStatus.NotFound;
        }

        var existing = await _db.ProductImages.Where(i => i.ProductId == productId).ToListAsync();
        if (existing.Count + images.Count > ImageRules.MaxPerProduct)
        {
            return ImageChangeStatus.TooMany;
        }

        var next = existing.Count == 0 ? 0 : existing.Max(i => i.SortOrder) + 1;
        foreach (var image in images)
        {
            _db.ProductImages.Add(new ProductImage { ProductId = productId, Path = image.Path, ThumbPath = image.ThumbPath, SortOrder = next++ });
        }

        await AuditWriter.AddAsync(_db, adminId, Now(), "Product.PhotosAdded", "Product", productId.ToString(), $"{images.Count} photo(s)");
        await _db.SaveChangesAsync();

        return ImageChangeStatus.Done;
    }

    public async Task<RemovedImage?> RemoveImageAsync(int productId, int imageId, string adminId)
    {
        var images = await _db.ProductImages.Where(i => i.ProductId == productId).OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToListAsync();
        var image = images.FirstOrDefault(i => i.Id == imageId);
        if (image is null)
        {
            return null;
        }

        _db.ProductImages.Remove(image);
        images.Remove(image);
        Renumber(images);

        await AuditWriter.AddAsync(_db, adminId, Now(), "Product.PhotoRemoved", "Product", productId.ToString(), image.Path);
        await _db.SaveChangesAsync();

        return new RemovedImage(image.Path, image.ThumbPath);
    }

    public async Task<ImageChangeStatus> MoveImageAsync(int productId, int imageId, int direction, string adminId)
    {
        var images = await _db.ProductImages.Where(i => i.ProductId == productId).OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToListAsync();
        var index = images.FindIndex(i => i.Id == imageId);
        if (index < 0)
        {
            return ImageChangeStatus.NotFound;
        }

        var target = index + Math.Sign(direction);
        if (target < 0 || target >= images.Count)
        {
            return ImageChangeStatus.Done; // already first or last: nothing to do
        }

        (images[index], images[target]) = (images[target], images[index]);
        Renumber(images);

        await AuditWriter.AddAsync(_db, adminId, Now(), "Product.PhotosOrdered", "Product", productId.ToString());
        await _db.SaveChangesAsync();

        return ImageChangeStatus.Done;
    }

    public async Task<ImageChangeStatus> MakeMainImageAsync(int productId, int imageId, string adminId)
    {
        var images = await _db.ProductImages.Where(i => i.ProductId == productId).OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToListAsync();
        var image = images.FirstOrDefault(i => i.Id == imageId);
        if (image is null)
        {
            return ImageChangeStatus.NotFound;
        }

        images.Remove(image);
        images.Insert(0, image);
        Renumber(images);

        await AuditWriter.AddAsync(_db, adminId, Now(), "Product.MainPhotoSet", "Product", productId.ToString());
        await _db.SaveChangesAsync();

        return ImageChangeStatus.Done;
    }

    // ---- helpers ----

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    // The photos are numbered 0, 1, 2... in the order shown; the first is the main one.
    private static void Renumber(List<ProductImage> images)
    {
        for (var i = 0; i < images.Count; i++)
        {
            images[i].SortOrder = i;
        }
    }

    private static ProductTranslation? Translation(Product product, string culture)
    {
        return product.Translations.FirstOrDefault(t => t.Culture == culture);
    }

    // The Armenian name is always there; Russian and English only when a name was typed (a description alone is not kept).
    private static void SetTranslation(Product product, string culture, string? name, string? description)
    {
        var text = name?.Trim();
        var existing = Translation(product, culture);

        if (string.IsNullOrEmpty(text))
        {
            if (existing is not null)
            {
                product.Translations.Remove(existing);
            }

            return;
        }

        var body = description?.Trim() ?? string.Empty;
        if (existing is null)
        {
            product.Translations.Add(new ProductTranslation { Culture = culture, Name = text, Description = body });
        }
        else
        {
            existing.Name = text;
            existing.Description = body;
        }
    }

    private static string Pick(IEnumerable<(string Culture, string Name)> names, string culture)
    {
        var list = names.ToList();
        var preferred = list.FirstOrDefault(n => n.Culture == culture).Name;
        return !string.IsNullOrEmpty(preferred) ? preferred : list.FirstOrDefault(n => n.Culture == Cultures.Default).Name ?? string.Empty;
    }

    // "Price 10000 -> 12000; Stock 5 -> 3", only what changed (names and descriptions are listed as "texts").
    private static string Describe(Product before, AdminProductForm form, string sku)
    {
        var changes = new List<string>();

        void Check<T>(string name, T was, T now)
        {
            if (!EqualityComparer<T>.Default.Equals(was, now))
            {
                changes.Add($"{name} {Plain(was)} -> {Plain(now)}");
            }
        }

        Check("Sku", before.Sku, sku);
        Check("Brand", before.Brand, form.Brand.Trim());
        Check("Category", before.CategoryId, form.CategoryId);
        Check("Price", before.Price, form.Price);
        Check("Discount", before.DiscountPercent, form.DiscountPercent);
        Check("Stock", before.StockQuantity, form.StockQuantity);
        Check("Archived", before.IsArchived, form.IsArchived);
        Check("PinnedAsNew", before.IsPinnedAsNew, form.IsPinnedAsNew);

        var oldAges = string.Join(',', before.AgeGroups.Select(a => a.Id).OrderBy(i => i));
        var newAges = string.Join(',', form.AgeGroupIds.Distinct().OrderBy(i => i));
        Check("AgeGroups", oldAges, newAges);

        var textsChanged = new[] { "hy", "ru", "en" }.Any(c =>
            (Translation(before, c)?.Name ?? string.Empty) != Normalized(c, form, name: true)
            || (Translation(before, c)?.Description ?? string.Empty) != Normalized(c, form, name: false));
        if (textsChanged)
        {
            changes.Add("texts changed");
        }

        return changes.Count == 0 ? "No changes" : string.Join("; ", changes);
    }

    // Amounts without the database's trailing zeros: 10000 instead of 10000.00.
    private static string Plain<T>(T value) => value is decimal d ? d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : value?.ToString() ?? string.Empty;

    private static string Normalized(string culture, AdminProductForm form, bool name)
    {
        var value = (culture, name) switch
        {
            ("hy", true) => form.NameHy,
            ("ru", true) => form.NameRu,
            ("en", true) => form.NameEn,
            ("hy", false) => form.DescriptionHy,
            ("ru", false) => form.DescriptionRu,
            _ => form.DescriptionEn
        };

        return value?.Trim() ?? string.Empty;
    }
}

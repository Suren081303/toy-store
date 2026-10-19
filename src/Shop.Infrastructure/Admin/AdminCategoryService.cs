using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Entities;
using Shop.Core.Localization;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Admin;

public class AdminCategoryService : IAdminCategoryService
{
    private readonly ShopDbContext _db;
    private readonly TimeProvider _time;

    public AdminCategoryService(ShopDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<IReadOnlyList<AdminCategoryRow>> ListAsync(string culture)
    {
        var categories = await _db.Categories.AsNoTracking()
            .Include(c => c.Translations)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .ToListAsync();

        var counts = await _db.Products.AsNoTracking()
            .GroupBy(p => p.CategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Count);

        return categories
            .Select(c => new AdminCategoryRow(c.Id, c.Translations.ResolveName(culture), counts.GetValueOrDefault(c.Id), c.SortOrder, c.IsActive, c.ImagePath))
            .ToList();
    }

    public async Task<AdminCategoryForm?> GetAsync(int id)
    {
        var category = await _db.Categories.AsNoTracking().Include(c => c.Translations).FirstOrDefaultAsync(c => c.Id == id);
        if (category is null)
        {
            return null;
        }

        return new AdminCategoryForm
        {
            Id = category.Id,
            NameHy = NameIn(category.Translations, "hy"),
            NameRu = NameIn(category.Translations, "ru"),
            NameEn = NameIn(category.Translations, "en"),
            SortOrder = category.SortOrder,
            IsActive = category.IsActive,
            ImagePath = category.ImagePath
        };
    }

    public async Task<SaveCategoryResult> SaveAsync(AdminCategoryForm form, string adminId)
    {
        if (string.IsNullOrWhiteSpace(form.NameHy))
        {
            throw new ArgumentException("The Armenian name is required.", nameof(form));
        }

        Category category;
        string? replacedImage = null;
        var isNew = form.Id is null;

        if (isNew)
        {
            category = new Category();
            _db.Categories.Add(category);
        }
        else
        {
            var existing = await _db.Categories.Include(c => c.Translations).FirstOrDefaultAsync(c => c.Id == form.Id);
            if (existing is null)
            {
                return new SaveCategoryResult(null);
            }

            category = existing;
            if (form.ImagePath != category.ImagePath)
            {
                replacedImage = category.ImagePath;
            }
        }

        category.SortOrder = form.SortOrder;
        category.IsActive = form.IsActive;
        category.ImagePath = string.IsNullOrWhiteSpace(form.ImagePath) ? null : form.ImagePath;

        SetName(category, "hy", form.NameHy);
        SetName(category, "ru", form.NameRu);
        SetName(category, "en", form.NameEn);

        // A new category has no id until the first save; the audit line is written right after, in a second small save.
        await _db.SaveChangesAsync();
        await AuditWriter.AddAsync(
            _db, adminId, _time.GetUtcNow().UtcDateTime, isNew ? "Category.Created" : "Category.Changed", "Category",
            category.Id.ToString(), $"{form.NameHy.Trim()}, order {category.SortOrder}, {(category.IsActive ? "shown" : "hidden")}");
        await _db.SaveChangesAsync();

        return new SaveCategoryResult(category.Id, replacedImage);
    }

    public async Task<DeleteCategoryResult> DeleteAsync(int id, string adminId)
    {
        var category = await _db.Categories.Include(c => c.Translations).FirstOrDefaultAsync(c => c.Id == id);
        if (category is null)
        {
            return new DeleteCategoryResult(DeleteStatus.NotFound);
        }

        // Products keep their category forever (old orders refer to them), so a category with any product, archived or not, stays.
        if (await _db.Products.AnyAsync(p => p.CategoryId == id))
        {
            return new DeleteCategoryResult(DeleteStatus.InUse);
        }

        var name = category.Translations.ResolveName("hy");
        var image = category.ImagePath;

        _db.Categories.Remove(category);
        await AuditWriter.AddAsync(_db, adminId, _time.GetUtcNow().UtcDateTime, "Category.Deleted", "Category", id.ToString(), name);
        await _db.SaveChangesAsync();

        return new DeleteCategoryResult(DeleteStatus.Deleted, image);
    }

    public async Task<IReadOnlyList<AdminOption>> OptionsAsync(string culture)
    {
        var categories = await _db.Categories.AsNoTracking()
            .Include(c => c.Translations)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .ToListAsync();

        return categories.Select(c => new AdminOption(c.Id, c.Translations.ResolveName(culture))).ToList();
    }

    private static string NameIn(IEnumerable<CategoryTranslation> translations, string culture)
    {
        return translations.FirstOrDefault(t => t.Culture == culture)?.Name ?? string.Empty;
    }

    // Armenian always has a name; Russian and English only when typed (a missing one falls back to Armenian in the shop).
    private static void SetName(Category category, string culture, string? name)
    {
        var text = name?.Trim();
        var existing = category.Translations.FirstOrDefault(t => t.Culture == culture);

        if (string.IsNullOrEmpty(text))
        {
            if (existing is not null)
            {
                category.Translations.Remove(existing);
            }

            return;
        }

        if (existing is null)
        {
            category.Translations.Add(new CategoryTranslation { Culture = culture, Name = text });
        }
        else
        {
            existing.Name = text;
        }
    }
}

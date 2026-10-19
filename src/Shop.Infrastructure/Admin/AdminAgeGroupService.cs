using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Entities;
using Shop.Core.Localization;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Admin;

public class AdminAgeGroupService : IAdminAgeGroupService
{
    private readonly ShopDbContext _db;
    private readonly TimeProvider _time;

    public AdminAgeGroupService(ShopDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<IReadOnlyList<AdminAgeGroupRow>> ListAsync(string culture)
    {
        var groups = await _db.AgeGroups.AsNoTracking()
            .Include(a => a.Translations)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id)
            .ToListAsync();

        // How many products use each age group (a product can be in several).
        var counts = await _db.Products.AsNoTracking()
            .SelectMany(p => p.AgeGroups.Select(a => a.Id))
            .GroupBy(id => id)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count);

        return groups
            .Select(a => new AdminAgeGroupRow(a.Id, a.Translations.ResolveName(culture), counts.GetValueOrDefault(a.Id), a.SortOrder, a.IsActive))
            .ToList();
    }

    public async Task<AdminAgeGroupForm?> GetAsync(int id)
    {
        var group = await _db.AgeGroups.AsNoTracking().Include(a => a.Translations).FirstOrDefaultAsync(a => a.Id == id);
        if (group is null)
        {
            return null;
        }

        return new AdminAgeGroupForm
        {
            Id = group.Id,
            NameHy = NameIn(group.Translations, "hy"),
            NameRu = NameIn(group.Translations, "ru"),
            NameEn = NameIn(group.Translations, "en"),
            SortOrder = group.SortOrder,
            IsActive = group.IsActive
        };
    }

    public async Task<int?> SaveAsync(AdminAgeGroupForm form, string adminId)
    {
        if (string.IsNullOrWhiteSpace(form.NameHy))
        {
            throw new ArgumentException("The Armenian name is required.", nameof(form));
        }

        AgeGroup group;
        var isNew = form.Id is null;

        if (isNew)
        {
            group = new AgeGroup();
            _db.AgeGroups.Add(group);
        }
        else
        {
            var existing = await _db.AgeGroups.Include(a => a.Translations).FirstOrDefaultAsync(a => a.Id == form.Id);
            if (existing is null)
            {
                return null;
            }

            group = existing;
        }

        group.SortOrder = form.SortOrder;
        group.IsActive = form.IsActive;

        SetName(group, "hy", form.NameHy);
        SetName(group, "ru", form.NameRu);
        SetName(group, "en", form.NameEn);

        await _db.SaveChangesAsync();
        await AuditWriter.AddAsync(
            _db, adminId, _time.GetUtcNow().UtcDateTime, isNew ? "AgeGroup.Created" : "AgeGroup.Changed", "AgeGroup",
            group.Id.ToString(), $"{form.NameHy.Trim()}, order {group.SortOrder}, {(group.IsActive ? "shown" : "hidden")}");
        await _db.SaveChangesAsync();

        return group.Id;
    }

    public async Task<DeleteStatus> DeleteAsync(int id, string adminId)
    {
        var group = await _db.AgeGroups.Include(a => a.Translations).FirstOrDefaultAsync(a => a.Id == id);
        if (group is null)
        {
            return DeleteStatus.NotFound;
        }

        if (await _db.Products.AnyAsync(p => p.AgeGroups.Any(a => a.Id == id)))
        {
            return DeleteStatus.InUse;
        }

        var name = group.Translations.ResolveName("hy");
        _db.AgeGroups.Remove(group);
        await AuditWriter.AddAsync(_db, adminId, _time.GetUtcNow().UtcDateTime, "AgeGroup.Deleted", "AgeGroup", id.ToString(), name);
        await _db.SaveChangesAsync();

        return DeleteStatus.Deleted;
    }

    public async Task<IReadOnlyList<AdminOption>> OptionsAsync(string culture)
    {
        var groups = await _db.AgeGroups.AsNoTracking()
            .Include(a => a.Translations)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id)
            .ToListAsync();

        return groups.Select(a => new AdminOption(a.Id, a.Translations.ResolveName(culture))).ToList();
    }

    private static string NameIn(IEnumerable<AgeGroupTranslation> translations, string culture)
    {
        return translations.FirstOrDefault(t => t.Culture == culture)?.Name ?? string.Empty;
    }

    private static void SetName(AgeGroup group, string culture, string? name)
    {
        var text = name?.Trim();
        var existing = group.Translations.FirstOrDefault(t => t.Culture == culture);

        if (string.IsNullOrEmpty(text))
        {
            if (existing is not null)
            {
                group.Translations.Remove(existing);
            }

            return;
        }

        if (existing is null)
        {
            group.Translations.Add(new AgeGroupTranslation { Culture = culture, Name = text });
        }
        else
        {
            existing.Name = text;
        }
    }
}

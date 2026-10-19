using Microsoft.EntityFrameworkCore;
using Shop.Core.Admin;
using Shop.Core.Entities;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Admin;

public class AdminSettingsService : IAdminSettingsService
{
    private readonly ShopDbContext _db;
    private readonly TimeProvider _time;

    public AdminSettingsService(ShopDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<AdminSettingsView> GetAsync()
    {
        var settings = await _db.StoreSettings.AsNoTracking().FirstAsync(s => s.Id == StoreSettings.SingleRowId);

        var slots = await _db.DeliverySlots.AsNoTracking()
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
            .Select(s => new AdminSlot(s.Id, s.Label, s.SortOrder, s.IsActive))
            .ToListAsync();

        return new AdminSettingsView(ToInput(settings), slots);
    }

    public async Task SaveSettingsAsync(AdminSettingsInput input, string adminUserId)
    {
        var settings = await _db.StoreSettings.FirstAsync(s => s.Id == StoreSettings.SingleRowId);
        var before = ToInput(settings);

        settings.DeliveryFee = input.DeliveryFee;
        settings.FreeDeliveryFrom = input.FreeDeliveryFrom;
        settings.DeliveryDaysAhead = input.DeliveryDaysAhead;
        settings.DeliveryFirstDayOffset = input.DeliveryFirstDayOffset;
        settings.ReturnWindowDays = input.ReturnWindowDays;
        settings.BankName = input.BankName.Trim();
        settings.BankAccount = input.BankAccount.Trim();
        settings.BankRecipient = input.BankRecipient.Trim();

        var after = ToInput(settings);
        await AuditWriter.AddAsync(_db, adminUserId, Now(), "Settings.Saved", "Settings", "1", Describe(before, after));

        await _db.SaveChangesAsync();
    }

    public async Task<SaveSlotResult> SaveSlotAsync(int? id, string label, int sortOrder, bool isActive, string adminUserId)
    {
        DeliverySlot slot;
        string action;

        if (id is null)
        {
            slot = new DeliverySlot();
            _db.DeliverySlots.Add(slot);
            action = "Slot.Added";
        }
        else
        {
            var existing = await _db.DeliverySlots.FirstOrDefaultAsync(s => s.Id == id);
            if (existing is null)
            {
                return SaveSlotResult.NotFound;
            }

            // Customers must always have a time to choose.
            if (existing.IsActive && !isActive && !await _db.DeliverySlots.AnyAsync(s => s.Id != id && s.IsActive))
            {
                return SaveSlotResult.LastActiveSlot;
            }

            slot = existing;
            action = "Slot.Changed";
        }

        slot.Label = label.Trim();
        slot.SortOrder = sortOrder;
        slot.IsActive = isActive;

        await _db.SaveChangesAsync();

        // The id of a new slot is known only after the first save, so the audit line is a second, small save.
        await AuditWriter.AddAsync(_db, adminUserId, Now(), action, "DeliverySlot", slot.Id.ToString(), $"{slot.Label}, order {slot.SortOrder}, {(slot.IsActive ? "active" : "hidden")}");
        await _db.SaveChangesAsync();

        return SaveSlotResult.Saved;
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private static AdminSettingsInput ToInput(StoreSettings s) => new()
    {
        DeliveryFee = s.DeliveryFee,
        FreeDeliveryFrom = s.FreeDeliveryFrom,
        DeliveryDaysAhead = s.DeliveryDaysAhead,
        DeliveryFirstDayOffset = s.DeliveryFirstDayOffset,
        ReturnWindowDays = s.ReturnWindowDays,
        BankName = s.BankName,
        BankAccount = s.BankAccount,
        BankRecipient = s.BankRecipient
    };

    private static string Plain<T>(T value) => value is decimal d ? d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : value?.ToString() ?? string.Empty;

    // "DeliveryFee 1500 -> 2000; ReturnWindowDays 14 -> 30", only what changed.
    private static string Describe(AdminSettingsInput before, AdminSettingsInput after)
    {
        var changes = new List<string>();

        void Check<T>(string name, T was, T now)
        {
            if (!EqualityComparer<T>.Default.Equals(was, now))
            {
                changes.Add($"{name} {Plain(was)} -> {Plain(now)}");
            }
        }

        Check(nameof(before.DeliveryFee), before.DeliveryFee, after.DeliveryFee);
        Check(nameof(before.FreeDeliveryFrom), before.FreeDeliveryFrom, after.FreeDeliveryFrom);
        Check(nameof(before.DeliveryDaysAhead), before.DeliveryDaysAhead, after.DeliveryDaysAhead);
        Check(nameof(before.DeliveryFirstDayOffset), before.DeliveryFirstDayOffset, after.DeliveryFirstDayOffset);
        Check(nameof(before.ReturnWindowDays), before.ReturnWindowDays, after.ReturnWindowDays);
        Check(nameof(before.BankName), before.BankName, after.BankName);
        Check(nameof(before.BankAccount), before.BankAccount, after.BankAccount);
        Check(nameof(before.BankRecipient), before.BankRecipient, after.BankRecipient);

        return changes.Count == 0 ? "No changes" : string.Join("; ", changes);
    }
}

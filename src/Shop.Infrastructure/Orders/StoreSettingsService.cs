using Microsoft.EntityFrameworkCore;
using Shop.Core.Entities;
using Shop.Core.Orders;
using Shop.Infrastructure.Data;

namespace Shop.Infrastructure.Orders;

public class StoreSettingsService : IStoreSettingsService
{
    private readonly ShopDbContext _db;

    public StoreSettingsService(ShopDbContext db)
    {
        _db = db;
    }

    public async Task<StoreSettingsView> GetAsync()
    {
        var settings = await _db.StoreSettings.AsNoTracking().FirstAsync(s => s.Id == StoreSettings.SingleRowId);

        var slots = await _db.DeliverySlots
            .AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
            .ToListAsync();

        return new StoreSettingsView(
            settings.DeliveryFee,
            settings.FreeDeliveryFrom,
            settings.DeliveryDaysAhead,
            settings.DeliveryFirstDayOffset,
            settings.ReturnWindowDays,
            slots.Select(s => new SlotOption(s.Id, s.Label)).ToList(),
            settings.BankName,
            settings.BankAccount,
            settings.BankRecipient);
    }
}

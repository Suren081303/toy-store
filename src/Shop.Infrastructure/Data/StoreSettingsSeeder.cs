using Microsoft.EntityFrameworkCore;
using Shop.Core.Entities;

namespace Shop.Infrastructure.Data;

public static class StoreSettingsSeeder
{
    // Creates the default delivery settings and time slots when they are missing (placeholders until the admin screen exists).
    public static async Task SeedAsync(ShopDbContext db)
    {
        var settings = await db.StoreSettings.OrderBy(s => s.Id).FirstOrDefaultAsync();
        if (settings is null)
        {
            settings = new StoreSettings
            {
                DeliveryFee = 1500,
                FreeDeliveryFrom = 20000,
                DeliveryDaysAhead = 14,
                DeliveryFirstDayOffset = 1,
                ReturnWindowDays = 14
            };
            db.StoreSettings.Add(settings);
        }

        // Placeholder bank details for bank transfers, only where they are empty (also on a row made before Phase 6).
        if (string.IsNullOrWhiteSpace(settings.BankName))
        {
            settings.BankName = "Sample Bank";
            settings.BankAccount = "AM00 0000 0000 0000 0000 0000 0000";
            settings.BankRecipient = "Shop LLC";
        }

        if (!await db.DeliverySlots.AnyAsync())
        {
            db.DeliverySlots.AddRange(
                new DeliverySlot { Label = "10:00–14:00", SortOrder = 1 },
                new DeliverySlot { Label = "14:00–18:00", SortOrder = 2 },
                new DeliverySlot { Label = "18:00–20:00", SortOrder = 3 });
        }

        await db.SaveChangesAsync();
    }
}

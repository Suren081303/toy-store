using System.Globalization;
using Shop.Core.Admin;
using Shop.Core.Orders;
using Shop.Web.Areas.Admin.Models;

namespace Shop.Web.Areas.Admin;

// What the "Edit order" page and the phone-order page have in common.
internal static class OrderFormRules
{
    // The days and times to choose from (the same rules as the checkout). keepDate: a day the order already has stays in the list
    // even when it would not be offered any more (for example today).
    public static async Task FillPageDataAsync(IStoreSettingsService settings, TimeProvider time, OrderContactAndDeliveryForm form, DateOnly? keepDate = null)
    {
        var current = await settings.GetAsync();
        var today = ShopClock.Today(time);
        var dates = DeliveryRules.GetDeliveryDates(today, current.DeliveryFirstDayOffset, current.DeliveryDaysAhead).ToList();

        if (keepDate is { } keep && !dates.Contains(keep))
        {
            dates.Add(keep);
            dates.Sort();
        }

        form.Dates = dates;
        form.Slots = current.Slots;
    }

    public static bool TryParseDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static AdminOrderEdit ToEdit(OrderContactAndDeliveryForm form, DateOnly day) => new()
    {
        CustomerName = form.CustomerName,
        Email = form.Email ?? string.Empty,
        Phone = form.Phone,
        Region = form.Region,
        City = form.City,
        Street = form.Street,
        Building = form.Building,
        Apartment = form.Apartment,
        Comment = form.Comment,
        DeliveryDate = day,
        DeliverySlotId = form.DeliverySlotId ?? 0
    };
}

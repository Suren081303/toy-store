namespace Shop.Core.Entities;

// The shop's delivery and returns settings: a single row (Id = 1). The admin screen that edits it comes later;
// until then the defaults created at startup apply.
public class StoreSettings
{
    public const int SingleRowId = 1;

    public int Id { get; set; } = SingleRowId;

    // Flat delivery fee in AMD, the same for every region.
    public decimal DeliveryFee { get; set; }

    // Orders at or above this items total get free delivery (0 = never free).
    public decimal FreeDeliveryFrom { get; set; }

    // How many days a customer can choose from.
    public int DeliveryDaysAhead { get; set; }

    // 1 = the earliest delivery day is tomorrow.
    public int DeliveryFirstDayOffset { get; set; }

    // Days after delivery during which a return can be requested.
    public int ReturnWindowDays { get; set; }

    // Where customers send bank transfers (placeholders until the admin screen exists).
    public string BankName { get; set; } = string.Empty;

    public string BankAccount { get; set; } = string.Empty;

    public string BankRecipient { get; set; } = string.Empty;
}

public class DeliverySlot
{
    public int Id { get; set; }

    // For example "10:00-14:00".
    public string Label { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;
}

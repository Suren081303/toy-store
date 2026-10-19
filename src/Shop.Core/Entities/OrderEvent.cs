using Shop.Core.Orders;

namespace Shop.Core.Entities;

// One line of an order's history: what it became and when.
public class OrderEvent
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;

    public OrderStatus Status { get; set; }

    public string? Note { get; set; }

    // The admin who made the change (null for the customer's own actions and the system).
    public string? ByUserId { get; set; }

    public Order Order { get; set; } = null!;
}

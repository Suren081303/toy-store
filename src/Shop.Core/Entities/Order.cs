using Shop.Core.Orders;

namespace Shop.Core.Entities;

public class Order
{
    public int Id { get; set; }

    // The number the customer sees (from a database sequence, starting at 10001).
    public int Number { get; set; }

    // Secret part of the link in the confirmation email: whoever has the link can open the order.
    public string AccessKey { get; set; } = string.Empty;

    // Created by the checkout page; it makes pressing "Place order" twice create only one order.
    public Guid CheckoutId { get; set; }

    public string? UserId { get; set; }

    // Contact and address as written at checkout (a snapshot: later account changes do not alter old orders).
    public string CustomerName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    // Region code, see Regions.
    public string Region { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public string Street { get; set; } = string.Empty;

    public string Building { get; set; } = string.Empty;

    public string? Apartment { get; set; }

    public string? Comment { get; set; }

    public DateOnly DeliveryDate { get; set; }

    // The time slot's text at order time, for example "10:00-14:00".
    public string DeliverySlot { get; set; } = string.Empty;

    public PaymentMethod PaymentMethod { get; set; }

    public PaymentStatus PaymentStatus { get; set; }

    public OrderStatus Status { get; set; }

    // Items at regular prices.
    public decimal Subtotal { get; set; }

    // Product discounts plus the registered-customer discount.
    public decimal Discount { get; set; }

    public decimal DeliveryFee { get; set; }

    // Subtotal - Discount + DeliveryFee
    public decimal Total { get; set; }

    public bool CustomerDiscountApplied { get; set; }

    // The registered-customer percent that was applied (10, or the customer's personal one); 0 when none.
    public int CustomerDiscountPercent { get; set; }

    // Language of the order (for emails): hy, ru or en.
    public string Culture { get; set; } = "hy";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? DeliveredAt { get; set; }

    public DateTime AcceptedTermsAt { get; set; }

    // Set once when a cancelled order's stock was put back, so it is never put back twice.
    public bool StockReleased { get; set; }

    // The admin who made this order for a customer by phone. Null: the customer ordered on the website.
    public string? CreatedByUserId { get; set; }

    public ApplicationUser? User { get; set; }

    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();

    public ICollection<OrderEvent> Events { get; set; } = new List<OrderEvent>();

    // Notes the shop keeps about the order. Only admins see them.
    public ICollection<OrderNote> Notes { get; set; } = new List<OrderNote>();

    public ICollection<ReturnRequest> ReturnRequests { get; set; } = new List<ReturnRequest>();
}

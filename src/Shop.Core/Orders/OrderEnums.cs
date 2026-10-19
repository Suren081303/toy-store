namespace Shop.Core.Orders;

public enum OrderStatus
{
    // Card or bank transfer chosen, not paid yet. The stock is already reserved.
    AwaitingPayment,

    // Paid, or cash on delivery.
    Ordered,

    Packed,

    Delivered,

    Cancelled
}

public enum PaymentMethod
{
    CashOnDelivery,

    Card,

    BankTransfer
}

public enum PaymentStatus
{
    Pending,

    Paid,

    Failed,

    Refunded
}

public enum ReturnStatus
{
    Requested,

    Rejected,

    Returned,

    Refunded
}

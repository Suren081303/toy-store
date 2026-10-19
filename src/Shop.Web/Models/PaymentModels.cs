using Shop.Core.Orders;
using Shop.Core.Payments;

namespace Shop.Web.Models;

// The page the customer sees after the bank: what happened, and whether they can try again.
// State: Failed, Cancelled, Waiting (no answer yet), Expired, Late (paid after the order was cancelled).
public record PaymentReturnViewModel(OrderSummary Order, string State, bool CanPay);

// The fake bank's page.
public record FakeBankViewModel(PaymentAttemptInfo Attempt, string? ReturnUrl);

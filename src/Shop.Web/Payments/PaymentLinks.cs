using Microsoft.AspNetCore.Mvc;

namespace Shop.Web.Payments;

// The addresses a payment needs: where the bank sends the customer back, and where the bank sends its answer.
public static class PaymentLinks
{
    // The customer's browser comes back here. The page only reads the result from our database.
    public static string PaymentReturn(this IUrlHelper url, HttpRequest request, int number, string key)
    {
        return url.Action("Return", "Payments", new { number, key }, request.Scheme)!;
    }

    // The bank's server calls this with the signed answer.
    public static string PaymentCallback(this IUrlHelper url, HttpRequest request)
    {
        return url.Action("Callback", "Payments", new { culture = "en" }, request.Scheme)!;
    }
}

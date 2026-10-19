using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Shop.Core.Payments;

namespace Shop.Web.Payments;

// A pretend bank for development. It works like a real one: the customer is sent to a payment page, and the answer
// comes as a signed message (reference, result, amount, signature) that the shop must check before believing it.
// It is registered only in Development (see Program.cs).
public class FakePaymentProvider : IPaymentProvider
{
    public const string ProviderName = "Fake";

    private readonly PaymentOptions _options;
    private readonly LinkGenerator _links;
    private readonly IHttpContextAccessor _context;

    public FakePaymentProvider(IOptions<PaymentOptions> options, LinkGenerator links, IHttpContextAccessor context)
    {
        _options = options.Value;
        _links = links;
        _context = context;
    }

    public string Name => ProviderName;

    public Task<ProviderSession> CreateSessionAsync(ProviderPaymentRequest request)
    {
        var url = _links.GetUriByAction(
            _context.HttpContext!, "Index", "FakeBank",
            new { culture = request.Culture, reference = request.Reference, returnUrl = request.ReturnUrl })!;

        return Task.FromResult(new ProviderSession(url));
    }

    public ProviderCallback? ParseCallback(IReadOnlyDictionary<string, string> fields)
    {
        if (string.IsNullOrEmpty(_options.FakeSecret)
            || !fields.TryGetValue("reference", out var reference)
            || !fields.TryGetValue("status", out var status)
            || !fields.TryGetValue("amount", out var amountText)
            || !fields.TryGetValue("signature", out var signature))
        {
            return null;
        }

        var expected = Encoding.UTF8.GetBytes(Sign(_options.FakeSecret, reference, status, amountText));
        var given = Encoding.UTF8.GetBytes(signature);
        if (expected.Length != given.Length || !CryptographicOperations.FixedTimeEquals(expected, given))
        {
            return null;
        }

        if (!Enum.TryParse<PaymentAttemptStatus>(status, out var result)
            || result == PaymentAttemptStatus.Created
            || !decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        return new ProviderCallback(reference, result, amount);
    }

    // The signature the "bank" puts on its answer: HMAC-SHA256 over the three values, as lower-case hex.
    public static string Sign(string secret, string reference, string status, string amount)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{reference}|{status}|{amount}"));
        return Convert.ToHexStringLower(hash);
    }
}

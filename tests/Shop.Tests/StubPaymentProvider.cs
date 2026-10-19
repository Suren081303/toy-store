using System.Globalization;
using Shop.Core.Payments;

namespace Shop.Tests;

// A bank for the service tests: the "signature" is the word "ok", so tests can send good and bad answers without any secret.
public class StubPaymentProvider : IPaymentProvider
{
    public string Name => "Stub";

    public Task<ProviderSession> CreateSessionAsync(ProviderPaymentRequest request)
    {
        return Task.FromResult(new ProviderSession("https://bank.test/pay/" + request.Reference));
    }

    public ProviderCallback? ParseCallback(IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("signature", out var signature) || signature != "ok"
            || !fields.TryGetValue("reference", out var reference)
            || !fields.TryGetValue("status", out var status)
            || !fields.TryGetValue("amount", out var amount)
            || !Enum.TryParse<PaymentAttemptStatus>(status, out var result))
        {
            return null;
        }

        return new ProviderCallback(reference, result, decimal.Parse(amount, CultureInfo.InvariantCulture));
    }

    // The fields of an answer from the stub bank.
    public static Dictionary<string, string> Answer(string reference, PaymentAttemptStatus status, decimal amount, string signature = "ok")
    {
        return new Dictionary<string, string>
        {
            ["reference"] = reference,
            ["status"] = status.ToString(),
            ["amount"] = amount.ToString(CultureInfo.InvariantCulture),
            ["signature"] = signature
        };
    }
}

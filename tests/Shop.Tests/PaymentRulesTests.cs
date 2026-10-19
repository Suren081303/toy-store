using Microsoft.Extensions.Options;
using Shop.Core.Orders;
using Shop.Core.Payments;
using Shop.Web.Payments;

namespace Shop.Tests;

public class PaymentRulesTests
{
    private static readonly DateTime Created = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly PaymentOptions Options = new() { CardTimeoutMinutes = 30, BankTransferTimeoutDays = 3 };

    [Fact]
    public void Deadline_ForCard_IsThirtyMinutes()
    {
        Assert.Equal(Created.AddMinutes(30), PaymentRules.GetDeadline(PaymentMethod.Card, Created, Options));
    }

    [Fact]
    public void Deadline_ForBankTransfer_IsThreeDays()
    {
        Assert.Equal(Created.AddDays(3), PaymentRules.GetDeadline(PaymentMethod.BankTransfer, Created, Options));
    }

    [Fact]
    public void Deadline_ForCash_DoesNotExist()
    {
        Assert.Null(PaymentRules.GetDeadline(PaymentMethod.CashOnDelivery, Created, Options));
    }

    [Theory]
    [InlineData(PaymentMethod.Card, 29, false)]
    [InlineData(PaymentMethod.Card, 30, false)] // exactly at the deadline is still allowed
    [InlineData(PaymentMethod.Card, 31, true)]
    [InlineData(PaymentMethod.BankTransfer, 60 * 24 * 2, false)]
    [InlineData(PaymentMethod.BankTransfer, 60 * 24 * 4, true)]
    [InlineData(PaymentMethod.CashOnDelivery, 60 * 24 * 365, false)]
    public void IsExpired_UsesTheMethodsOwnTime(PaymentMethod method, int minutesLater, bool expected)
    {
        Assert.Equal(expected, PaymentRules.IsExpired(method, Created, Created.AddMinutes(minutesLater), Options));
    }
}

public class FakePaymentProviderTests
{
    private const string Secret = "test-secret";

    private static FakePaymentProvider Create(string secret = Secret)
    {
        // The address builder and the request accessor are only needed to start a payment, not to read an answer.
        return new FakePaymentProvider(Options.Create(new PaymentOptions { FakeSecret = secret }), null!, null!);
    }

    private static Dictionary<string, string> Answer(string reference, string status, string amount, string? signature = null)
    {
        return new Dictionary<string, string>
        {
            ["reference"] = reference,
            ["status"] = status,
            ["amount"] = amount,
            ["signature"] = signature ?? FakePaymentProvider.Sign(Secret, reference, status, amount)
        };
    }

    [Fact]
    public void AValidAnswer_IsRead()
    {
        var answer = Create().ParseCallback(Answer("abc", "Paid", "17250.00"));

        Assert.NotNull(answer);
        Assert.Equal("abc", answer.Reference);
        Assert.Equal(PaymentAttemptStatus.Paid, answer.Result);
        Assert.Equal(17250m, answer.Amount);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public void FailedAndCancelledAnswers_AreRead(string status)
    {
        var answer = Create().ParseCallback(Answer("abc", status, "100.00"));

        Assert.Equal(Enum.Parse<PaymentAttemptStatus>(status), answer!.Result);
    }

    [Fact]
    public void ChangingAnyValue_BreaksTheSignature()
    {
        var provider = Create();
        var signature = FakePaymentProvider.Sign(Secret, "abc", "Failed", "17250.00");

        Assert.Null(provider.ParseCallback(Answer("abc", "Paid", "17250.00", signature)));   // status changed
        Assert.Null(provider.ParseCallback(Answer("abc", "Failed", "1.00", signature)));     // amount changed
        Assert.Null(provider.ParseCallback(Answer("abd", "Failed", "17250.00", signature))); // reference changed
    }

    [Fact]
    public void AnAnswerSignedWithAnotherSecret_IsRefused()
    {
        var forged = FakePaymentProvider.Sign("other-secret", "abc", "Paid", "100.00");

        Assert.Null(Create().ParseCallback(Answer("abc", "Paid", "100.00", forged)));
    }

    [Fact]
    public void WithoutASecret_NothingIsAccepted()
    {
        var signedWithEmpty = FakePaymentProvider.Sign(string.Empty, "abc", "Paid", "100.00");

        Assert.Null(Create(secret: string.Empty).ParseCallback(Answer("abc", "Paid", "100.00", signedWithEmpty)));
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("status")]
    [InlineData("amount")]
    [InlineData("signature")]
    public void AMissingField_IsRefused(string missing)
    {
        var fields = Answer("abc", "Paid", "100.00");
        fields.Remove(missing);

        Assert.Null(Create().ParseCallback(fields));
    }

    [Theory]
    [InlineData("Created")]
    [InlineData("Refunded")]
    [InlineData("")]
    public void AnUnknownOrNonFinalStatus_IsRefused_EvenWhenSigned(string status)
    {
        Assert.Null(Create().ParseCallback(Answer("abc", status, "100.00")));
    }

    [Fact]
    public void AnAmountThatIsNotANumber_IsRefused_EvenWhenSigned()
    {
        Assert.Null(Create().ParseCallback(Answer("abc", "Paid", "a lot")));
    }
}

using Shop.Core.Phones;

namespace Shop.Tests;

public class PhoneNumbersTests
{
    [Theory]
    [InlineData("091 123456", "+37491123456")]
    [InlineData("091123456", "+37491123456")]
    [InlineData("(091) 123-456", "+37491123456")]
    [InlineData("91123456", "+37491123456")]
    [InlineData("+374 91 123456", "+37491123456")]
    [InlineData("+37491123456", "+37491123456")]
    [InlineData("0037491123456", "+37491123456")]
    [InlineData("37491123456", "+37491123456")]
    [InlineData("  077 12 34 56  ", "+37477123456")]
    [InlineData("+7 912 345 6789", "+79123456789")]
    [InlineData("+1 (415) 555-0132", "+14155550132")]
    public void TryNormalize_AcceptsUsualWritings(string input, string expected)
    {
        Assert.True(PhoneNumbers.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("123")]
    [InlineData("0911234")]            // too short
    [InlineData("0911234567")]         // too long for a local number
    [InlineData("+374 91 12345")]      // Armenian number needs 8 digits after +374
    [InlineData("+374 91 1234567")]
    [InlineData("+0 123 456 789")]     // country code cannot start with 0
    [InlineData("+7 912 345 6789 0123 4")]  // more than 15 digits
    [InlineData("091 123 456 x")]
    [InlineData("091+123456")]
    [InlineData("7 912 345 6789")]     // foreign numbers need + or 00
    public void TryNormalize_RejectsInvalidNumbers(string? input)
    {
        Assert.False(PhoneNumbers.TryNormalize(input, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }
}

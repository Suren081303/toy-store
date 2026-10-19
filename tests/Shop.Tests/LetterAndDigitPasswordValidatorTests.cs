using Shop.Core.Entities;
using Shop.Web.Identity;

namespace Shop.Tests;

public class LetterAndDigitPasswordValidatorTests
{
    private static async Task<string[]> ErrorCodesAsync(string? password)
    {
        var validator = new LetterAndDigitPasswordValidator(new FakeLocalizer());

        // The validator does not use the user manager or the user.
        var result = await validator.ValidateAsync(null!, new ApplicationUser(), password);

        return result.Errors.Select(e => e.Code).ToArray();
    }

    [Theory]
    [InlineData("abc12345")]
    [InlineData("ABCD1234")]
    [InlineData("Գաղտնի12")]     // Armenian letters
    [InlineData("Пароль123")]    // Cyrillic letters
    [InlineData("a1")]           // length is checked by Identity's own validator, not this one
    public async Task Accepts_PasswordsWithALetterAndADigit(string password)
    {
        Assert.Empty(await ErrorCodesAsync(password));
    }

    [Fact]
    public async Task Rejects_PasswordWithoutADigit()
    {
        Assert.Equal(new[] { "PasswordRequiresDigit" }, await ErrorCodesAsync("abcdefgh"));
    }

    [Fact]
    public async Task Rejects_PasswordWithoutALetter()
    {
        Assert.Equal(new[] { "PasswordRequiresLetter" }, await ErrorCodesAsync("12345678"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Rejects_EmptyPassword_WithBothErrors(string? password)
    {
        Assert.Equal(new[] { "PasswordRequiresLetter", "PasswordRequiresDigit" }, await ErrorCodesAsync(password));
    }
}

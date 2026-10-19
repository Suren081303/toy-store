using Shop.Web.Identity;

namespace Shop.Tests;

public class TokenEncodingTests
{
    [Fact]
    public void EncodeThenDecode_ReturnsTheOriginalToken()
    {
        // Real Identity tokens contain +, / and = characters.
        const string token = "CfDJ8N+/abc==_Ünïcode-123";

        var encoded = TokenEncoding.Encode(token);

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
        Assert.True(TokenEncoding.TryDecode(encoded, out var decoded));
        Assert.Equal(token, decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("***not base64***")]
    [InlineData("abc def")]
    public void TryDecode_RejectsMangledText(string? text)
    {
        Assert.False(TokenEncoding.TryDecode(text, out var decoded));
        Assert.Equal(string.Empty, decoded);
    }
}

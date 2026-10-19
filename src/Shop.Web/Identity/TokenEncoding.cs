using System.Buffers.Text;
using System.Text;

namespace Shop.Web.Identity;

// Identity tokens contain characters that are awkward in links, so they travel as URL-safe text.
public static class TokenEncoding
{
    public static string Encode(string token)
    {
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(token));
    }

    // False when the text is not a valid encoded token (for example a mangled link).
    public static bool TryDecode(string? encoded, out string token)
    {
        token = string.Empty;

        if (string.IsNullOrEmpty(encoded) || !Base64Url.IsValid(encoded))
        {
            return false;
        }

        token = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(encoded));
        return true;
    }
}

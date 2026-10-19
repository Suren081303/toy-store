namespace Shop.Core.Phones;

public static class PhoneNumbers
{
    // Armenian numbers in any usual writing -> "+374XXXXXXXX". Other countries must start with "+" or "00".
    //   091 123456, 091123456, 91123456, +374 91 123456, 0037491123456, (091) 123-456  -> +37491123456
    //   +7 912 345 6789                                                               -> +79123456789
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();

        if (text.Any(c => !char.IsAsciiDigit(c) && !" -().+".Contains(c)))
        {
            return false;
        }

        var plusCount = text.Count(c => c == '+');
        if (plusCount > 1 || (plusCount == 1 && text[0] != '+'))
        {
            return false;
        }

        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());

        if (plusCount == 1)
        {
            return Accept("+" + digits, out normalized);
        }

        if (digits.StartsWith("00"))
        {
            return Accept("+" + digits[2..], out normalized);
        }

        if (digits.Length == 8)
        {
            return Accept("+374" + digits, out normalized);
        }

        if (digits.Length == 9 && digits[0] == '0')
        {
            return Accept("+374" + digits[1..], out normalized);
        }

        if (digits.Length == 11 && digits.StartsWith("374"))
        {
            return Accept("+" + digits, out normalized);
        }

        return false;
    }

    private static bool Accept(string candidate, out string normalized)
    {
        normalized = string.Empty;
        var digits = candidate[1..];

        // E.164: up to 15 digits, no leading zero. Armenia (374) has exactly 8 digits after the country code.
        if (digits.Length is < 8 or > 15 || digits[0] == '0')
        {
            return false;
        }

        if (digits.StartsWith("374") && digits.Length != 11)
        {
            return false;
        }

        normalized = candidate;
        return true;
    }
}

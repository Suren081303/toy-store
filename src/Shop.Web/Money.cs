using System.Globalization;

namespace Shop.Web;

public static class Money
{
    // 12900 -> "12 900 ֏" (separator depends on the current language).
    public static string Format(decimal amount)
    {
        return amount.ToString("N0", CultureInfo.CurrentCulture) + " ֏";
    }
}

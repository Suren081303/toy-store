using System.Globalization;

namespace Shop.Web;

public static class CurrentCulture
{
    // "hy", "ru" or "en" - set from the URL by the localization middleware.
    public static string Code => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
}

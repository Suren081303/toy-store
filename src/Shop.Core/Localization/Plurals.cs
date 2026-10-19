namespace Shop.Core.Localization;

public enum PluralForm
{
    One,
    Few,
    Many
}

public static class Plurals
{
    // Which wording to use after a number: ru has three forms, en has two, hy uses the same form for every number.
    public static PluralForm GetForm(string culture, int number)
    {
        return culture switch
        {
            "ru" => GetRussianForm(number),
            "en" => number == 1 ? PluralForm.One : PluralForm.Many,
            _ => PluralForm.Many
        };
    }

    private static PluralForm GetRussianForm(int number)
    {
        var abs = Math.Abs(number);
        var lastTwo = abs % 100;
        var last = abs % 10;

        if (last == 1 && lastTwo != 11)
        {
            return PluralForm.One;
        }

        if (last is >= 2 and <= 4 && lastTwo is < 12 or > 14)
        {
            return PluralForm.Few;
        }

        return PluralForm.Many;
    }
}

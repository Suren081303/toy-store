using System.Text;
using Shop.Core.Admin;

namespace Shop.Tests;

public class AnalyticsPeriodTests
{
    private static readonly DateOnly Today = new(2030, 3, 15);

    [Fact]
    public void Today_IsOneDay_AndThePreviousPeriodIsYesterday()
    {
        var period = AnalyticsPeriods.Resolve(PeriodPreset.Today, null, null, Today, out var invalid);

        Assert.False(invalid);
        Assert.Equal(new AnalyticsPeriod(Today, Today), period);
        Assert.Equal(1, period.Days);
        Assert.Equal(new AnalyticsPeriod(new DateOnly(2030, 3, 14), new DateOnly(2030, 3, 14)), period.Previous);
    }

    [Fact]
    public void Last7Days_EndsToday_AndThePreviousPeriodIsTheSevenDaysBefore()
    {
        var period = AnalyticsPeriods.Resolve(PeriodPreset.Last7Days, null, null, Today, out _);

        Assert.Equal(new AnalyticsPeriod(new DateOnly(2030, 3, 9), Today), period);
        Assert.Equal(new AnalyticsPeriod(new DateOnly(2030, 3, 2), new DateOnly(2030, 3, 8)), period.Previous);
    }

    [Fact]
    public void Last30Days_Is30DaysIncludingToday()
    {
        var period = AnalyticsPeriods.Resolve(PeriodPreset.Last30Days, null, null, Today, out _);

        Assert.Equal(30, period.Days);
        Assert.Equal(Today, period.To);
    }

    [Fact]
    public void ThisMonth_RunsFromTheFirstToToday_AndIsComparedWithTheSameNumberOfDaysBefore()
    {
        var period = AnalyticsPeriods.Resolve(PeriodPreset.ThisMonth, null, null, Today, out _);

        Assert.Equal(new AnalyticsPeriod(new DateOnly(2030, 3, 1), Today), period);
        Assert.Equal(new AnalyticsPeriod(new DateOnly(2030, 2, 14), new DateOnly(2030, 2, 28)), period.Previous);
    }

    [Fact]
    public void LastMonth_IsTheWholePreviousMonth()
    {
        var period = AnalyticsPeriods.Resolve(PeriodPreset.LastMonth, null, null, Today, out _);

        Assert.Equal(new AnalyticsPeriod(new DateOnly(2030, 2, 1), new DateOnly(2030, 2, 28)), period);
        Assert.Equal(new AnalyticsPeriod(new DateOnly(2030, 1, 4), new DateOnly(2030, 1, 31)), period.Previous);
    }

    [Fact]
    public void LastMonth_InJanuary_IsDecemberOfThePreviousYear()
    {
        var period = AnalyticsPeriods.Resolve(PeriodPreset.LastMonth, null, null, new DateOnly(2031, 1, 5), out _);

        Assert.Equal(new AnalyticsPeriod(new DateOnly(2030, 12, 1), new DateOnly(2030, 12, 31)), period);
    }

    [Fact]
    public void Custom_Valid_IsUsedAsTyped()
    {
        var period = AnalyticsPeriods.Resolve(PeriodPreset.Custom, new DateOnly(2030, 1, 10), new DateOnly(2030, 1, 20), Today, out var invalid);

        Assert.False(invalid);
        Assert.Equal(new AnalyticsPeriod(new DateOnly(2030, 1, 10), new DateOnly(2030, 1, 20)), period);
    }

    [Fact]
    public void Custom_OneDay_IsAllowed()
    {
        var period = AnalyticsPeriods.Resolve(PeriodPreset.Custom, Today, Today, Today, out var invalid);

        Assert.False(invalid);
        Assert.Equal(1, period.Days);
    }

    [Fact]
    public void Custom_Of366Days_IsAllowed_Of367IsNot()
    {
        var from = new DateOnly(2029, 1, 1);

        var ok = AnalyticsPeriods.Resolve(PeriodPreset.Custom, from, from.AddDays(365), Today, out var okInvalid);
        var tooLong = AnalyticsPeriods.Resolve(PeriodPreset.Custom, from, from.AddDays(366), Today, out var longInvalid);

        Assert.False(okInvalid);
        Assert.Equal(366, ok.Days);
        Assert.True(longInvalid);
        Assert.Equal(30, tooLong.Days);
    }

    [Theory]
    [InlineData(false, false)] // nothing typed
    [InlineData(true, false)] // only the first date
    [InlineData(false, true)] // only the second date
    public void Custom_WithAMissingDate_FallsBackToTheLast30Days(bool hasFrom, bool hasTo)
    {
        var period = AnalyticsPeriods.Resolve(
            PeriodPreset.Custom, hasFrom ? new DateOnly(2030, 3, 1) : null, hasTo ? new DateOnly(2030, 3, 5) : null, Today, out var invalid);

        Assert.True(invalid);
        Assert.Equal(new AnalyticsPeriod(Today.AddDays(-29), Today), period);
    }

    [Fact]
    public void Custom_Reversed_FallsBackToTheLast30Days()
    {
        var period = AnalyticsPeriods.Resolve(PeriodPreset.Custom, new DateOnly(2030, 3, 10), new DateOnly(2030, 3, 5), Today, out var invalid);

        Assert.True(invalid);
        Assert.Equal(new AnalyticsPeriod(Today.AddDays(-29), Today), period);
    }
}

public class SalesTotalsTests
{
    private static SalesTotals Totals(decimal sales = 0, int orders = 0, int cancelled = 0, decimal refunded = 0, int registered = 0) =>
        new(sales, orders, 0, 0, 0, cancelled, 0, refunded, 0, registered, 0);

    [Fact]
    public void NothingSold_GivesZeros_NotErrors()
    {
        var totals = Totals();

        Assert.Equal(0, totals.AverageOrder);
        Assert.Equal(0, totals.CancelRate);
        Assert.Equal(0, totals.NetSales);
    }

    [Fact]
    public void AverageOrder_IsRoundedToWholeDrams()
    {
        Assert.Equal(3333, Totals(sales: 10000, orders: 3).AverageOrder);
    }

    [Fact]
    public void NetSales_TakesTheRefundsOff()
    {
        Assert.Equal(7000, Totals(sales: 10000, refunded: 3000).NetSales);
    }

    [Fact]
    public void CancelRate_IsCancelledAmongConfirmedAndCancelled()
    {
        Assert.Equal(25m, Totals(orders: 3, cancelled: 1).CancelRate);
        Assert.Equal(33.3m, Totals(orders: 2, cancelled: 1).CancelRate);
    }

    [Fact]
    public void GuestOrders_AreTheOnesWithoutAnAccount()
    {
        Assert.Equal(3, Totals(orders: 5, registered: 2).GuestOrders);
    }
}

public class AnalyticsChartTests
{
    private static List<DayPoint> Days(int count) =>
        Enumerable.Range(0, count).Select(i => new DayPoint(new DateOnly(2030, 1, 1).AddDays(i), 100, 1)).ToList();

    [Fact]
    public void UpTo62Days_EveryDayHasItsOwnBar()
    {
        var bars = AnalyticsChart.Bars(Days(62));

        Assert.Equal(62, bars.Count);
        Assert.All(bars, b => Assert.Equal(b.From, b.To));
    }

    [Fact]
    public void LongerPeriods_AreShownByWeeks_AndNothingIsLost()
    {
        var days = Days(64);

        var bars = AnalyticsChart.Bars(days);

        Assert.Equal(10, bars.Count); // 9 whole weeks and a last bar of one day
        Assert.Equal(new DateOnly(2030, 1, 1), bars[0].From);
        Assert.Equal(new DateOnly(2030, 1, 7), bars[0].To);
        Assert.Equal(bars[^1].From, bars[^1].To);
        Assert.Equal(days.Sum(d => d.Sales), bars.Sum(b => b.Sales));
        Assert.Equal(days.Sum(d => d.Orders), bars.Sum(b => b.Orders));
    }
}

public class CsvWriterTests
{
    private static string Text(byte[] file) => Encoding.UTF8.GetString(file, 3, file.Length - 3);

    [Fact]
    public void File_StartsWithTheByteOrderMark_AndLinesEndWithCrLf()
    {
        var file = CsvWriter.Build(["A", "B"], [new object?[] { 1, "x" }]);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file[..3]);
        Assert.Equal("A,B\r\n1,x\r\n", Text(file));
    }

    [Fact]
    public void Numbers_AreWrittenPlainly_AndNegativeNumbersAreNotTouched()
    {
        Assert.Equal("-5", CsvWriter.Cell(-5));
        Assert.Equal("12900", CsvWriter.Cell(12900m));
        Assert.Equal("12900.5", CsvWriter.Cell(12900.50m));
        Assert.Equal("-1200", CsvWriter.Cell(-1200m));
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+7", "'+7")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "'\tcmd")]
    public void TextThatLooksLikeAFormula_GetsAnApostrophe(string input, string expected)
    {
        Assert.Equal(expected, CsvWriter.Cell(input));
    }

    [Fact]
    public void TextWithCommasQuotesOrNewlines_IsQuoted()
    {
        Assert.Equal("\"a,b\"", CsvWriter.Cell("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", CsvWriter.Cell("say \"hi\""));
        Assert.Equal("\"one\ntwo\"", CsvWriter.Cell("one\ntwo"));
    }

    [Fact]
    public void FormulaWithAComma_IsBothMarkedAndQuoted()
    {
        Assert.Equal("\"'=A1,B1\"", CsvWriter.Cell("=A1,B1"));
    }

    [Fact]
    public void ArmenianAndRussianLetters_SurviveTheRoundTrip()
    {
        var file = CsvWriter.Build(["Name"], [new object?[] { "Խաղալիք" }, new object?[] { "Кубики" }]);

        Assert.Equal("Name\r\nԽաղալիք\r\nКубики\r\n", Text(file));
    }

    [Fact]
    public void NullAndDates_AreFormattedInvariantly()
    {
        Assert.Equal(string.Empty, CsvWriter.Cell(null));
        Assert.Equal("2030-03-10 14:05", CsvWriter.Cell(new DateTime(2030, 3, 10, 14, 5, 30)));
    }
}

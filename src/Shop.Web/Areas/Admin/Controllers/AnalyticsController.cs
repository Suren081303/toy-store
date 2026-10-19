using Microsoft.AspNetCore.Mvc;
using Shop.Core.Admin;
using Shop.Core.Orders;
using Shop.Web.Areas.Admin.Models;

namespace Shop.Web.Areas.Admin.Controllers;

public class AnalyticsController : AdminControllerBase
{
    private readonly IAdminAnalyticsService _analytics;
    private readonly TimeProvider _time;

    public AnalyticsController(IAdminAnalyticsService analytics, TimeProvider time)
    {
        _analytics = analytics;
        _time = time;
    }

    [HttpGet]
    public async Task<IActionResult> Index(PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null)
    {
        var period = AnalyticsPeriods.Resolve(preset, from, to, ShopClock.Today(_time), out var customWasInvalid);
        var report = await _analytics.GetReportAsync(period, CurrentCulture.Code);

        // An invalid custom range was replaced by the last 30 days; the picker shows that, not "Custom".
        return View(new AdminAnalyticsViewModel(report, customWasInvalid ? PeriodPreset.Last30Days : preset, customWasInvalid));
    }

    // The exports are POST: they write an audit line, so a link or a prefetch must not trigger them.
    [HttpPost]
    public async Task<IActionResult> ExportOrders(PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null)
    {
        var period = AnalyticsPeriods.Resolve(preset, from, to, ShopClock.Today(_time), out _);
        var file = await _analytics.ExportOrdersCsvAsync(period, AdminId);
        return File(file, "text/csv; charset=utf-8", $"orders-{period.From:yyyy-MM-dd}_{period.To:yyyy-MM-dd}.csv");
    }

    [HttpPost]
    public async Task<IActionResult> ExportItems(PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null)
    {
        var period = AnalyticsPeriods.Resolve(preset, from, to, ShopClock.Today(_time), out _);
        var file = await _analytics.ExportItemsCsvAsync(period, AdminId);
        return File(file, "text/csv; charset=utf-8", $"items-{period.From:yyyy-MM-dd}_{period.To:yyyy-MM-dd}.csv");
    }
}

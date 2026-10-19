using Microsoft.Extensions.Options;
using Shop.Core.Carts;

namespace Shop.Web.Background;

// Deletes guest carts that were not touched for CartOptions.GuestCartDays days. Runs a minute after the site starts, then once a day.
// Errors are not caught here: they are logged by the host and the job stops, but the site keeps running.
public class GuestCartCleanupService : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopes;
    private readonly CartOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GuestCartCleanupService> _logger;

    public GuestCartCleanupService(
        IServiceScopeFactory scopes,
        IOptions<CartOptions> options,
        TimeProvider time,
        ILogger<GuestCartCleanupService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(FirstRunDelay, stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            await RunOnceAsync();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync()
    {
        using var scope = _scopes.CreateScope();
        var carts = scope.ServiceProvider.GetRequiredService<ICartService>();

        var limit = _time.GetUtcNow().UtcDateTime.AddDays(-_options.GuestCartDays);
        var deleted = await carts.DeleteOldGuestCartsAsync(limit);

        _logger.LogInformation("Deleted {Count} guest carts not touched for {Days} days.", deleted, _options.GuestCartDays);
        return deleted;
    }
}

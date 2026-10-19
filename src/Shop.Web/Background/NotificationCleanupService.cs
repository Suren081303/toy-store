using Shop.Core.Notifications;

namespace Shop.Web.Background;

// Deletes old bell items (90 days) and sent emails (30 days). Runs two minutes after the site starts, then once a day.
// Errors are not caught here: they are logged by the host and the job stops, but the site keeps running.
public class NotificationCleanupService : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<NotificationCleanupService> _logger;

    public NotificationCleanupService(IServiceScopeFactory scopes, ILogger<NotificationCleanupService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(FirstRunDelay, stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            using var scope = _scopes.CreateScope();
            var (notifications, emails) = await scope.ServiceProvider.GetRequiredService<INotificationCleanup>().CleanAsync();

            _logger.LogInformation("Deleted {Notifications} old bell items and {Emails} old emails.", notifications, emails);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

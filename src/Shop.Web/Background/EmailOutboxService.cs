using Shop.Core.Notifications;

namespace Shop.Web.Background;

// Sends the queued emails: first run 10 seconds after the site starts, then every Email:OutboxIntervalSeconds (default 30).
// An interval of 0 or less switches the job off (the tests do this and send by hand).
// Unlike the other jobs, this one logs an unexpected error (for example the database being down for a moment) and carries on
// with the next round: a single hiccup must not stop all email until the site is restarted. The error is logged, not hidden.
public class EmailOutboxService : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EmailOutboxService> _logger;
    private readonly int _intervalSeconds;

    public EmailOutboxService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<EmailOutboxService> logger)
    {
        _scopes = scopes;
        _logger = logger;
        _intervalSeconds = configuration.GetValue("Email:OutboxIntervalSeconds", 30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_intervalSeconds <= 0)
        {
            _logger.LogInformation("The email queue sender is switched off (Email:OutboxIntervalSeconds is {Seconds}).", _intervalSeconds);
            return;
        }

        await Task.Delay(FirstRunDelay, stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_intervalSeconds));
        do
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var handled = await scope.ServiceProvider.GetRequiredService<IEmailOutboxProcessor>().ProcessAsync();
                if (handled > 0)
                {
                    _logger.LogInformation("The email queue sender dealt with {Count} emails.", handled);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "A round of the email queue sender failed; it will try again at the next round.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

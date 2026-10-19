using Shop.Core.Payments;

namespace Shop.Web.Background;

// Cancels unpaid card and bank-transfer orders whose time ran out, and puts their stock back.
// Runs a minute after the site starts, then every 5 minutes. Errors are not caught here: they are logged by the host
// and the job stops, but the site keeps running.
public class PaymentExpiryService : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PaymentExpiryService> _logger;

    public PaymentExpiryService(IServiceScopeFactory scopes, ILogger<PaymentExpiryService> logger)
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
            await RunOnceAsync();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync()
    {
        using var scope = _scopes.CreateScope();
        var payments = scope.ServiceProvider.GetRequiredService<IPaymentService>();

        var cancelled = await payments.ExpireUnpaidOrdersAsync();
        if (cancelled > 0)
        {
            _logger.LogInformation("Cancelled {Count} unpaid orders whose time to pay ran out.", cancelled);
        }

        return cancelled;
    }
}

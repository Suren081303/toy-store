using Microsoft.EntityFrameworkCore;
using Shop.Core.Email;
using Shop.Core.Notifications;
using Shop.Infrastructure.Data;

namespace Shop.Web.Notifications;

// Sends the emails in the queue that are due. For each one: make the text, send it, and write down the result.
// A failure is not thrown away: the error message is saved on the email (the admin "Emails" screen shows it), it is logged
// as an error, and the email is tried again later (1, 5, 15, 60 minutes), up to OutboxRules.MaxAttempts times, then it is marked Failed.
public class EmailOutboxProcessor : IEmailOutboxProcessor
{
    private const int BatchSize = 20;

    // One round at a time inside this site (the background job and a manual "send now" must not send the same email twice).
    private static readonly SemaphoreSlim OneRoundAtATime = new(1, 1);

    private readonly ShopDbContext _db;
    private readonly OrderEmails _renderer;
    private readonly IEmailSender _sender;
    private readonly TimeProvider _time;
    private readonly ILogger<EmailOutboxProcessor> _logger;

    public EmailOutboxProcessor(
        ShopDbContext db,
        OrderEmails renderer,
        IEmailSender sender,
        TimeProvider time,
        ILogger<EmailOutboxProcessor> logger)
    {
        _db = db;
        _renderer = renderer;
        _sender = sender;
        _time = time;
        _logger = logger;
    }

    public async Task<int> ProcessAsync()
    {
        await OneRoundAtATime.WaitAsync();
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;

            var due = await _db.OutboxEmails
                .Where(e => e.Status == OutboxStatus.Pending && e.NextAttemptAt <= now)
                .OrderBy(e => e.Id)
                .Take(BatchSize)
                .ToListAsync();

            foreach (var email in due)
            {
                email.Attempts++;

                try
                {
                    var rendered = await _renderer.RenderAsync(email);
                    email.ToAddress = rendered.To;

                    await _sender.SendAsync(rendered.To, rendered.Subject, rendered.Html);

                    email.Status = OutboxStatus.Sent;
                    email.SentAt = _time.GetUtcNow().UtcDateTime;
                    email.LastError = null;
                }
                catch (Exception ex)
                {
                    // Recorded and logged, never swallowed (see the comment at the top of this class).
                    _logger.LogError(ex, "Sending queued email {EmailId} ({Kind}, order {Order}) failed on attempt {Attempt}.", email.Id, email.Kind, email.OrderNumber, email.Attempts);

                    var message = $"{ex.GetType().Name}: {ex.Message}";
                    email.LastError = message.Length > 1000 ? message[..1000] : message;

                    if (email.Attempts >= OutboxRules.MaxAttempts)
                    {
                        email.Status = OutboxStatus.Failed;
                    }
                    else
                    {
                        email.NextAttemptAt = now + OutboxRules.RetryDelay(email.Attempts);
                    }
                }

                await _db.SaveChangesAsync();
            }

            return due.Count;
        }
        finally
        {
            OneRoundAtATime.Release();
        }
    }
}

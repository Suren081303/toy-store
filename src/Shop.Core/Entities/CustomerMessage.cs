namespace Shop.Core.Entities;

// A message an admin sent to a customer (by email). The text is kept here, so the admin can see what was sent;
// the email itself waits in the outbox like every other email. Plain text only.
public class CustomerMessage
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    // The language of the email's frame (greeting, footnote): the language the admin was using when sending.
    public string Culture { get; set; } = "hy";

    public string? SentByUserId { get; set; }

    // The admin's email at that moment (kept even if the account is later deleted).
    public string SentByEmail { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

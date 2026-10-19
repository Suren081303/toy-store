using Shop.Core.Notifications;

namespace Shop.Core.Entities;

// One item in the admins' bell. It is written in the same transaction as the event it reports.
// Only facts are stored (numbers, a name); the text is made when it is shown, in the language of the admin who looks at it.
public class AdminNotification
{
    public int Id { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public NotificationType Type { get; set; }

    public int? OrderNumber { get; set; }

    public int? ReturnId { get; set; }

    public int? ProductId { get; set; }

    public decimal? Amount { get; set; }

    // A product name (Armenian) for low-stock items.
    public string? Name { get; set; }

    public int? Stock { get; set; }

    // Read state is shared: when one admin reads an item, it is read for everyone.
    public bool IsRead { get; set; }

    public DateTime? ReadAt { get; set; }

    public string? ReadByUserId { get; set; }
}

// An email waiting to be sent, or already sent. The email text is not stored: the sender job makes it from the kind and the order,
// when it is time to send. The row is written in the same transaction as the order change, so an email can never be lost
// between "saved" and "sent", and a mail outage never breaks an order.
public class OutboxEmail
{
    public int Id { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public EmailKind Kind { get; set; }

    public int OrderNumber { get; set; }

    public int? ReturnId { get; set; }

    // For an email that is an admin's message to a customer (then OrderNumber is 0).
    public int? CustomerMessageId { get; set; }

    // A note for the text: the admin's note on a cancel or a return answer, or a code such as "expired".
    public string? Detail { get; set; }

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;

    public int Attempts { get; set; }

    // When the sender may try again (now, for a new email).
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;

    public DateTime? SentAt { get; set; }

    // Filled when a send is attempted.
    public string? ToAddress { get; set; }

    // What went wrong in the last attempt (the mail server's message).
    public string? LastError { get; set; }
}

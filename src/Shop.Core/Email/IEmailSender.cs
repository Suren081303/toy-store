namespace Shop.Core.Email;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody);
}

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Shop.Core.Email;

namespace Shop.Infrastructure.Email;

// Sends email through any SMTP server (set in Email:Smtp). Every message has the HTML page and a plain-text copy.
// A failure is not hidden: the exception goes to the caller, and the email queue keeps the message and tries again later.
public class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;

    public SmtpEmailSender(IOptions<EmailOptions> options)
    {
        _options = options.Value;
    }

    public async Task SendAsync(string to, string subject, string htmlBody)
    {
        var smtp = _options.Smtp;
        var message = BuildMessage(_options, to, subject, htmlBody);

        using var client = new SmtpClient { Timeout = Math.Max(5, smtp.TimeoutSeconds) * 1000 };

        await client.ConnectAsync(smtp.Host, smtp.Port, SecurityOf(smtp.Security));

        if (!string.IsNullOrEmpty(smtp.UserName))
        {
            await client.AuthenticateAsync(smtp.UserName, smtp.Password);
        }

        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);
    }

    public static SecureSocketOptions SecurityOf(string? security) => security?.Trim().ToLowerInvariant() switch
    {
        "sslonconnect" => SecureSocketOptions.SslOnConnect,
        "none" => SecureSocketOptions.None,
        null or "" or "starttls" => SecureSocketOptions.StartTls,
        _ => throw new InvalidOperationException($"Email:Smtp:Security \"{security}\" is not valid. Use StartTls, SslOnConnect or None.")
    };

    public static MimeMessage BuildMessage(EmailOptions options, string to, string subject, string htmlBody)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody, TextBody = ToPlainText(htmlBody) }.ToMessageBody();
        return message;
    }

    // A readable plain-text copy of the HTML: links keep their address, blocks become lines.
    public static string ToPlainText(string html)
    {
        var text = Regex.Replace(html, @"<(style|script|head)[\s\S]*?</\1>", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<a\s[^>]*href=""([^""]*)""[^>]*>([\s\S]*?)</a>", m =>
        {
            var label = Regex.Replace(m.Groups[2].Value, "<[^>]+>", string.Empty).Trim();
            var url = WebUtility.HtmlDecode(m.Groups[1].Value);
            return string.IsNullOrEmpty(label) || label == url ? url : $"{label} ({url})";
        }, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<br\s*/?>|</(p|div|h[1-6]|tr|li|table)>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</t[dh]>", "  ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim() + "\n";
    }
}

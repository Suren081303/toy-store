using System.Text;
using Microsoft.Extensions.Options;
using Shop.Core.Email;

namespace Shop.Infrastructure.Email;

// Development email sender: saves each message as an .html file (open it in a browser to read it and click its links).
public class FileEmailSender : IEmailSender
{
    private readonly EmailOptions _options;

    public FileEmailSender(IOptions<EmailOptions> options)
    {
        _options = options.Value;
    }

    public async Task SendAsync(string to, string subject, string htmlBody)
    {
        Directory.CreateDirectory(_options.Folder);

        var safeRecipient = string.Concat(to.Select(c => char.IsLetterOrDigit(c) || c is '@' or '.' or '-' or '_' ? c : '_'));
        var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}_{Guid.NewGuid().ToString("N")[..6]}_{safeRecipient}.html";

        // The envelope goes in an HTML comment at the end, so the message itself stays a normal HTML page.
        var header = $"<!--\nTo: {to}\nFrom: {_options.FromName} <{_options.FromAddress}>\nSubject: {subject.Replace("--", "- -")}\n-->";

        await File.WriteAllTextAsync(Path.Combine(_options.Folder, fileName), htmlBody + "\n" + header, Encoding.UTF8);
    }
}

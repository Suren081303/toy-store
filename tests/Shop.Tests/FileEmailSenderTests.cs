using Microsoft.Extensions.Options;
using Shop.Core.Email;
using Shop.Infrastructure.Email;

namespace Shop.Tests;

public class FileEmailSenderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "shop-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task SendAsync_SavesTheMessageAsAnHtmlFile()
    {
        var sender = new FileEmailSender(Options.Create(new EmailOptions { Folder = _folder, FromName = "Shop", FromAddress = "no-reply@example.com" }));

        await sender.SendAsync("a+b@example.com", "Confirm -- your email", "<html><body>Hello</body></html>");

        var file = Assert.Single(Directory.GetFiles(_folder));
        Assert.EndsWith(".html", file);
        Assert.DoesNotContain("+", Path.GetFileName(file));

        var content = await File.ReadAllTextAsync(file);
        Assert.StartsWith("<html><body>Hello</body></html>", content);
        Assert.Contains("To: a+b@example.com", content);
        Assert.Contains("From: Shop <no-reply@example.com>", content);
        // "--" would end the HTML comment that holds the envelope.
        Assert.Contains("Subject: Confirm - - your email", content);
    }

    [Fact]
    public async Task SendAsync_TwoMessagesToTheSamePerson_AreTwoFiles()
    {
        var sender = new FileEmailSender(Options.Create(new EmailOptions { Folder = _folder }));

        await sender.SendAsync("same@example.com", "One", "<p>1</p>");
        await sender.SendAsync("same@example.com", "Two", "<p>2</p>");

        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }
}

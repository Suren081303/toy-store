namespace Shop.Core.Email;

public class EmailOptions
{
    public const string SectionName = "Email";

    // "File" (default): every email is saved as a file, for development. "Smtp": emails are sent through the server in Smtp.
    public string Provider { get; set; } = "File";

    public SmtpOptions Smtp { get; set; } = new();

    public string FromName { get; set; } = "Shop";

    public string FromAddress { get; set; } = "no-reply@example.com";

    // Development: every email is saved here as an .html file instead of being sent.
    public string Folder { get; set; } = string.Empty;
}

public class SmtpOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    // StartTls (port 587, the usual one), SslOnConnect (port 465) or None (only for a test server on this computer).
    public string Security { get; set; } = "StartTls";

    // Leave empty for a server that needs no sign-in. Keep the password out of appsettings.json: use the environment variable Email__Smtp__Password.
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;
}

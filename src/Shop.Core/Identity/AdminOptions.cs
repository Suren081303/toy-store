namespace Shop.Core.Identity;

// The first admin account, created at startup. Section "Admin" in configuration
// (appsettings.Development.json in development, environment variables Admin__Email / Admin__Password in production).
public class AdminOptions
{
    public const string SectionName = "Admin";

    public string? Email { get; set; }

    public string? Password { get; set; }
}

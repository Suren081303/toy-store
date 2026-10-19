namespace Shop.Web.Hosting;

// Settings that must be right before the site goes live. In Production the site refuses to start with an Error here
// (the message tells what to set), and logs each Warning. The checks only read configuration; they change nothing.
public static class ProductionChecks
{
    // The sample values from appsettings.json; real ones must replace them.
    private const string SamplePhone = "+374 00 000 000";
    private const string SampleAddress = "Yerevan, Armenia";
    private const string SampleShopEmail = "shop@example.com";
    private const string SampleFromAddress = "no-reply@example.com";

    public record Result(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

    public static Result Run(IConfiguration configuration)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")))
        {
            errors.Add("ConnectionStrings:DefaultConnection is empty. Set the database connection string.");
        }

        CheckBaseUrl(configuration["Shop:BaseUrl"], errors);

        if (IsMissingOrSample(configuration["Shop:Email"], SampleShopEmail))
        {
            errors.Add("Shop:Email is not set (or still the sample value). New-order notices and the contact address use it.");
        }

        if (IsMissingOrSample(configuration["Email:FromAddress"], SampleFromAddress))
        {
            errors.Add("Email:FromAddress is not set (or still the sample value). It is the sender of every email.");
        }

        CheckEmailProvider(configuration, errors);

        if (string.Equals(configuration["Payments:Provider"], "Fake", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Payments:Provider is \"Fake\". The fake bank is for development only; leave it empty until a real bank is connected.");
        }

        if (string.IsNullOrWhiteSpace(configuration["Admin:Email"]) || string.IsNullOrWhiteSpace(configuration["Admin:Password"]))
        {
            warnings.Add("Admin:Email / Admin:Password are not set. No first admin will be created (fine if an admin already exists).");
        }

        var uploads = configuration["Uploads:Folder"];
        if (string.IsNullOrWhiteSpace(uploads))
        {
            warnings.Add("Uploads:Folder is not set. Photos are saved inside the site's own folder and can be lost when a new version is deployed. Set a full path outside the site, and back it up.");
        }
        else if (!Path.IsPathRooted(uploads))
        {
            errors.Add($"Uploads:Folder \"{uploads}\" must be a full path (for example /var/shop/uploads or D:\\shop\\uploads).");
        }

        if (configuration["AllowedHosts"] is null or "" or "*")
        {
            warnings.Add("AllowedHosts is \"*\". Set it to the site's host name (for example shop.example.am).");
        }

        if (IsMissingOrSample(configuration["Shop:Phone"], SamplePhone) || IsMissingOrSample(configuration["Shop:Address"], SampleAddress))
        {
            warnings.Add("Shop:Phone or Shop:Address is still the sample value. It is shown in the footer and on the legal pages.");
        }

        if (string.IsNullOrWhiteSpace(configuration["Shop:LegalName"]))
        {
            warnings.Add("Shop:LegalName is not set. The Terms page should name the company or sole trader that sells.");
        }

        return new Result(errors, warnings);
    }

    private static bool IsMissingOrSample(string? value, string sample) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), sample, StringComparison.OrdinalIgnoreCase);

    private static void CheckBaseUrl(string? baseUrl, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            errors.Add("Shop:BaseUrl is empty. Set the public address, for example https://shop.example.am (emails, the sitemap and links need it).");
            return;
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.PathAndQuery != "/" || uri.IsLoopback)
        {
            errors.Add($"Shop:BaseUrl \"{baseUrl}\" is not valid. It must be the public https address with no path, for example https://shop.example.am");
        }
    }

    private static void CheckEmailProvider(IConfiguration configuration, List<string> errors)
    {
        var provider = configuration["Email:Provider"];

        if (!string.Equals(provider, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Email:Provider must be \"Smtp\" in Production. Without it emails are only written to files and nobody receives them.");
            return;
        }

        if (string.IsNullOrWhiteSpace(configuration["Email:Smtp:Host"]))
        {
            errors.Add("Email:Smtp:Host is empty. Set the mail server's host name.");
        }

        if (!int.TryParse(configuration["Email:Smtp:Port"] ?? "587", out var port) || port is < 1 or > 65535)
        {
            errors.Add("Email:Smtp:Port is not a valid port number.");
        }

        var security = configuration["Email:Smtp:Security"] ?? "StartTls";
        if (!new[] { "StartTls", "SslOnConnect", "None" }.Contains(security, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("Email:Smtp:Security must be StartTls, SslOnConnect or None.");
        }

        if (string.Equals(security, "None", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Email:Smtp:Security is None. Emails with sign-in and password-reset links must not travel unencrypted.");
        }
    }
}

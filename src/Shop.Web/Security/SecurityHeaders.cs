namespace Shop.Web.Security;

// Headers that every response carries. The Content-Security-Policy lets the browser load only what this site itself serves:
// no inline scripts, no outside scripts, styles or fonts (no CDN). Inline style="" attributes are allowed (style-src-attr),
// for example the width of a bar in the analytics tables; they cannot run code.
public static class SecurityHeaders
{
    public const string ExtraFormActionOrigins = "Security:ExtraFormActionOrigins";

    // The Content-Security-Policy text. extraFormActions: other sites a form or redirect may send the browser to, for example the bank's
    // payment page (a policy of "form-action 'self'" also blocks the redirect that follows a form post).
    public static string BuildPolicy(IEnumerable<string>? extraFormActions)
    {
        var formAction = new List<string> { "'self'" };

        foreach (var entry in extraFormActions ?? [])
        {
            // Only a clean web origin is let into the header (https://bank.example), so a setting cannot add other directives.
            if (Uri.TryCreate(entry?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            {
                formAction.Add(uri.GetLeftPart(UriPartial.Authority));
            }
        }

        return string.Join("; ",
            "default-src 'self'",
            "base-uri 'self'",
            "object-src 'none'",
            "frame-ancestors 'none'",
            "form-action " + string.Join(' ', formAction.Distinct()),
            "script-src 'self'",
            "style-src 'self'",
            "style-src-attr 'unsafe-inline'",
            "img-src 'self' data:",
            "font-src 'self'",
            "connect-src 'self'",
            "manifest-src 'self'");
    }

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, IConfiguration configuration)
    {
        var policy = BuildPolicy(configuration.GetSection(ExtraFormActionOrigins).Get<string[]>());

        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = policy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";

            await next();
        });
    }
}

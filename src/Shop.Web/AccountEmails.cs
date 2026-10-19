using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Localization;
using Shop.Core.Email;
using Shop.Core.Entities;

namespace Shop.Web;

// The emails of the account pages, written in the language the visitor is browsing in.
public class AccountEmails
{
    private readonly IEmailSender _sender;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IConfiguration _configuration;

    public AccountEmails(IEmailSender sender, IStringLocalizer<SharedResource> localizer, IConfiguration configuration)
    {
        _sender = sender;
        _localizer = localizer;
        _configuration = configuration;
    }

    public Task SendConfirmationAsync(ApplicationUser user, string confirmUrl)
    {
        return SendAsync(user, confirmUrl, "Confirm");
    }

    public Task SendPasswordResetAsync(ApplicationUser user, string resetUrl)
    {
        return SendAsync(user, resetUrl, "Reset");
    }

    // Keys: Email_<kind>Subject, Email_<kind>Heading ({0} = name), Email_<kind>Intro, Email_<kind>Button, Email_<kind>Footnote.
    private Task SendAsync(ApplicationUser user, string url, string kind)
    {
        var encoder = HtmlEncoder.Default;
        var shop = encoder.Encode(_configuration["Shop:Name"] ?? "Shop");
        var subject = _localizer[$"Email_{kind}Subject", _configuration["Shop:Name"] ?? "Shop"].Value;
        var heading = encoder.Encode(_localizer[$"Email_{kind}Heading", user.FullName].Value);
        var intro = encoder.Encode(_localizer[$"Email_{kind}Intro"].Value);
        var button = encoder.Encode(_localizer[$"Email_{kind}Button"].Value);
        var footnote = encoder.Encode(_localizer[$"Email_{kind}Footnote"].Value);
        var linkHelp = encoder.Encode(_localizer["Email_LinkHelp"].Value);
        var safeUrl = encoder.Encode(url);
        var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        var html = $"""
            <!DOCTYPE html>
            <html lang="{culture}">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{encoder.Encode(subject)}</title></head>
            <body style="margin:0;background:#f6f2fb;font-family:Arial,Helvetica,sans-serif;color:#28223a">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:24px 12px">
                <table role="presentation" width="520" cellpadding="0" cellspacing="0" style="max-width:520px;width:100%;background:#ffffff;border-radius:16px;overflow:hidden">
                  <tr><td style="background:#6544a3;color:#ffffff;padding:18px 24px;font-size:22px;font-weight:bold">{shop}</td></tr>
                  <tr><td style="padding:24px">
                    <h1 style="font-size:20px;margin:0 0 12px">{heading}</h1>
                    <p style="margin:0 0 20px;line-height:1.5">{intro}</p>
                    <p style="margin:0 0 20px"><a href="{safeUrl}" style="background:#6544a3;color:#ffffff;text-decoration:none;padding:12px 22px;border-radius:999px;display:inline-block;font-weight:bold">{button}</a></p>
                    <p style="margin:0 0 8px;font-size:13px;color:#6b657a">{linkHelp}</p>
                    <p style="margin:0 0 16px;font-size:13px;word-break:break-all"><a href="{safeUrl}">{safeUrl}</a></p>
                    <p style="margin:0;font-size:13px;color:#6b657a">{footnote}</p>
                  </td></tr>
                </table>
              </td></tr></table>
            </body>
            </html>
            """;

        return _sender.SendAsync(user.Email!, subject, html);
    }
}

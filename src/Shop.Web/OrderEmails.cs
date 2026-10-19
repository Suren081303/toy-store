using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Shop.Core.Admin;
using Shop.Core.Entities;
using Shop.Core.Localization;
using Shop.Core.Notifications;
using Shop.Core.Orders;
using Shop.Core.Payments;

namespace Shop.Web;

// The email as it will be sent.
public record RenderedEmail(string To, string Subject, string Html);

// Makes the text of the emails about an order. The email queue's sender asks for it (RenderAsync) at the moment of sending.
// Emails to the customer are in the language of the order (the language they were browsing in); emails to the shop are in Armenian.
public class OrderEmails
{
    private readonly IStringLocalizer<SharedResource> _l;
    private readonly IConfiguration _configuration;
    private readonly IStoreSettingsService _settings;
    private readonly PaymentOptions _payments;
    private readonly IOrderService _orders;
    private readonly IAdminReturnService _returns;

    public OrderEmails(
        IStringLocalizer<SharedResource> localizer,
        IConfiguration configuration,
        IStoreSettingsService settings,
        IOptions<PaymentOptions> payments,
        IOrderService orders,
        IAdminReturnService returns)
    {
        _l = localizer;
        _configuration = configuration;
        _settings = settings;
        _payments = payments.Value;
        _orders = orders;
        _returns = returns;
    }

    public async Task<RenderedEmail> RenderAsync(OutboxEmail item)
    {
        var order = await _orders.FindForSystemAsync(item.OrderNumber)
            ?? throw new InvalidOperationException($"Order {item.OrderNumber} (queued email {item.Id}) does not exist.");

        // Links in an email must be complete addresses, so the site's own address has to be configured.
        var baseUrl = (_configuration["Shop:BaseUrl"] ?? string.Empty).TrimEnd('/');
        if (baseUrl.Length == 0)
        {
            throw new InvalidOperationException("Shop:BaseUrl is not configured. Emails need it to build their links (for example https://www.example.am).");
        }

        var forShop = EmailKinds.IsForShop(item.Kind);
        var to = forShop ? _configuration["Shop:Email"] : order.Email;
        if (string.IsNullOrWhiteSpace(to))
        {
            throw new InvalidOperationException("Shop:Email is not configured, so emails to the shop cannot be sent.");
        }

        var culture = forShop ? Cultures.Default : order.Culture;
        var customerUrl = $"{baseUrl}/{order.Culture}/Checkout/Done?number={order.Number}&key={Uri.EscapeDataString(order.AccessKey)}";
        var adminOrderUrl = $"{baseUrl}/{Cultures.Default}/Admin/Orders/Details?number={order.Number}";
        var settings = await _settings.GetAsync();
        var returnDetail = item.ReturnId is null ? null : await _returns.GetAsync(item.ReturnId.Value);
        var shopName = _configuration["Shop:Name"] ?? "Shop";

        var (subject, html) = InCulture(culture, () =>
        {
            var money = order.Total.ToString("N0", new CultureInfo(culture)) + " \u058F";
            var button = _l["Order_EmailButton"].Value;
            var footnote = _l["Order_EmailFootnote"].Value;
            var note = string.IsNullOrWhiteSpace(item.Detail) ? null : item.Detail;

            switch (item.Kind)
            {
                case EmailKind.OrderConfirmation:
                    return (_l["Order_EmailSubject", order.Number, shopName].Value,
                        BuildHtml(order, settings, _l["Order_EmailHeading", order.CustomerName].Value, _l["Order_EmailIntro"].Value, customerUrl, button, footnote, culture));

                case EmailKind.ShopNewOrder:
                    return (_l["Order_NewOrderSubject", order.Number].Value,
                        BuildHtml(order, settings, _l["Order_NewOrderHeading", order.Number].Value, string.Empty, adminOrderUrl, _l["Email_OpenOrderAdmin"].Value, string.Empty, culture));

                case EmailKind.OrderPacked:
                    return (_l["Email_Packed_Subject", order.Number].Value,
                        BuildStatusHtml(culture, shopName, _l["Email_Packed_Heading"].Value, order.Number,
                            [_l["Email_Packed_Text", order.Number, order.DeliveryDate.ToString("d MMMM yyyy", new CultureInfo(culture)), order.DeliverySlot].Value],
                            null, customerUrl, button, footnote));

                case EmailKind.OrderDelivered:
                {
                    var deadline = ShopClock.ToShopTime(order.DeliveredAt ?? order.CreatedAt).AddDays(settings.ReturnWindowDays);
                    return (_l["Email_Delivered_Subject", order.Number].Value,
                        BuildStatusHtml(culture, shopName, _l["Email_Delivered_Heading"].Value, order.Number,
                            [_l["Email_Delivered_Text", deadline.ToString("d MMMM yyyy", new CultureInfo(culture))].Value],
                            null, customerUrl, button, footnote));
                }

                case EmailKind.OrderCancelled:
                    return (_l["Email_Cancelled_Subject", order.Number].Value,
                        BuildStatusHtml(culture, shopName, _l["Email_Cancelled_Heading"].Value, order.Number,
                            [item.Detail == "expired" ? _l["Email_Cancelled_Expired", order.Number].Value : _l["Email_Cancelled_Text", order.Number].Value],
                            item.Detail == "expired" ? null : note, customerUrl, button, footnote));

                case EmailKind.TransferReceived:
                    return (_l["Email_Transfer_Subject", order.Number].Value,
                        BuildStatusHtml(culture, shopName, _l["Email_Transfer_Heading"].Value, order.Number,
                            [_l["Email_Transfer_Text", order.Number].Value], null, customerUrl, button, footnote));

                case EmailKind.ReturnRejected:
                    return (_l["Email_ReturnRejected_Subject", order.Number].Value,
                        BuildStatusHtml(culture, shopName, _l["Email_ReturnRejected_Heading"].Value, order.Number,
                            [_l["Email_ReturnRejected_Text", order.Number].Value], note, customerUrl, button, footnote));

                case EmailKind.ReturnReceived:
                    return (_l["Email_ReturnReceived_Subject", order.Number].Value,
                        BuildStatusHtml(culture, shopName, _l["Email_ReturnReceived_Heading"].Value, order.Number,
                            [_l["Email_ReturnReceived_Text", order.Number].Value], note, customerUrl, button, footnote));

                case EmailKind.ReturnRefunded:
                    return (_l["Email_ReturnRefunded_Subject", order.Number].Value,
                        BuildStatusHtml(culture, shopName, _l["Email_ReturnRefunded_Heading"].Value, order.Number,
                            [_l["Email_ReturnRefunded_Text", order.Number].Value], note, customerUrl, button, footnote));

                case EmailKind.ShopReturnRequested:
                {
                    var returnUrl = $"{baseUrl}/{Cultures.Default}/Admin/Returns/Details/{item.ReturnId}";
                    return (_l["Email_ShopReturn_Subject", order.Number].Value,
                        BuildStatusHtml(culture, shopName, _l["Email_ShopReturn_Heading", order.Number].Value, order.Number,
                            [_l["Email_ShopReturn_Text", order.Number, order.CustomerName].Value, returnDetail?.Reason ?? string.Empty],
                            null, returnUrl, _l["Email_OpenReturnAdmin"].Value, string.Empty));
                }

                case EmailKind.ShopRefundNeeded:
                    return (_l["Email_ShopRefund_Subject", order.Number].Value,
                        BuildStatusHtml(culture, shopName, _l["Email_ShopRefund_Heading", order.Number].Value, order.Number,
                            [_l["Email_ShopRefund_Text", money].Value], null, adminOrderUrl, _l["Email_OpenOrderAdmin"].Value, string.Empty));

                default:
                    throw new InvalidOperationException($"No email text for the kind {item.Kind}.");
            }
        });

        return new RenderedEmail(to, subject, html);
    }

    // The texts are translated into the language of the email, whatever language the code that triggered it was running in.
    private static T InCulture<T>(string culture, Func<T> build)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);

        try
        {
            return build();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    // A short email: a heading, one or more paragraphs, an optional note, and a button. Used for status news and the shop's notices.
    private static string BuildStatusHtml(
        string culture, string shopName, string heading, int orderNumber, IEnumerable<string> paragraphs, string? note,
        string url, string buttonText, string footnote)
    {
        var encoder = HtmlEncoder.Default;
        var body = new StringBuilder();

        foreach (var paragraph in paragraphs.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            body.Append($"<p style=\"margin:0 0 14px;line-height:1.5;white-space:pre-line\">{encoder.Encode(paragraph)}</p>");
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            body.Append($"<p style=\"margin:0 0 14px;padding:10px 14px;background:#f6f2fb;border-radius:10px;line-height:1.5;white-space:pre-line\">{encoder.Encode(note)}</p>");
        }

        return $"""
            <!DOCTYPE html>
            <html lang="{culture}">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{encoder.Encode(heading)}</title></head>
            <body style="margin:0;background:#f6f2fb;font-family:Arial,Helvetica,sans-serif;color:#28223a">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:24px 12px">
                <table role="presentation" width="560" cellpadding="0" cellspacing="0" style="max-width:560px;width:100%;background:#ffffff;border-radius:16px;overflow:hidden">
                  <tr><td style="background:#6544a3;color:#ffffff;padding:18px 24px;font-size:22px;font-weight:bold">{encoder.Encode(shopName)}</td></tr>
                  <tr><td style="padding:24px">
                    <h1 style="font-size:20px;margin:0 0 8px">{encoder.Encode(heading)}</h1>
                    <p style="margin:0 0 16px;color:#6b657a">#{orderNumber}</p>
                    {body}
                    <p style="margin:20px 0"><a href="{encoder.Encode(url)}" style="background:#6544a3;color:#ffffff;text-decoration:none;padding:12px 22px;border-radius:999px;display:inline-block;font-weight:bold">{encoder.Encode(buttonText)}</a></p>
                    {(string.IsNullOrEmpty(footnote) ? string.Empty : $"<p style=\"margin:0;font-size:13px;color:#6b657a\">{encoder.Encode(footnote)}</p>")}
                  </td></tr>
                </table>
              </td></tr></table>
            </body>
            </html>
            """;
    }

    private string BuildHtml(OrderSummary order, StoreSettingsView settings, string heading, string intro, string orderUrl, string buttonText, string footnote, string cultureName)
    {
        var encoder = HtmlEncoder.Default;
        var culture = new CultureInfo(cultureName);
        string Money(decimal amount) => encoder.Encode(amount.ToString("N0", culture) + " ֏");

        var rows = new StringBuilder();
        foreach (var line in order.Lines)
        {
            rows.Append($"""
                <tr>
                  <td style="padding:6px 0;border-bottom:1px solid #eee8f4">{encoder.Encode(line.Name)}<br><span style="color:#6b657a;font-size:12px">{Money(line.UnitPrice)} × {line.Quantity}</span></td>
                  <td style="padding:6px 0;border-bottom:1px solid #eee8f4;text-align:right;white-space:nowrap">{Money(line.LineTotal)}</td>
                </tr>
                """);
        }

        string TotalRow(string label, string value, bool bold = false) =>
            $"""<tr><td style="padding:4px 0;{(bold ? "font-weight:bold;font-size:16px" : "color:#6b657a")}">{encoder.Encode(label)}</td><td style="padding:4px 0;text-align:right;{(bold ? "font-weight:bold;font-size:16px" : "")}">{value}</td></tr>""";

        var totals = new StringBuilder();
        totals.Append(TotalRow(_l["Cart_Subtotal"].Value, Money(order.Subtotal)));
        if (order.Discount > 0)
        {
            totals.Append(TotalRow(_l["Cart_Discounts"].Value, "−" + Money(order.Discount)));
        }

        totals.Append(TotalRow(_l["Cart_Delivery"].Value, order.DeliveryFee == 0 ? encoder.Encode(_l["Checkout_FreeDelivery"].Value) : Money(order.DeliveryFee)));
        totals.Append(TotalRow(_l["Cart_Total"].Value, Money(order.Total), bold: true));

        var address = $"{_l["Region_" + order.Region].Value}, {order.City}, {order.Street} {order.Building}" +
                      (string.IsNullOrWhiteSpace(order.Apartment) ? string.Empty : $", {_l["Label_Apartment"].Value} {order.Apartment}");

        var shop = encoder.Encode(_configuration["Shop:Name"] ?? "Shop");
        var safeUrl = encoder.Encode(orderUrl);

        return $"""
            <!DOCTYPE html>
            <html lang="{cultureName}">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{encoder.Encode(heading)}</title></head>
            <body style="margin:0;background:#f6f2fb;font-family:Arial,Helvetica,sans-serif;color:#28223a">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:24px 12px">
                <table role="presentation" width="560" cellpadding="0" cellspacing="0" style="max-width:560px;width:100%;background:#ffffff;border-radius:16px;overflow:hidden">
                  <tr><td style="background:#6544a3;color:#ffffff;padding:18px 24px;font-size:22px;font-weight:bold">{shop}</td></tr>
                  <tr><td style="padding:24px">
                    <h1 style="font-size:20px;margin:0 0 8px">{encoder.Encode(heading)}</h1>
                    <p style="margin:0 0 16px;color:#6b657a">{encoder.Encode(_l["Order_Number"].Value)} <b style="color:#28223a">#{order.Number}</b></p>
                    {(string.IsNullOrEmpty(intro) ? string.Empty : $"<p style=\"margin:0 0 16px;line-height:1.5\">{encoder.Encode(intro)}</p>")}
                    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="font-size:14px">{rows}</table>
                    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="font-size:14px;margin-top:10px">{totals}</table>
                    <p style="margin:18px 0 4px;font-weight:bold">{encoder.Encode(_l["Order_DeliveryTitle"].Value)}</p>
                    <p style="margin:0;font-size:14px;line-height:1.5">
                      {encoder.Encode(order.DeliveryDate.ToString("dddd, d MMMM yyyy", culture))}, {encoder.Encode(order.DeliverySlot)}<br>
                      {encoder.Encode(address)}<br>
                      {encoder.Encode(order.CustomerName)}, {encoder.Encode(order.Phone)}
                    </p>
                    <p style="margin:12px 0 0;font-size:14px"><b>{encoder.Encode(_l["Order_PaymentTitle"].Value)}:</b> {encoder.Encode(_l["Pay_" + order.PaymentMethod].Value)}</p>
                    {BankBlock(order, settings, culture, encoder)}
                    <p style="margin:20px 0"><a href="{safeUrl}" style="background:#6544a3;color:#ffffff;text-decoration:none;padding:12px 22px;border-radius:999px;display:inline-block;font-weight:bold">{encoder.Encode(buttonText)}</a></p>
                    {(string.IsNullOrEmpty(footnote) ? string.Empty : $"<p style=\"margin:0;font-size:13px;color:#6b657a\">{encoder.Encode(footnote)}</p>")}
                  </td></tr>
                </table>
              </td></tr></table>
            </body>
            </html>
            """;
    }

    // Bank details and the payment deadline, for a bank transfer order that is still waiting for the money.
    private string BankBlock(OrderSummary order, StoreSettingsView settings, CultureInfo culture, HtmlEncoder encoder)
    {
        if (order.PaymentMethod != PaymentMethod.BankTransfer || order.Status != OrderStatus.AwaitingPayment)
        {
            return string.Empty;
        }

        string Row(string label, string value) => $"{encoder.Encode(label)}: <b>{encoder.Encode(value)}</b><br>";

        var deadline = PaymentRules.GetDeadline(order.PaymentMethod, order.CreatedAt, _payments)!.Value;
        var deadlineText = ShopClock.ToShopTime(deadline).ToString("d MMM yyyy, HH:mm", culture);

        return $"""
            <div style="margin:14px 0;padding:12px 14px;background:#f6f2fb;border-radius:12px;font-size:14px;line-height:1.6">
              <b>{encoder.Encode(_l["Bank_Title"].Value)}</b><br>
              {Row(_l["Bank_Name"].Value, settings.BankName)}
              {Row(_l["Bank_Account"].Value, settings.BankAccount)}
              {Row(_l["Bank_Recipient"].Value, settings.BankRecipient)}
              {Row(_l["Bank_Amount"].Value, order.Total.ToString("N0", culture) + " ֏")}
              {Row(_l["Bank_Reference"].Value, _l["Bank_ReferenceValue", order.Number].Value)}
              <span style="color:#6b657a">{encoder.Encode(_l["Pay_Deadline", deadlineText].Value)}</span>
            </div>
            """;
    }
}

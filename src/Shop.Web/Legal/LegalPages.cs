using Microsoft.Extensions.Options;
using Shop.Core.Carts;
using Shop.Core.Catalog;
using Shop.Core.Orders;
using Shop.Core.Payments;

namespace Shop.Web.Legal;

// What the legal pages (delivery and returns, terms, privacy) say that depends on the shop: who it is, and the numbers
// the shop really uses (delivery fee, return days, payment time limits). The texts read these values, so they cannot
// disagree with what the site does when the admin changes a setting.
public record LegalViewModel(
    string ShopName,
    string LegalName,
    string? TaxId,
    string Address,
    string Phone,
    string Email,
    string Hours,
    decimal DeliveryFee,
    decimal FreeDeliveryFrom,
    int DeliveryDaysAhead,
    int ReturnDays,
    bool CardAvailable,
    int CardMinutes,
    int TransferDays,
    int GuestCartDays,
    int CustomerDiscountPercent);

public class LegalPages
{
    private readonly IConfiguration _configuration;
    private readonly IStoreSettingsService _settings;
    private readonly IPaymentService _payments;
    private readonly PaymentOptions _paymentOptions;
    private readonly CartOptions _cartOptions;

    public LegalPages(
        IConfiguration configuration, IStoreSettingsService settings, IPaymentService payments,
        IOptions<PaymentOptions> paymentOptions, IOptions<CartOptions> cartOptions)
    {
        _configuration = configuration;
        _settings = settings;
        _payments = payments;
        _paymentOptions = paymentOptions.Value;
        _cartOptions = cartOptions.Value;
    }

    public async Task<LegalViewModel> BuildAsync()
    {
        var settings = await _settings.GetAsync();
        var shopName = _configuration["Shop:Name"] ?? string.Empty;

        return new LegalViewModel(
            shopName,
            // The company or sole trader that sells; the shop's name until it is set.
            string.IsNullOrWhiteSpace(_configuration["Shop:LegalName"]) ? shopName : _configuration["Shop:LegalName"]!,
            string.IsNullOrWhiteSpace(_configuration["Shop:TaxId"]) ? null : _configuration["Shop:TaxId"],
            _configuration["Shop:Address"] ?? string.Empty,
            _configuration["Shop:Phone"] ?? string.Empty,
            _configuration["Shop:Email"] ?? string.Empty,
            _configuration["Shop:Hours"] ?? string.Empty,
            settings.DeliveryFee,
            settings.FreeDeliveryFrom,
            settings.DeliveryDaysAhead,
            settings.ReturnWindowDays,
            _payments.CardPaymentAvailable,
            _paymentOptions.CardTimeoutMinutes,
            _paymentOptions.BankTransferTimeoutDays,
            _cartOptions.GuestCartDays,
            ProductRules.CustomerDiscountPercent);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shop.Core.Admin;
using Shop.Core.Carts;
using Shop.Core.Catalog;
using Shop.Core.Email;
using Shop.Core.Favorites;
using Shop.Core.Notifications;
using Shop.Core.Orders;
using Shop.Core.Payments;
using Shop.Infrastructure.Admin;
using Shop.Infrastructure.Carts;
using Shop.Infrastructure.Catalog;
using Shop.Infrastructure.Data;
using Shop.Infrastructure.Email;
using Shop.Infrastructure.Favorites;
using Shop.Infrastructure.Notifications;
using Shop.Infrastructure.Orders;
using Shop.Infrastructure.Payments;

namespace Shop.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ShopDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("DefaultConnection")));

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<IFavoriteService, FavoriteService>();
        services.AddScoped<ICartService, CartService>();
        services.AddScoped<IStoreSettingsService, StoreSettingsService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IAdminOrderService, AdminOrderService>();
        services.AddScoped<IAdminReturnService, AdminReturnService>();
        services.AddScoped<IAdminSettingsService, AdminSettingsService>();
        services.AddScoped<IAdminCategoryService, AdminCategoryService>();
        services.AddScoped<IAdminAgeGroupService, AdminAgeGroupService>();
        services.AddScoped<IAdminProductService, AdminProductService>();
        services.AddScoped<IAdminProductToolsService, AdminProductToolsService>();
        services.AddScoped<IAdminAccountService, AdminAccountService>();
        services.AddScoped<IAdminCustomerService, AdminCustomerService>();
        services.AddScoped<IAdminAuditService, AdminAuditService>();
        services.AddScoped<IAdminAnalyticsService, AdminAnalyticsService>();
        services.AddScoped<IAdminNotificationService, AdminNotificationService>();
        services.AddScoped<IAdminEmailService, AdminEmailService>();
        services.AddScoped<INotificationCleanup, NotificationCleanup>();

        // Email:Provider "Smtp" sends through a mail server; the default writes emails to files (development).
        if (string.Equals(configuration["Email:Provider"], "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            services.AddScoped<IEmailSender, FileEmailSender>();
        }

        return services;
    }
}

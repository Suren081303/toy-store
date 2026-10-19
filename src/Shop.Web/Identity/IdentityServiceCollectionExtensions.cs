using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Shop.Core.Entities;
using Shop.Infrastructure.Data;

namespace Shop.Web.Identity;

public static class IdentityServiceCollectionExtensions
{
    public static IServiceCollection AddShopIdentity(
        this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                options.User.RequireUniqueEmail = true;

                // Length is checked here; letter + digit by LetterAndDigitPasswordValidator.
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);

                // The confirmed-email check is done in AccountController, after the password was verified,
                // so the sign-in page does not reveal which emails are registered.
                options.SignIn.RequireConfirmedEmail = false;

                options.Tokens.PasswordResetTokenProvider = PasswordResetTokenProvider<ApplicationUser>.ProviderName;
            })
            .AddEntityFrameworkStores<ShopDbContext>()
            .AddDefaultTokenProviders()
            .AddTokenProvider<PasswordResetTokenProvider<ApplicationUser>>(PasswordResetTokenProvider<ApplicationUser>.ProviderName)
            .AddErrorDescriber<LocalizedIdentityErrorDescriber>()
            .AddPasswordValidator<LetterAndDigitPasswordValidator>()
            .AddClaimsPrincipalFactory<ShopClaimsPrincipalFactory>();

        // Email-confirmation links: 24 hours (password reset links: 1 hour, see PasswordResetTokenProviderOptions).
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromHours(24));

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "Shop.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context => Redirect(context, "Login", StatusCodes.Status401Unauthorized);
            options.Events.OnRedirectToAccessDenied = context => Redirect(context, "AccessDenied", StatusCodes.Status403Forbidden);
        });

        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "Shop.Antiforgery";
            options.HeaderName = "RequestVerificationToken";
        });

        // Keys that sign the cookies and email links are stored in the database (DataProtectionKeys table).
        services.AddDataProtection()
            .SetApplicationName("Shop")
            .PersistKeysToDbContext<ShopDbContext>();

        services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicies.Account, context =>
            {
                var permitLimit = context.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value.AccountPermitLimit;

                return RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    });
            });
        });

        return services;
    }

    // Visitors go to the sign-in page in their own language and come back afterwards.
    // Script requests (Accept: application/json) get a plain 401/403 instead of a redirect.
    private static Task Redirect(RedirectContext<CookieAuthenticationOptions> context, string action, int scriptStatusCode)
    {
        if (context.Request.Headers.Accept.ToString().Contains("application/json"))
        {
            context.Response.StatusCode = scriptStatusCode;
            return Task.CompletedTask;
        }

        var culture = LanguageLinks.CultureOf(context.Request.Path);

        if (action == "Login")
        {
            var returnUrl = context.Request.Path + context.Request.QueryString;
            context.Response.Redirect($"/{culture}/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }
        else
        {
            context.Response.Redirect($"/{culture}/Account/{action}");
        }

        return Task.CompletedTask;
    }
}

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Localization.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Shop.Core.Admin;
using Shop.Core.Carts;
using Shop.Core.Catalog;
using Shop.Core.Email;
using Shop.Core.Entities;
using Shop.Core.Identity;
using Shop.Core.Notifications;
using Shop.Core.Payments;
using Shop.Infrastructure;
using Shop.Infrastructure.Data;
using Shop.Infrastructure.Identity;
using Shop.Web;
using Shop.Web.Background;
using Shop.Web.Hosting;
using Shop.Web.Identity;
using Shop.Web.Legal;
using Shop.Web.Notifications;
using Shop.Web.Payments;
using Shop.Web.Security;
using Shop.Web.Uploads;

var supportedCultures = new[] { "hy", "ru", "en" };

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services
    .AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
    .AddViewLocalization()
    .AddDataAnnotationsLocalization(options =>
        options.DataAnnotationLocalizerProvider = (type, factory) => factory.Create(typeof(SharedResource)));
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddShopIdentity(builder.Configuration, builder.Environment);
builder.Services.Configure<CatalogOptions>(builder.Configuration.GetSection(CatalogOptions.SectionName));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.SectionName));
builder.Services.Configure<EmailOptions>(options =>
{
    builder.Configuration.GetSection(EmailOptions.SectionName).Bind(options);

    if (string.IsNullOrEmpty(options.Folder))
    {
        options.Folder = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "emails");
    }
});
builder.Services.Configure<CartOptions>(builder.Configuration.GetSection(CartOptions.SectionName));
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection(PaymentOptions.SectionName));
builder.Services.AddHttpContextAccessor();

// Behind a reverse proxy or a hosting platform's front end: trust X-Forwarded-For and X-Forwarded-Proto, so the site sees the visitor's
// address (rate limiting) and https (redirects, secure cookies). Switch it on only when the site can be reached through the proxy alone,
// because anybody who can reach the site directly could then fake these headers.
if (builder.Configuration.GetValue<bool>("Hosting:ForwardedHeaders"))
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

// The fake bank exists only in Development, and only while Payments:Provider is "Fake". Without a provider, card payment is not offered.
if (builder.Environment.IsDevelopment()
    && builder.Configuration[$"{PaymentOptions.SectionName}:Provider"] == FakePaymentProvider.ProviderName)
{
    builder.Services.AddScoped<IPaymentProvider, FakePaymentProvider>();
}
builder.Services.AddScoped<LegalPages>();
builder.Services.AddScoped<AccountEmails>();
builder.Services.AddScoped<OrderEmails>();
builder.Services.AddScoped<IEmailOutboxProcessor, EmailOutboxProcessor>();
builder.Services.AddScoped<OrderAccess>();
builder.Services.AddScoped<Shop.Web.Areas.Admin.AdminRoleFilter>();
builder.Services.AddSingleton<IImageStore, ImageStore>();
builder.Services.AddScoped<FavoriteState>();
builder.Services.AddScoped<GuestCookies>();
builder.Services.AddScoped<CartOwnerResolver>();
builder.Services.AddScoped<CartState>();
builder.Services.AddHostedService<GuestCartCleanupService>();
builder.Services.AddHostedService<PaymentExpiryService>();
builder.Services.AddHostedService<EmailOutboxService>();
builder.Services.AddHostedService<NotificationCleanupService>();

// A failing background job is logged and stops, but does not take the whole site down.
builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture("hy");
    options.AddSupportedCultures(supportedCultures);
    options.AddSupportedUICultures(supportedCultures);
    options.RequestCultureProviders.Clear();

    // 1. The language in the URL (/hy, /ru, /en).
    options.RequestCultureProviders.Add(new RouteDataRequestCultureProvider { Options = options });

    // 2. On the 404 page the URL is /not-found and on the error page it is /error, so use the language of the address the visitor asked for.
    options.RequestCultureProviders.Add(new CustomRequestCultureProvider(context =>
    {
        var originalPath = context.Features.Get<IStatusCodeReExecuteFeature>()?.OriginalPath
            ?? context.Features.Get<IExceptionHandlerPathFeature>()?.Path;
        var first = originalPath?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (first is not null && supportedCultures.Contains(first))
        {
            return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(first));
        }

        return Task.FromResult<ProviderCultureResult?>(null);
    }));
});

var app = builder.Build();

// Production only: stop at once, with a clear message, when a setting needed for going live is missing or still a sample value.
if (!app.Environment.IsDevelopment())
{
    var checks = ProductionChecks.Run(app.Configuration);

    foreach (var warning in checks.Warnings)
    {
        app.Logger.LogWarning("Production check: {Warning}", warning);
    }

    if (checks.Errors.Count > 0)
    {
        throw new InvalidOperationException(
            "The site cannot start in Production until these settings are fixed:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", checks.Errors));
    }
}

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;

    // Development only: placeholder categories, age groups and products (skipped when data exists).
    if (app.Environment.IsDevelopment())
    {
        await DbSeeder.SeedAsync(services.GetRequiredService<ShopDbContext>(), TimeProvider.System);
    }

    // Every environment: the Admin and Customer roles, and the first admin when Admin:Email/Admin:Password are configured.
    await IdentitySeeder.SeedAsync(
        services.GetRequiredService<RoleManager<IdentityRole>>(),
        services.GetRequiredService<UserManager<ApplicationUser>>(),
        services.GetRequiredService<IOptions<AdminOptions>>().Value,
        app.Logger);

    // Every environment: default delivery settings and time slots, if they are missing.
    await StoreSettingsSeeder.SeedAsync(services.GetRequiredService<ShopDbContext>());
}

// Configure the HTTP request pipeline.
if (app.Configuration.GetValue<bool>("Hosting:ForwardedHeaders"))
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    // The error page is /error; it picks the language of the address that failed (see the culture providers above).
    app.UseExceptionHandler("/error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseSecurityHeaders(app.Configuration);

// Empty 404 responses (unknown product, missing page) show the friendly 404 page.
app.UseStatusCodePagesWithReExecute("/not-found");

app.UseHttpsRedirection();

// Photos that the admin uploads are added after the site was built, so (unlike the build-time assets below) they are read from disk.
// The browser must not guess a file's type: only JPEG files made by ImageStore are in there.
// With Uploads:Folder set, the photos are served from that folder (outside the deployed files) at the same address, /uploads.
if (!string.IsNullOrWhiteSpace(app.Configuration["Uploads:Folder"]))
{
    var uploadsFolder = ImageStore.UploadsRoot(app.Environment, app.Configuration);
    Directory.CreateDirectory(uploadsFolder);

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(uploadsFolder),
        RequestPath = "/uploads",
        OnPrepareResponse = context => context.Context.Response.Headers.XContentTypeOptions = "nosniff"
    });
}

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.XContentTypeOptions = "nosniff"
});

app.UseRouting();
app.UseRequestLocalization();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapGet("/", () => Results.Redirect("/hy"));

// The admin panel: /hy|ru|en/Admin/... (controllers in Areas/Admin, only for the Admin role).
app.MapAreaControllerRoute(
    name: "admin",
    areaName: "Admin",
    pattern: "{culture:regex(^(hy|ru|en)$)}/Admin/{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{culture:regex(^(hy|ru|en)$)}/{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

// Lets the integration tests start the site (WebApplicationFactory<Program>).
public partial class Program
{
}

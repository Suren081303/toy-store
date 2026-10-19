using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shop.Core.Entities;

namespace Shop.Infrastructure.Data;

// IDataProtectionKeyContext: the keys that sign cookies and email links are kept in the DataProtectionKeys table,
// so they survive restarts and server moves.
public class ShopDbContext : IdentityDbContext<ApplicationUser>, IDataProtectionKeyContext
{
    public ShopDbContext(DbContextOptions<ShopDbContext> options) : base(options)
    {
    }

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<CategoryTranslation> CategoryTranslations => Set<CategoryTranslation>();

    public DbSet<AgeGroup> AgeGroups => Set<AgeGroup>();

    public DbSet<AgeGroupTranslation> AgeGroupTranslations => Set<AgeGroupTranslation>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductTranslation> ProductTranslations => Set<ProductTranslation>();

    public DbSet<ProductImage> ProductImages => Set<ProductImage>();

    public DbSet<Favorite> Favorites => Set<Favorite>();

    public DbSet<Cart> Carts => Set<Cart>();

    public DbSet<CartItem> CartItems => Set<CartItem>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    public DbSet<OrderEvent> OrderEvents => Set<OrderEvent>();

    public DbSet<ReturnRequest> ReturnRequests => Set<ReturnRequest>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<AdminNotification> AdminNotifications => Set<AdminNotification>();

    public DbSet<OutboxEmail> OutboxEmails => Set<OutboxEmail>();

    public DbSet<StoreSettings> StoreSettings => Set<StoreSettings>();

    public DbSet<DeliverySlot> DeliverySlots => Set<DeliverySlot>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Order numbers: 10001, 10002, ...
        builder.HasSequence<int>("OrderNumbers").StartsAt(10001).IncrementsBy(1);

        builder.ApplyConfigurationsFromAssembly(typeof(ShopDbContext).Assembly);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Core.Entities;

namespace Shop.Infrastructure.Data.Configurations;

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.HasKey(o => o.Id);

        // The customer-facing number comes from the OrderNumbers sequence (defined in ShopDbContext).
        builder.Property(o => o.Number).UseSequence("OrderNumbers");
        builder.HasIndex(o => o.Number).IsUnique();
        builder.HasIndex(o => o.CheckoutId).IsUnique();

        builder.Property(o => o.AccessKey).HasMaxLength(32).IsRequired();
        builder.Property(o => o.UserId).HasMaxLength(450);

        builder.Property(o => o.CustomerName).HasMaxLength(100).IsRequired();
        builder.Property(o => o.Email).HasMaxLength(256).IsRequired();
        builder.Property(o => o.Phone).HasMaxLength(20).IsRequired();
        builder.Property(o => o.Region).HasMaxLength(30).IsRequired();
        builder.Property(o => o.City).HasMaxLength(100).IsRequired();
        builder.Property(o => o.Street).HasMaxLength(150).IsRequired();
        builder.Property(o => o.Building).HasMaxLength(30).IsRequired();
        builder.Property(o => o.Apartment).HasMaxLength(30);
        builder.Property(o => o.Comment).HasMaxLength(500);
        builder.Property(o => o.DeliverySlot).HasMaxLength(50).IsRequired();
        builder.Property(o => o.Culture).HasMaxLength(5).IsRequired();

        // Enums are stored as text, so the table is readable in the database.
        builder.Property(o => o.PaymentMethod).HasConversion<string>().HasMaxLength(30);
        builder.Property(o => o.PaymentStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(30);

        builder.Property(o => o.Subtotal).HasPrecision(18, 2);
        builder.Property(o => o.Discount).HasPrecision(18, 2);
        builder.Property(o => o.DeliveryFee).HasPrecision(18, 2);
        builder.Property(o => o.Total).HasPrecision(18, 2);

        builder.HasIndex(o => new { o.UserId, o.CreatedAt });
        builder.HasIndex(o => o.Status);

        // The analytics ask for the orders of a period.
        builder.HasIndex(o => o.CreatedAt);

        // Orders are kept when a customer account is deleted.
        builder.HasOne(o => o.User)
            .WithMany()
            .HasForeignKey(o => o.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(o => o.Items)
            .WithOne(i => i.Order)
            .HasForeignKey(i => i.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(o => o.Events)
            .WithOne(e => e.Order)
            .HasForeignKey(e => e.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(o => o.ReturnRequests)
            .WithOne(r => r.Order)
            .HasForeignKey(r => r.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Sku).HasMaxLength(50).IsRequired();
        builder.Property(i => i.Brand).HasMaxLength(100).IsRequired();
        builder.Property(i => i.Name).HasMaxLength(200).IsRequired();
        builder.Property(i => i.NameHy).HasMaxLength(200).IsRequired();
        builder.Property(i => i.RegularPrice).HasPrecision(18, 2);
        builder.Property(i => i.UnitPrice).HasPrecision(18, 2);

        builder.ToTable(t => t.HasCheckConstraint("CK_OrderItems_Quantity", "[Quantity] >= 1"));

        // A product that was ordered can never be deleted (it is archived instead).
        builder.HasOne(i => i.Product)
            .WithMany()
            .HasForeignKey(i => i.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => i.OrderId);
        builder.HasIndex(i => i.ProductId);
    }
}

public class OrderEventConfiguration : IEntityTypeConfiguration<OrderEvent>
{
    public void Configure(EntityTypeBuilder<OrderEvent> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.Note).HasMaxLength(500);
        builder.Property(e => e.ByUserId).HasMaxLength(450);
        builder.HasIndex(e => e.OrderId);
    }
}

public class ReturnRequestConfiguration : IEntityTypeConfiguration<ReturnRequest>
{
    public void Configure(EntityTypeBuilder<ReturnRequest> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Reason).HasMaxLength(1000).IsRequired();
        builder.Property(r => r.AdminNote).HasMaxLength(1000);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(30);
        builder.HasIndex(r => r.OrderId);
        builder.HasIndex(r => r.RefundedAt);
    }
}

public class StoreSettingsConfiguration : IEntityTypeConfiguration<StoreSettings>
{
    public void Configure(EntityTypeBuilder<StoreSettings> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.DeliveryFee).HasPrecision(18, 2);
        builder.Property(s => s.FreeDeliveryFrom).HasPrecision(18, 2);
        builder.Property(s => s.BankName).HasMaxLength(200);
        builder.Property(s => s.BankAccount).HasMaxLength(50);
        builder.Property(s => s.BankRecipient).HasMaxLength(200);

        // There is only ever one settings row.
        builder.ToTable(t => t.HasCheckConstraint("CK_StoreSettings_SingleRow", "[Id] = 1"));
    }
}

public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Provider).HasMaxLength(30).IsRequired();
        builder.Property(p => p.Reference).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Amount).HasPrecision(18, 2);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(30);

        // The provider finds the attempt by this value.
        builder.HasIndex(p => p.Reference).IsUnique();
        builder.HasIndex(p => p.OrderId);

        builder.HasOne(p => p.Order)
            .WithMany()
            .HasForeignKey(p => p.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.HasKey(a => a.Id);

        builder.Property(a => a.UserId).HasMaxLength(450);
        builder.Property(a => a.UserEmail).HasMaxLength(256).IsRequired();
        builder.Property(a => a.Action).HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(50).IsRequired();
        builder.Property(a => a.EntityId).HasMaxLength(50).IsRequired();
        builder.Property(a => a.Details).HasMaxLength(1000);

        // The log is read newest first.
        builder.HasIndex(a => a.At);
    }
}

public class DeliverySlotConfiguration : IEntityTypeConfiguration<DeliverySlot>
{
    public void Configure(EntityTypeBuilder<DeliverySlot> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Label).HasMaxLength(50).IsRequired();
    }
}

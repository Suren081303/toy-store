using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Core.Entities;

namespace Shop.Infrastructure.Data.Configurations;

public class AdminNotificationConfiguration : IEntityTypeConfiguration<AdminNotification>
{
    public void Configure(EntityTypeBuilder<AdminNotification> builder)
    {
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Type).HasConversion<string>().HasMaxLength(30);
        builder.Property(n => n.Amount).HasPrecision(18, 2);
        builder.Property(n => n.Name).HasMaxLength(200);
        builder.Property(n => n.ReadByUserId).HasMaxLength(450);

        // The bell asks for "unread, newest first" all the time.
        builder.HasIndex(n => new { n.IsRead, n.CreatedAt });
        builder.HasIndex(n => n.CreatedAt);
    }
}

public class OutboxEmailConfiguration : IEntityTypeConfiguration<OutboxEmail>
{
    public void Configure(EntityTypeBuilder<OutboxEmail> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Kind).HasConversion<string>().HasMaxLength(40);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.Detail).HasMaxLength(1000);
        builder.Property(e => e.ToAddress).HasMaxLength(256);
        builder.Property(e => e.LastError).HasMaxLength(1000);

        // The sender asks for "pending and due".
        builder.HasIndex(e => new { e.Status, e.NextAttemptAt });
        builder.HasIndex(e => e.OrderNumber);
    }
}

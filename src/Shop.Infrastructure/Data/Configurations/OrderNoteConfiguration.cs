using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Core.Entities;

namespace Shop.Infrastructure.Data.Configurations;

public class OrderNoteConfiguration : IEntityTypeConfiguration<OrderNote>
{
    public void Configure(EntityTypeBuilder<OrderNote> builder)
    {
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Text).HasMaxLength(1000).IsRequired();
        builder.Property(n => n.ByUserId).HasMaxLength(450);
        builder.Property(n => n.ByEmail).HasMaxLength(256).IsRequired();

        builder.HasOne(n => n.Order)
            .WithMany(o => o.Notes)
            .HasForeignKey(n => n.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(n => n.OrderId);
    }
}

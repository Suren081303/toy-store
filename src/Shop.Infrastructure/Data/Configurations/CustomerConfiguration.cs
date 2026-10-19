using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Core.Entities;

namespace Shop.Infrastructure.Data.Configurations;

// What an admin can set on a customer's account: blocked, and a personal discount.
public class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.PersonalDiscountUntil).HasColumnType("date");

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_AspNetUsers_PersonalDiscountPercent",
            "[PersonalDiscountPercent] IS NULL OR ([PersonalDiscountPercent] >= 1 AND [PersonalDiscountPercent] <= 50)"));
    }
}

public class CustomerMessageConfiguration : IEntityTypeConfiguration<CustomerMessage>
{
    public void Configure(EntityTypeBuilder<CustomerMessage> builder)
    {
        builder.HasKey(m => m.Id);

        builder.Property(m => m.UserId).HasMaxLength(450).IsRequired();
        builder.Property(m => m.Subject).HasMaxLength(150).IsRequired();
        builder.Property(m => m.Body).HasMaxLength(2000).IsRequired();
        builder.Property(m => m.Culture).HasMaxLength(5).IsRequired();
        builder.Property(m => m.SentByUserId).HasMaxLength(450);
        builder.Property(m => m.SentByEmail).HasMaxLength(256).IsRequired();

        // The messages go with the account if it is ever deleted.
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // "The last messages to this customer", and the 24-hour limit.
        builder.HasIndex(m => new { m.UserId, m.CreatedAt });
    }
}

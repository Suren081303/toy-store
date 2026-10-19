using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Core.Entities;

namespace Shop.Infrastructure.Data.Configurations;

public class AgeGroupConfiguration : IEntityTypeConfiguration<AgeGroup>
{
    public void Configure(EntityTypeBuilder<AgeGroup> builder)
    {
        builder.HasKey(a => a.Id);
    }
}

public class AgeGroupTranslationConfiguration : IEntityTypeConfiguration<AgeGroupTranslation>
{
    public void Configure(EntityTypeBuilder<AgeGroupTranslation> builder)
    {
        builder.HasKey(t => new { t.AgeGroupId, t.Culture });
        builder.Property(t => t.Culture).HasMaxLength(10);
        builder.Property(t => t.Name).HasMaxLength(100).IsRequired();
        builder.HasOne(t => t.AgeGroup)
            .WithMany(a => a.Translations)
            .HasForeignKey(t => t.AgeGroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

using CaeManager.Domain.DocumentosIa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ExtraccionIaCacheConfiguration : IEntityTypeConfiguration<ExtraccionIaCache>
{
    public void Configure(EntityTypeBuilder<ExtraccionIaCache> builder)
    {
        builder.ToTable("ExtraccionesIaCache");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.HashSha256).IsRequired().HasMaxLength(ExtraccionIaCache.LongitudHash);
        builder.Property(c => c.ExtraccionJson).IsRequired();

        builder.HasIndex(c => new { c.TenantId, c.HashSha256 }).IsUnique();
    }
}

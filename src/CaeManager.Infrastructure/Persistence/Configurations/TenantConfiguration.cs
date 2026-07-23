using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Nombre)
            .IsRequired()
            .HasMaxLength(Tenant.LongitudMaximaNombre);

        builder.Property(t => t.Estado)
            .IsRequired()
            .HasConversion<string>();

        // Sin HasQueryFilter: Tenant no pertenece a ningún tenant (ver
        // docs/MULTITENANCY.md § 4.1) y no tiene soft delete (ver Tenant.cs).
    }
}

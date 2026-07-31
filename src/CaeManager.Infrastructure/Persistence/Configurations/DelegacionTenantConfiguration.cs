using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class DelegacionTenantConfiguration : IEntityTypeConfiguration<DelegacionTenant>
{
    public void Configure(EntityTypeBuilder<DelegacionTenant> builder)
    {
        builder.ToTable("DelegacionesTenant");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.TenantConsultoraId).IsRequired();
        builder.Property(d => d.TenantClienteId).IsRequired();
        builder.Property(d => d.Activa).IsRequired();

        // Se guarda el nombre del enum, no su número: un valor intercalado en
        // el futuro no debe reinterpretar filas existentes — y aquí eso
        // convertiría una delegación comercial en una de soporte.
        builder.Property(d => d.Proposito).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(d => d.MotivoActivacion).HasMaxLength(500);

        // Un mismo par Consultora/Cliente no puede tener dos delegaciones
        // activas simultáneas — desactivar la anterior antes de crear otra.
        builder.HasIndex(d => new { d.TenantConsultoraId, d.TenantClienteId, d.Activa });

        // Sin HasQueryFilter: catálogo global de autorización, mismo
        // tratamiento que Tenant (ver ADR-004 § 5.3 y DelegacionTenant.cs).
    }
}

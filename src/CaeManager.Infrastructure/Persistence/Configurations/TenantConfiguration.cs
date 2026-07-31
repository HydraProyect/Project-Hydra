using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Persistence.Seed;
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

        // El tenant #1 (la organización actual, ver Etapa 2 de
        // PLAN-MIGRACION-MULTITENANT.md) — fecha fija, no DateTime.UtcNow,
        // para que la migración generada sea reproducible.
        builder.HasData(new
        {
            Id = TenantSeedData.IdPorDefecto,
            Nombre = TenantSeedData.NombrePorDefecto,
            Estado = EstadoTenant.Activo,
            CreadoEnUtc = new DateTime(2026, 7, 23, 0, 0, 0, DateTimeKind.Utc),
            // El tenant #1 es desde el que se opera Hydra, así que es también
            // el de plataforma: el único que puede recibir delegaciones de
            // soporte sobre los demás (ver Tenant.EsPlataforma). Se marca aquí
            // y no se deduce del Id, que es público y determinista.
            EsPlataforma = true,
        });
    }
}

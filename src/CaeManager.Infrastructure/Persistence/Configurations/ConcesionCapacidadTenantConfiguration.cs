using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ConcesionCapacidadTenantConfiguration : IEntityTypeConfiguration<ConcesionCapacidadTenant>
{
    public const string IndiceUnaVigentePorUsuarioYCapacidad =
        "IX_ConcesionesCapacidadTenant_UnaVigentePorUsuarioYCapacidad";

    public void Configure(EntityTypeBuilder<ConcesionCapacidadTenant> builder)
    {
        builder.ToTable("ConcesionesCapacidadTenant", t =>
        {
            t.HasCheckConstraint(
                "CK_ConcesionesCapacidadTenant_Capacidad",
                "\"Capacidad\" IN ('DerechosDelInteresado')");
            // Una revocación deja siempre quién y cuándo, o ninguna de las dos cosas.
            t.HasCheckConstraint(
                "CK_ConcesionesCapacidadTenant_RevocacionCoherente",
                "(\"RevocadaEnUtc\" IS NULL) = (\"RevocadaPorUsuarioId\" IS NULL)");
        });
        builder.HasKey(c => c.Id);

        builder.Property(c => c.UsuarioId).IsRequired();
        builder.Property(c => c.Capacidad).IsRequired().HasConversion<string>().HasMaxLength(40);
        builder.Property(c => c.ConcedidaPorUsuarioId).IsRequired();
        builder.Property(c => c.ConcedidaEnUtc).IsRequired();

        // Como mucho una concesión vigente por cuenta y capacidad en cada Tenant; las revocadas quedan como historial.
        builder.HasIndex(c => new { c.TenantId, c.UsuarioId, c.Capacidad })
            .IsUnique()
            .HasFilter("\"RevocadaEnUtc\" IS NULL")
            .HasDatabaseName(IndiceUnaVigentePorUsuarioYCapacidad);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

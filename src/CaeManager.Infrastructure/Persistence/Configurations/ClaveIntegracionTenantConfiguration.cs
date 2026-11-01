using CaeManager.Domain.ApiKeys;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ClaveIntegracionTenantConfiguration : IEntityTypeConfiguration<ClaveIntegracionTenant>
{
    public void Configure(EntityTypeBuilder<ClaveIntegracionTenant> builder)
    {
        builder.ToTable("ClavesIntegracionTenant");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Nombre).HasMaxLength(ClaveIntegracionTenant.LongitudMaximaNombre).IsRequired();
        builder.Property(c => c.SistemaConsumidor).HasMaxLength(ClaveIntegracionTenant.LongitudMaximaSistemaConsumidor).IsRequired();
        builder.Property(c => c.PrefijoVisible).HasMaxLength(ClaveIntegracionTenant.LongitudMaximaPrefijoVisible).IsRequired();
        builder.Property(c => c.HashClave).HasMaxLength(ClaveIntegracionTenant.LongitudHashClave).IsRequired();
        builder.Property<List<string>>("AlcancesConcedidos")
            .HasColumnName("Alcances")
            .HasColumnType("text[]")
            .IsRequired();
        builder.Ignore(c => c.Alcances);
        builder.Property(c => c.MotivoSuspension).HasMaxLength(ClaveIntegracionTenant.LongitudMaximaMotivoSuspension);

        // Búsqueda por hash al obtener un token: única y global, porque el Tenant se deriva de la
        // clave y no al revés.
        builder.HasIndex(c => c.HashClave).IsUnique();

        // Una clave se rota una sola vez: sin esto, una antecesora en convivencia acuñaría otra
        // sucesora de hasta 365 días. Por Tenant: un índice global dejaría a un Tenant bloquear la
        // rotación de otro declarando como antecesora una clave ajena.
        builder.HasIndex(c => new { c.TenantId, c.SucedeAClaveId }).IsUnique().HasFilter("\"SucedeAClaveId\" IS NOT NULL");

        builder.HasIndex(c => c.TenantId);
    }
}

using CaeManager.Domain.ApiKeys;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ClaveApiConfiguration : IEntityTypeConfiguration<ClaveApi>
{
    public void Configure(EntityTypeBuilder<ClaveApi> builder)
    {
        builder.ToTable("ClavesApi");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.NombreDescriptivo).HasMaxLength(200).IsRequired();
        builder.Property(c => c.PrefijoVisible).HasMaxLength(16).IsRequired();
        builder.Property(c => c.HashClave).HasMaxLength(64).IsRequired();
        // El DEFAULT solo existe para la ranura anterior de un relevo azul/verde, que inserta sin
        // conocer la columna (ver la migración AnadeCaducidadClavesApi); ClaveApi siempre la fija.
        builder.Property(c => c.CaducaEnUtc).IsRequired().HasDefaultValueSql("now() + interval '30 days'");
        builder.Ignore(c => c.MotivoEmision);

        // Búsqueda por hash en cada request autenticado: única y global (no
        // por tenant) porque el hash en sí ya identifica una clave concreta,
        // el tenant se deriva de ella, no al revés.
        builder.HasIndex(c => c.HashClave).IsUnique();

        builder.HasIndex(c => c.TenantId);
    }
}

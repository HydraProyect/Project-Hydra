using CaeManager.Domain.Archivos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ArchivoPendienteDeReferenciaConfiguration : IEntityTypeConfiguration<ArchivoPendienteDeReferencia>
{
    public void Configure(EntityTypeBuilder<ArchivoPendienteDeReferencia> builder)
    {
        builder.ToTable("ArchivosPendientesDeReferencia");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Clave).HasMaxLength(ArchivoPendienteDeReferencia.LongitudMaximaClave).IsRequired();
        // Una fila por archivo, y la que quita el alta confirmada se busca por su clave.
        builder.HasIndex(p => new { p.TenantId, p.Clave }).IsUnique();
        // El barrido recorre las más antiguas primero.
        builder.HasIndex(p => new { p.TenantId, p.GuardadoEnUtc });
    }
}

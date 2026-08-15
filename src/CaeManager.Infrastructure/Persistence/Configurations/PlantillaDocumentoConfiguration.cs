using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class PlantillaDocumentoConfiguration : IEntityTypeConfiguration<PlantillaDocumento>
{
    public void Configure(EntityTypeBuilder<PlantillaDocumento> builder)
    {
        builder.ToTable("PlantillasDocumento");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Origen).IsRequired().HasConversion<string>();
        builder.Property(p => p.Nombre).IsRequired().HasMaxLength(PlantillaDocumento.LongitudMaximaNombre);
        builder.Property(p => p.Descripcion).HasMaxLength(PlantillaDocumento.LongitudMaximaDescripcion);
        builder.Property(p => p.AmbitoAplicacion).IsRequired().HasConversion<string>();
        builder.Property(p => p.CodigoFormato).HasMaxLength(PlantillaDocumento.LongitudMaximaCodigoFormato);
        builder.Property(p => p.FormatoOrigen).IsRequired().HasConversion<string>();
        builder.Property(p => p.Estado).IsRequired().HasConversion<string>();

        builder.HasIndex(p => new { p.TenantId, p.CentroId });
        builder.HasIndex(p => new { p.TenantId, p.ClienteId });

        // Restrict: la versión activa siempre pertenece a la misma plantilla,
        // y no hay ningún flujo en este MVP que borre una PlantillaDocumentoVersion.
        builder.HasOne<PlantillaDocumentoVersion>()
            .WithMany()
            .HasForeignKey(p => p.VersionActualId)
            .OnDelete(DeleteBehavior.Restrict);

        // Mismo criterio que Documento → TipoDocumento (DocumentoConfiguration).
        builder.HasOne<TipoDocumento>()
            .WithMany()
            .HasForeignKey(p => new { p.TenantId, p.TipoDocumentoId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Prerequisito de la FK que DocumentoGenerado declara hacia PlantillaDocumento (vía versión).
        builder.HasIndex(p => new { p.TenantId, p.Id }).IsUnique();

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

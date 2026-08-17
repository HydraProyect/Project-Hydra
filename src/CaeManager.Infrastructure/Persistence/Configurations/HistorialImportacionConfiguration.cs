using CaeManager.Domain.Importacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class HistorialImportacionConfiguration : IEntityTypeConfiguration<HistorialImportacion>
{
    public void Configure(EntityTypeBuilder<HistorialImportacion> builder)
    {
        builder.ToTable("HistorialImportaciones");
        builder.HasKey(h => h.Id);

        builder.Property(h => h.Plantilla).IsRequired().HasMaxLength(50);
        builder.Property(h => h.NombreArchivo).IsRequired().HasMaxLength(260);
        builder.Property(h => h.MensajeError).HasMaxLength(500);

        builder.HasIndex(h => new { h.TenantId, h.EjecutadaEnUtc });
    }
}

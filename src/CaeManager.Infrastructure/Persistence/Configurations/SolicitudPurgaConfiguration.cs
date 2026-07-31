using CaeManager.Domain.Retencion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SolicitudPurgaConfiguration : IEntityTypeConfiguration<SolicitudPurga>
{
    public void Configure(EntityTypeBuilder<SolicitudPurga> builder)
    {
        builder.ToTable("SolicitudesPurga");
        builder.HasKey(s => s.Id);

        // Nombre y no número, mismo criterio que el resto de enums
        // persistidos: un valor intercalado no debe reinterpretar filas
        // existentes, y aquí eso cambiaría el estado de una purga.
        builder.Property(s => s.TipoDato).IsRequired().HasConversion<string>().HasMaxLength(40);
        builder.Property(s => s.Estado).IsRequired().HasConversion<string>().HasMaxLength(30);

        builder.Property(s => s.RegistrosAfectados).IsRequired();
        builder.Property(s => s.FechaCorte).IsRequired();
        builder.Property(s => s.DetectadaEnUtc).IsRequired();
        builder.Property(s => s.Motivo).HasMaxLength(SolicitudPurga.LongitudMaximaMotivo);

        // La consulta que importa: qué hay pendiente de revisar por categoría.
        builder.HasIndex(s => new { s.TipoDato, s.Estado });

        // El filtro global de tenant lo aplica CaeManagerDbContext.
    }
}

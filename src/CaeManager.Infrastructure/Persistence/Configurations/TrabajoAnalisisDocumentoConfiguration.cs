using CaeManager.Domain.DocumentosIa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class TrabajoAnalisisDocumentoConfiguration : IEntityTypeConfiguration<TrabajoAnalisisDocumento>
{
    public void Configure(EntityTypeBuilder<TrabajoAnalisisDocumento> builder)
    {
        builder.ToTable("TrabajosAnalisisDocumento");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Tipo).IsRequired().HasConversion<string>();
        builder.Property(t => t.Estado).IsRequired().HasConversion<string>();
        builder.Property(t => t.UltimoError).HasMaxLength(TrabajoAnalisisDocumento.LongitudMaximaError);

        // El procesador pide "el pendiente más antiguo de este tenant" en
        // cada ciclo de sondeo — sin este índice sería un scan completo de la
        // tabla en cuanto acumule historial de trabajos ya completados.
        builder.HasIndex(t => new { t.TenantId, t.Estado, t.CreadoEnUtc });
    }
}

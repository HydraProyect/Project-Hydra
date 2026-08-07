using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AdjuntoMensajeConfiguration : IEntityTypeConfiguration<AdjuntoMensaje>
{
    public void Configure(EntityTypeBuilder<AdjuntoMensaje> builder)
    {
        builder.ToTable("AdjuntosMensaje");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.NombreArchivo).IsRequired().HasMaxLength(AdjuntoMensaje.LongitudMaximaNombreArchivo);
        builder.Property(a => a.TipoContenido).IsRequired().HasMaxLength(AdjuntoMensaje.LongitudMaximaTipoContenido);
        builder.Property(a => a.ArchivoUrl).IsRequired().HasMaxLength(AdjuntoMensaje.LongitudMaximaArchivoUrl);

        builder.HasIndex(a => a.MensajeId);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AdjuntoMensajeCorreoConfiguration : IEntityTypeConfiguration<AdjuntoMensajeCorreo>
{
    public void Configure(EntityTypeBuilder<AdjuntoMensajeCorreo> builder)
    {
        builder.ToTable("AdjuntosMensajeCorreo");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.NombreArchivo).IsRequired().HasMaxLength(AdjuntoMensajeCorreo.LongitudMaximaNombreArchivo);
        builder.Property(a => a.TipoContenido).IsRequired().HasMaxLength(AdjuntoMensajeCorreo.LongitudMaximaTipoContenido);
        builder.Property(a => a.ArchivoUrl).IsRequired().HasMaxLength(AdjuntoMensajeCorreo.LongitudMaximaArchivoUrl);

        builder.HasIndex(a => a.MensajeCorreoId);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

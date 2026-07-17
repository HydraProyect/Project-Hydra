using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class DocumentoConfiguration : IEntityTypeConfiguration<Documento>
{
    public void Configure(EntityTypeBuilder<Documento> builder)
    {
        builder.ToTable("Documentos");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.ArchivoUrl).HasMaxLength(Documento.LongitudMaximaArchivoUrl);
        builder.Property(d => d.Comentarios).HasMaxLength(Documento.LongitudMaximaComentarios);

        builder.HasIndex(d => new { d.TrabajadorId, d.TipoDocumentoId });
        builder.HasIndex(d => d.FechaVencimiento);

        builder.HasQueryFilter(d => !d.EstaEliminado);
    }
}

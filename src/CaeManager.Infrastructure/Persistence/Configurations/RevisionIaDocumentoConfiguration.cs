using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class RevisionIaDocumentoConfiguration : IEntityTypeConfiguration<RevisionIaDocumento>
{
    public void Configure(EntityTypeBuilder<RevisionIaDocumento> builder)
    {
        builder.ToTable("RevisionesIaDocumento");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.TipoDetectado).HasMaxLength(RevisionIaDocumento.LongitudMaximaTipoDetectado);
        builder.Property(r => r.Motivo).IsRequired().HasMaxLength(RevisionIaDocumento.LongitudMaximaMotivo);

        builder.HasIndex(r => new { r.DocumentoId, r.Resuelta });
    }
}

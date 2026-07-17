using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class TipoDocumentoCentroConfiguration : IEntityTypeConfiguration<TipoDocumentoCentro>
{
    public void Configure(EntityTypeBuilder<TipoDocumentoCentro> builder)
    {
        builder.ToTable("TiposDocumentoCentros");
        builder.HasKey(tc => tc.Id);

        builder.HasIndex(tc => new { tc.TipoDocumentoId, tc.CentroId }).IsUnique();
        builder.HasIndex(tc => tc.CentroId);
    }
}

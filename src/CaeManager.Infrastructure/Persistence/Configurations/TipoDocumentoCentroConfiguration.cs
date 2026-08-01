using CaeManager.Domain.Centros;
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

        builder.HasIndex(tc => new { tc.TenantId, tc.TipoDocumentoId, tc.CentroId }).IsUnique();
        builder.HasIndex(tc => tc.CentroId);

        // FKs reales — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasOne<TipoDocumento>().WithMany()
            .HasForeignKey(tc => new { tc.TenantId, tc.TipoDocumentoId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(tc => new { tc.TenantId, tc.CentroId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

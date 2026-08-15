using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class FirmaEnCampoDocumentoConfiguration : IEntityTypeConfiguration<FirmaEnCampoDocumento>
{
    public void Configure(EntityTypeBuilder<FirmaEnCampoDocumento> builder)
    {
        builder.ToTable("FirmasEnCampoDocumento");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.FirmanteNombre).IsRequired().HasMaxLength(FirmaEnCampoDocumento.LongitudMaximaFirmanteNombre);
        builder.Property(f => f.FirmanteRol).IsRequired().HasMaxLength(FirmaEnCampoDocumento.LongitudMaximaFirmanteRol);
        builder.Property(f => f.Ubicacion).HasMaxLength(FirmaEnCampoDocumento.LongitudMaximaUbicacion);
        builder.Property(f => f.HashSha256Pdf).IsRequired().HasMaxLength(FirmaEnCampoDocumento.LongitudHashSha256);

        builder.HasOne<Documento>().WithMany().HasForeignKey(f => f.DocumentoId).OnDelete(DeleteBehavior.Cascade);

        // El acceso es siempre "las firmas en campo de este documento".
        builder.HasIndex(f => new { f.TenantId, f.DocumentoId });
    }
}

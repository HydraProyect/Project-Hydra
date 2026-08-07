using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class FirmaDigitalDocumentoConfiguration : IEntityTypeConfiguration<FirmaDigitalDocumento>
{
    public void Configure(EntityTypeBuilder<FirmaDigitalDocumento> builder)
    {
        builder.ToTable("FirmasDigitalesDocumento");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Estado).IsRequired().HasConversion<string>();
        builder.Property(f => f.Revocacion).IsRequired().HasConversion<string>();
        builder.Property(f => f.EmisorCertificado).IsRequired().HasMaxLength(FirmaDigitalDocumento.LongitudMaximaEmisor);
        builder.Property(f => f.NumeroSerieCertificado).IsRequired().HasMaxLength(FirmaDigitalDocumento.LongitudMaximaNumeroSerie);
        builder.Property(f => f.FirmanteNombre).HasMaxLength(FirmaDigitalDocumento.LongitudMaximaFirmante);
        builder.Property(f => f.FirmanteNif).HasMaxLength(FirmaDigitalDocumento.LongitudMaximaNif);
        builder.Property(f => f.MotivoInvalidez).HasMaxLength(FirmaDigitalDocumento.LongitudMaximaMotivo);

        builder.HasOne<Documento>().WithMany().HasForeignKey(f => f.DocumentoId).OnDelete(DeleteBehavior.Cascade);

        // El acceso es siempre "las firmas de este documento" (detalle del
        // Documento y delete-and-recreate al renovar).
        builder.HasIndex(f => new { f.TenantId, f.DocumentoId });
    }
}

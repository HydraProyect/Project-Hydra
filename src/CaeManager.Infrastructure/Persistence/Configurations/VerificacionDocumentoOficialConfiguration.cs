using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class VerificacionDocumentoOficialConfiguration : IEntityTypeConfiguration<VerificacionDocumentoOficial>
{
    public void Configure(EntityTypeBuilder<VerificacionDocumentoOficial> builder)
    {
        builder.ToTable("VerificacionesDocumentoOficial");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Perfil).IsRequired().HasConversion<string>();
        builder.Property(v => v.NivelConfianza).IsRequired().HasConversion<string>();
        builder.Property(v => v.ResultadoCotejo).IsRequired().HasConversion<string>();
        builder.Property(v => v.Decision).IsRequired().HasConversion<string>();
        builder.Property(v => v.CodigoVerificacion).HasMaxLength(VerificacionDocumentoOficial.LongitudMaximaCodigoVerificacion);
        builder.Property(v => v.CifDetectado).HasMaxLength(VerificacionDocumentoOficial.LongitudMaximaCif);
        builder.Property(v => v.RazonSocialDetectada).HasMaxLength(VerificacionDocumentoOficial.LongitudMaximaRazonSocial);
        builder.Property(v => v.PeriodoDetectado).HasMaxLength(VerificacionDocumentoOficial.LongitudMaximaPeriodo);
        builder.Property(v => v.Motivos).IsRequired().HasMaxLength(VerificacionDocumentoOficial.LongitudMaximaMotivos);

        builder.HasOne<Documento>().WithMany().HasForeignKey(v => v.DocumentoId).OnDelete(DeleteBehavior.Cascade);

        // 0..1 por Documento: única con TenantId primero, como el resto de
        // índices únicos compuestos del multi-tenant (docs/MULTITENANCY.md).
        builder.HasIndex(v => new { v.TenantId, v.DocumentoId }).IsUnique();
    }
}

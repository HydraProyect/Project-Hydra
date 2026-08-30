using CaeManager.Domain.DocumentosIa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AuditoriaExtraccionIaConfiguration : IEntityTypeConfiguration<AuditoriaExtraccionIa>
{
    public void Configure(EntityTypeBuilder<AuditoriaExtraccionIa> builder)
    {
        builder.ToTable("AuditoriasExtraccionIa");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.HashSha256).IsRequired().HasMaxLength(AuditoriaExtraccionIa.LongitudHash);
        builder.Property(a => a.TipoEsperado).IsRequired().HasMaxLength(AuditoriaExtraccionIa.LongitudMaximaTipoEsperado);
        builder.Property(a => a.ProveedorCodigo).IsRequired().HasMaxLength(AuditoriaExtraccionIa.LongitudMaximaProveedorCodigo);
        builder.Property(a => a.CosteEstimadoOcr).HasPrecision(10, 6);
        builder.Property(a => a.CosteEstimado).HasPrecision(10, 6);
        builder.Property(a => a.Incidencias).HasMaxLength(AuditoriaExtraccionIa.LongitudMaximaIncidencias);
        builder.Property(a => a.VersionPipeline).IsRequired().HasMaxLength(ExtraccionIaCache.LongitudMaximaVersionPipeline);
        builder.Property(a => a.ModeloExacto).HasMaxLength(AuditoriaExtraccionIa.LongitudMaximaModeloExacto);
        builder.Property(a => a.RequestId).HasMaxLength(AuditoriaExtraccionIa.LongitudMaximaRequestId);
        builder.Property(a => a.ProveedoresInvocados).HasMaxLength(AuditoriaExtraccionIa.LongitudMaximaProveedoresInvocados);

        builder.HasIndex(a => new { a.TenantId, a.CreadaEnUtc });
        builder.HasIndex(a => a.DocumentoId);
    }
}

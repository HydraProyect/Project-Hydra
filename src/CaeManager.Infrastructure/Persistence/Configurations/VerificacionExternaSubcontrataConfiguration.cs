using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class VerificacionExternaSubcontrataConfiguration : IEntityTypeConfiguration<VerificacionExternaSubcontrata>
{
    public void Configure(EntityTypeBuilder<VerificacionExternaSubcontrata> builder)
    {
        builder.ToTable("VerificacionesExternaSubcontrata");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Observaciones)
            .HasMaxLength(VerificacionExternaSubcontrata.LongitudMaximaObservaciones);

        builder.Property(v => v.EvidenciaNombreArchivo)
            .HasMaxLength(VerificacionExternaSubcontrata.LongitudMaximaNombreArchivo);

        // La consulta central de supervisión pide "la última verificación de
        // cada (subcontrata, centro, tipo)" — índice exacto de esa pregunta.
        builder.HasIndex(v => new { v.TenantId, v.SubcontrataId, v.CentroId, v.TipoDocumentoId });

        // FKs compuestas con TenantId — ver P0-1 de docs/business/MATURITY_REVIEW.md:
        // un Id ajeno de otro tenant no puede referenciarse ni por accidente.
        // F3b-Subcontrata — SubcontrataId repunta contra Empresas; CASCADE se
        // conserva tal cual (asimetría ya existente frente al resto de F3b,
        // que usa RESTRICT — ver f3b-subcontrata-inventario-fresco-2026-08-26.md).
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(v => new { v.TenantId, v.SubcontrataId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(v => new { v.TenantId, v.CentroId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TipoDocumento>().WithMany()
            .HasForeignKey(v => new { v.TenantId, v.TipoDocumentoId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

using CaeManager.Domain.Documentos;
using CaeManager.Domain.Retencion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class DocumentoPropuestoPurgaConfiguration : IEntityTypeConfiguration<DocumentoPropuestoPurga>
{
    public void Configure(EntityTypeBuilder<DocumentoPropuestoPurga> builder)
    {
        builder.ToTable("DocumentosPropuestosPurga");
        builder.HasKey(p => p.Id);

        // Sin navegación, como IncidenciaPurgaConfiguration: el fixup del grafo fijaría el TenantId antes del sellado.
        // Las dos FK compuestas con TenantId impiden que una propuesta nombre una solicitud o un Documento de otro
        // Tenant propietario.
        builder.HasOne<SolicitudPurga>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.SolicitudPurgaId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Documento>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.DocumentoId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => new { p.TenantId, p.SolicitudPurgaId, p.DocumentoId }).IsUnique();
        builder.HasIndex(p => new { p.TenantId, p.DocumentoId });
    }
}

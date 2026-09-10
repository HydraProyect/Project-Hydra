using CaeManager.Domain.Retencion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class IncidenciaPurgaConfiguration : IEntityTypeConfiguration<IncidenciaPurga>
{
    public void Configure(EntityTypeBuilder<IncidenciaPurga> builder)
    {
        builder.ToTable("IncidenciasPurga");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Tipo).IsRequired().HasConversion<string>().HasMaxLength(40);
        builder.Property(i => i.Detalle).IsRequired().HasMaxLength(IncidenciaPurga.LongitudMaximaDetalle);
        builder.Property(i => i.DetectadaEnUtc).IsRequired();

        // Sin navegación a propósito, mismo motivo que
        // ExtraccionIaCacheDocumentoConfiguration: con navegación real el
        // fixup del grafo del padre fija el TenantId del hijo antes de que
        // TenantSelladoInterceptor lo selle.
        builder.HasOne<SolicitudPurga>().WithMany()
            .HasForeignKey(i => new { i.TenantId, i.SolicitudPurgaId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // ObjetivoId es polimórfico (Documento o Trabajador según
        // SolicitudPurga.TipoDato) — sin FK a propósito, ver el comentario de
        // IncidenciaPurga.ObjetivoId.
        builder.HasIndex(i => new { i.TenantId, i.SolicitudPurgaId });
    }
}

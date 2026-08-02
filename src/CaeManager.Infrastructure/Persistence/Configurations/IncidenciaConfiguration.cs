using CaeManager.Domain.Centros;
using CaeManager.Domain.Incidencias;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class IncidenciaConfiguration : IEntityTypeConfiguration<Incidencia>
{
    public void Configure(EntityTypeBuilder<Incidencia> builder)
    {
        builder.ToTable("Incidencias");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Tipo).IsRequired().HasConversion<string>();
        builder.Property(i => i.Gravedad).IsRequired().HasConversion<string>();
        builder.Property(i => i.Descripcion).IsRequired().HasMaxLength(Incidencia.LongitudMaximaDescripcion);

        builder.HasIndex(i => i.CentroId);
        builder.HasIndex(i => i.TrabajadorId);

        // FKs reales — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(i => new { i.TenantId, i.CentroId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Trabajador>().WithMany()
            .HasForeignKey(i => new { i.TenantId, i.TrabajadorId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

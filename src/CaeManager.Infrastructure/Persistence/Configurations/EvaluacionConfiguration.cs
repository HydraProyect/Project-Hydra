using CaeManager.Domain.Centros;
using CaeManager.Domain.Evaluaciones;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class EvaluacionConfiguration : IEntityTypeConfiguration<Evaluacion>
{
    public void Configure(EntityTypeBuilder<Evaluacion> builder)
    {
        builder.ToTable("Evaluaciones");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Observaciones).HasMaxLength(Evaluacion.LongitudMaximaObservaciones);

        builder.HasIndex(e => e.CentroId);
        builder.HasIndex(e => e.TrabajadorId);

        // FKs reales — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.CentroId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Trabajador>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.TrabajadorId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AsignacionConfiguration : IEntityTypeConfiguration<Asignacion>
{
    public void Configure(EntityTypeBuilder<Asignacion> builder)
    {
        builder.ToTable("Asignaciones");
        builder.HasKey(a => a.Id);

        builder.HasIndex(a => new { a.TenantId, a.TrabajadorId, a.CentroId, a.FechaAlta }).IsUnique();
        builder.HasIndex(a => a.CentroId);

        // FKs reales — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasOne<Trabajador>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.TrabajadorId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.CentroId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

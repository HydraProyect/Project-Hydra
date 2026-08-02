using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ProyectoTecnicoConfiguration : IEntityTypeConfiguration<ProyectoTecnico>
{
    public void Configure(EntityTypeBuilder<ProyectoTecnico> builder)
    {
        builder.ToTable("ProyectosTecnicos");
        builder.HasKey(pt => pt.Id);

        builder.HasIndex(pt => new { pt.TenantId, pt.ProyectoId, pt.TrabajadorId, pt.FechaAlta }).IsUnique();
        builder.HasIndex(pt => pt.TrabajadorId);

        // FKs reales — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasOne<Proyecto>().WithMany()
            .HasForeignKey(pt => new { pt.TenantId, pt.ProyectoId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Trabajador>().WithMany()
            .HasForeignKey(pt => new { pt.TenantId, pt.TrabajadorId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

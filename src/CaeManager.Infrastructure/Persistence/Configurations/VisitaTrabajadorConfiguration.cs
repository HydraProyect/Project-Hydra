using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class VisitaTrabajadorConfiguration : IEntityTypeConfiguration<VisitaTrabajador>
{
    public void Configure(EntityTypeBuilder<VisitaTrabajador> builder)
    {
        builder.ToTable("VisitasTrabajadores");
        builder.HasKey(vt => vt.Id);

        builder.HasIndex(vt => vt.VisitaId);
        builder.HasIndex(vt => vt.TrabajadorId);
        builder.HasIndex(vt => new { vt.TenantId, vt.VisitaId, vt.TrabajadorId }).IsUnique();

        // FKs reales — ver P0-1 de docs/business/MATURITY_REVIEW.md. Restrict
        // en ambos extremos, igual que el resto del modelo: el borrado real
        // en este dominio es soft-delete (EstaEliminado), así que un DELETE
        // SQL nunca debería llegar aquí sin pasar antes por código que ya
        // limpia la unión explícitamente (ver EditarVisitaCommandHandler).
        builder.HasOne<Visita>().WithMany()
            .HasForeignKey(vt => new { vt.TenantId, vt.VisitaId })
            .HasPrincipalKey(v => new { v.TenantId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Trabajador>().WithMany()
            .HasForeignKey(vt => new { vt.TenantId, vt.TrabajadorId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

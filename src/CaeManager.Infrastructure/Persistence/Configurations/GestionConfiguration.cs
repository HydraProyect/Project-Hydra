using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class GestionConfiguration : IEntityTypeConfiguration<Gestion>
{
    public void Configure(EntityTypeBuilder<Gestion> builder)
    {
        builder.ToTable("Gestiones");
        builder.HasKey(g => g.Id);

        builder.Property(g => g.Notas).HasMaxLength(Gestion.LongitudMaximaNotas);

        builder.HasIndex(g => g.TrabajadorId);
        builder.HasIndex(g => g.CentroId);

        // FK reales — mismo criterio que RequisitoDocumentalConfiguration (P0-1 de docs/business/MATURITY_REVIEW.md).
        builder.HasOne<Trabajador>().WithMany()
            .HasForeignKey(g => new { g.TenantId, g.TrabajadorId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(g => new { g.TenantId, g.CentroId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TipoDocumento>().WithMany()
            .HasForeignKey(g => new { g.TenantId, g.TipoDocumentoId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

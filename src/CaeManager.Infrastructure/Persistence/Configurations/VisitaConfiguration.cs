using CaeManager.Domain.Centros;
using CaeManager.Domain.Visitas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class VisitaConfiguration : IEntityTypeConfiguration<Visita>
{
    public void Configure(EntityTypeBuilder<Visita> builder)
    {
        builder.ToTable("Visitas");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Notas).HasMaxLength(Visita.LongitudMaximaNotas);

        // Sin navigation property hacia Centro a propósito: cada agregado se
        // consulta por su propio repositorio/query (ver ARCHITECTURE.md). La
        // FK sí se declara — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasIndex(v => v.CentroId);
        builder.HasIndex(v => v.FechaFin);

        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(v => new { v.TenantId, v.CentroId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Prerequisito de la FK que VisitaTrabajador declara hacia Visita.
        builder.HasIndex(v => new { v.TenantId, v.Id }).IsUnique();

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

using CaeManager.Domain.Proyectos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ProyectoConfiguration : IEntityTypeConfiguration<Proyecto>
{
    public void Configure(EntityTypeBuilder<Proyecto> builder)
    {
        builder.ToTable("Proyectos");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Nombre).HasMaxLength(Proyecto.LongitudMaximaNombre).IsRequired();
        builder.Property(p => p.Notas).HasMaxLength(Proyecto.LongitudMaximaNotas);

        // Nombre único por (tenant, cliente) — no se puede repetir aunque el
        // proyecto esté en otro centro del mismo cliente, ni tras cerrarse
        // (solo se libera si se elimina, ver DATABASE.md/ROADMAP.md).
        builder.HasIndex(p => new { p.TenantId, p.ClienteId, p.Nombre })
               .IsUnique()
               .HasFilter($"NOT \"{nameof(Proyecto.EstaEliminado)}\"");

        builder.HasIndex(p => p.CentroId);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

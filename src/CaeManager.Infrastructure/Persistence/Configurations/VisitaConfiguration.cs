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
        // consulta por su propio repositorio/query (ver ARCHITECTURE.md).
        builder.HasIndex(v => v.CentroId);
        builder.HasIndex(v => v.FechaFin);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

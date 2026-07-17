using CaeManager.Domain.Asignaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AsignacionConfiguration : IEntityTypeConfiguration<Asignacion>
{
    public void Configure(EntityTypeBuilder<Asignacion> builder)
    {
        builder.ToTable("Asignaciones");
        builder.HasKey(a => a.Id);

        builder.HasIndex(a => new { a.TrabajadorId, a.CentroId, a.FechaAlta }).IsUnique();
        builder.HasIndex(a => a.CentroId);
    }
}

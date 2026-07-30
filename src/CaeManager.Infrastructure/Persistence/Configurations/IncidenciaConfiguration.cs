using CaeManager.Domain.Incidencias;
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
    }
}

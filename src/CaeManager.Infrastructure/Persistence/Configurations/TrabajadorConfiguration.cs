using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class TrabajadorConfiguration : IEntityTypeConfiguration<Trabajador>
{
    public void Configure(EntityTypeBuilder<Trabajador> builder)
    {
        builder.ToTable("Trabajadores");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Nombre).IsRequired().HasMaxLength(Trabajador.LongitudMaximaNombre);
        builder.Property(t => t.Apellidos).IsRequired().HasMaxLength(Trabajador.LongitudMaximaApellidos);
        builder.Property(t => t.Dni).IsRequired().HasMaxLength(Trabajador.LongitudMaximaDni);
        builder.Property(t => t.Email).HasMaxLength(Trabajador.LongitudMaximaEmail);
        builder.Property(t => t.Observaciones).HasMaxLength(Trabajador.LongitudMaximaObservaciones);

        builder.HasIndex(t => t.Dni).IsUnique();
        builder.HasIndex(t => t.EmpresaId);
        builder.HasIndex(t => t.SubcontrataId);

        builder.HasQueryFilter(t => !t.EstaEliminado);
    }
}

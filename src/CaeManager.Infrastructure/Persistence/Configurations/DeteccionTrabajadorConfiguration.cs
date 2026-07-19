using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class DeteccionTrabajadorConfiguration : IEntityTypeConfiguration<DeteccionTrabajador>
{
    public void Configure(EntityTypeBuilder<DeteccionTrabajador> builder)
    {
        builder.ToTable("DeteccionesTrabajador");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Nombre).IsRequired().HasMaxLength(DeteccionTrabajador.LongitudMaximaNombre);
        builder.Property(d => d.Apellidos).HasMaxLength(DeteccionTrabajador.LongitudMaximaApellidos);
        builder.Property(d => d.Dni).IsRequired().HasMaxLength(DeteccionTrabajador.LongitudMaximaDni);

        builder.HasIndex(d => new { d.EmpresaId, d.Resuelta });
        builder.HasIndex(d => new { d.DocumentoId, d.Resuelta });
    }
}

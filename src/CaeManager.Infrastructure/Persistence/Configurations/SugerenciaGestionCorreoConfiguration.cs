using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SugerenciaGestionCorreoConfiguration : IEntityTypeConfiguration<SugerenciaGestionCorreo>
{
    public void Configure(EntityTypeBuilder<SugerenciaGestionCorreo> builder)
    {
        builder.ToTable("SugerenciasGestionCorreo");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Resumen).IsRequired().HasMaxLength(SugerenciaGestionCorreo.LongitudMaximaResumen);

        builder.HasIndex(s => s.MensajeId);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

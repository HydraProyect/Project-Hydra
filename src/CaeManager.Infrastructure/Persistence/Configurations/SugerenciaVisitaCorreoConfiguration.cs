using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SugerenciaVisitaCorreoConfiguration : IEntityTypeConfiguration<SugerenciaVisitaCorreo>
{
    public void Configure(EntityTypeBuilder<SugerenciaVisitaCorreo> builder)
    {
        builder.ToTable("SugerenciasVisitaCorreo");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Resumen).IsRequired().HasMaxLength(SugerenciaVisitaCorreo.LongitudMaximaResumen);

        builder.HasIndex(s => s.MensajeCorreoId);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

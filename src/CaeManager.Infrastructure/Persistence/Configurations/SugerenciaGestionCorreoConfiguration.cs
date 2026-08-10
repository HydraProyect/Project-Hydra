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

        // Detalles es una colección de solo lectura respaldada por un campo privado (ver
        // SugerenciaGestionCorreo) — mismo patrón que Conversacion.Mensajes/Participantes.
        builder.HasMany(s => s.Detalles)
            .WithOne()
            .HasForeignKey(d => d.SugerenciaGestionCorreoId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Detalles).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

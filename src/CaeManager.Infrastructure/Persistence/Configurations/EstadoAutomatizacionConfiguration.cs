using CaeManager.Domain.Configuracion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class EstadoAutomatizacionConfiguration : IEntityTypeConfiguration<EstadoAutomatizacion>
{
    public void Configure(EntityTypeBuilder<EstadoAutomatizacion> builder)
    {
        builder.ToTable("EstadosAutomatizacion");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.TrabajoId).IsRequired().HasMaxLength(100);
        builder.Property(e => e.UltimoMensajeError).HasMaxLength(EstadoAutomatizacion.LongitudMaximaUltimoMensajeError);

        // Una fila por trabajo y tenant — se crea perezosamente, no hay
        // HasData: un tenant sin fila para un TrabajoId se trata como
        // Activo=true (el valor por defecto de la propia entidad).
        builder.HasIndex(e => new { e.TenantId, e.TrabajoId }).IsUnique();
    }
}

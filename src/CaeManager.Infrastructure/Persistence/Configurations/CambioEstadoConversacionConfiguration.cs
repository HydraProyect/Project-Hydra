using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class CambioEstadoConversacionConfiguration : IEntityTypeConfiguration<CambioEstadoConversacion>
{
    public void Configure(EntityTypeBuilder<CambioEstadoConversacion> builder)
    {
        builder.ToTable("CambiosEstadoConversacion");
        builder.HasKey(c => c.Id);

        // Las métricas de tiempo leen el historial de un hilo en orden cronológico.
        builder.HasIndex(c => new { c.TenantId, c.ConversacionId, c.FechaUtc });

        // Sin columna de usuario a propósito: la medida es del hilo, no del Gestor CAE (ver CambioEstadoConversacion).
        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

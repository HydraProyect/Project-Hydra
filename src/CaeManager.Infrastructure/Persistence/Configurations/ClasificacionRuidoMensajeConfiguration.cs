using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ClasificacionRuidoMensajeConfiguration : IEntityTypeConfiguration<ClasificacionRuidoMensaje>
{
    public void Configure(EntityTypeBuilder<ClasificacionRuidoMensaje> builder)
    {
        builder.ToTable("ClasificacionesRuidoMensaje");
        builder.HasKey(c => c.Id);

        builder.HasIndex(c => c.MensajeId).IsUnique();

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

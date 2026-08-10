using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ClasificacionRuidoDetalleGestionConfiguration : IEntityTypeConfiguration<ClasificacionRuidoDetalleGestion>
{
    public void Configure(EntityTypeBuilder<ClasificacionRuidoDetalleGestion> builder)
    {
        builder.ToTable("ClasificacionesRuidoDetalleGestion");
        builder.HasKey(c => c.Id);

        builder.HasIndex(c => c.DetalleSugerenciaGestionCorreoId).IsUnique();

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

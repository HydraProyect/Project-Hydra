using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class DetalleSugerenciaGestionCorreoConfiguration : IEntityTypeConfiguration<DetalleSugerenciaGestionCorreo>
{
    public void Configure(EntityTypeBuilder<DetalleSugerenciaGestionCorreo> builder)
    {
        builder.ToTable("DetallesSugerenciaGestionCorreo");
        builder.HasKey(d => d.Id);

        builder.HasIndex(d => d.SugerenciaGestionCorreoId);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

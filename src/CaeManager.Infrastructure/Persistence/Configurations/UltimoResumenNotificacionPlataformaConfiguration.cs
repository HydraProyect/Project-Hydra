using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class UltimoResumenNotificacionPlataformaConfiguration : IEntityTypeConfiguration<UltimoResumenNotificacionPlataforma>
{
    public void Configure(EntityTypeBuilder<UltimoResumenNotificacionPlataforma> builder)
    {
        builder.ToTable("UltimosResumenesNotificacionPlataforma");
        builder.HasKey(u => u.Id);

        builder.HasIndex(u => new { u.TenantId, u.ClienteId, u.ProveedorPlataformaCaeId }).IsUnique();

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

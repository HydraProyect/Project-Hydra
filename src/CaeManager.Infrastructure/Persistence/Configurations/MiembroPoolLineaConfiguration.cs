using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class MiembroPoolLineaConfiguration : IEntityTypeConfiguration<MiembroPoolLinea>
{
    public void Configure(EntityTypeBuilder<MiembroPoolLinea> builder)
    {
        builder.ToTable("MiembrosPoolLinea");
        builder.HasKey(m => m.Id);

        builder.HasIndex(m => new { m.LineaWhatsAppId, m.UsuarioId }).IsUnique();

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

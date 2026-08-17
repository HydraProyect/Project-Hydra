using CaeManager.Domain.Reportes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class HistorialInformeConfiguration : IEntityTypeConfiguration<HistorialInforme>
{
    public void Configure(EntityTypeBuilder<HistorialInforme> builder)
    {
        builder.ToTable("HistorialInformes");
        builder.HasKey(h => h.Id);

        builder.Property(h => h.TipoInforme).IsRequired().HasMaxLength(50);
        builder.Property(h => h.ClienteNombre).HasMaxLength(200);

        builder.HasIndex(h => new { h.TenantId, h.GeneradoEnUtc });
    }
}

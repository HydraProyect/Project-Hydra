using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SolicitudConexionMicrosoft365Configuration : IEntityTypeConfiguration<SolicitudConexionMicrosoft365>
{
    public void Configure(EntityTypeBuilder<SolicitudConexionMicrosoft365> builder)
    {
        builder.ToTable("SolicitudesConexionMicrosoft365");
        builder.HasKey(s => s.Id);

        builder.HasIndex(s => s.FechaExpiracionUtc);
    }
}

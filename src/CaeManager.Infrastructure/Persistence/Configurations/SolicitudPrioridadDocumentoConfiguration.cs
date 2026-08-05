using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SolicitudPrioridadDocumentoConfiguration : IEntityTypeConfiguration<SolicitudPrioridadDocumento>
{
    public void Configure(EntityTypeBuilder<SolicitudPrioridadDocumento> builder)
    {
        builder.ToTable("SolicitudesPrioridadDocumento");
        builder.HasKey(s => s.Id);

        builder.HasIndex(s => s.CentroId);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

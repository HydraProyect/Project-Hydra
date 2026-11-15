using CaeManager.Domain.Inbound.ContratosAcceso;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Inbound.Persistence;

public class HistorialVigenciaContratoConfiguration : IEntityTypeConfiguration<HistorialVigenciaContrato>
{
    public void Configure(EntityTypeBuilder<HistorialVigenciaContrato> builder)
    {
        builder.ToTable("HistorialVigenciaContratos", t =>
        {
            t.HasCheckConstraint(
                "CK_HistorialVigenciaContratos_CambiaElFin",
                $"\"{nameof(HistorialVigenciaContrato.FechaFinAnterior)}\" IS DISTINCT FROM \"{nameof(HistorialVigenciaContrato.FechaFinNueva)}\"");
            t.HasCheckConstraint(
                "CK_HistorialVigenciaContratos_Origen",
                ContratoAccesoConfiguration.EnValoresDe<OrigenCambioVigencia>(nameof(HistorialVigenciaContrato.Origen)));
        });
        builder.HasKey(h => h.Id);

        builder.Property(h => h.Origen).HasConversion<string>().HasMaxLength(30);

        // El historial de un contrato se lee en orden cronológico.
        builder.HasIndex(h => new { h.TenantId, h.ContratoAccesoId, h.RegistradoEnUtc });

        // Solo inserción: el runtime no tiene UPDATE ni DELETE (AnadeEstadoContratoAcceso). Filtro global de tenant
        // centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

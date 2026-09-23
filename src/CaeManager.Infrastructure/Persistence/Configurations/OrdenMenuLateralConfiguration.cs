using CaeManager.Domain.Plataforma;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class OrdenMenuLateralConfiguration : IEntityTypeConfiguration<OrdenMenuLateral>
{
    public void Configure(EntityTypeBuilder<OrdenMenuLateral> builder)
    {
        builder.ToTable("OrdenMenuLateral");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.OrdenGrupos).IsRequired();
        builder.Property(o => o.OrdenEnlaces).IsRequired();
        builder.Property(o => o.ActualizadoPorUsuarioId).IsRequired();
        builder.Property(o => o.ActualizadoEnUtc).IsRequired();

        builder.Property(o => o.Version).IsConcurrencyToken();

        // Fila única del despliegue, mismo criterio que EstadoBootstrapPlataforma: el orden del
        // menú es uno para toda la plataforma, y una segunda fila sería un error de programación.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_OrdenMenuLateral_FilaUnica",
            $@"""Id"" = '{OrdenMenuLateral.ClaveCanonica}'"));
    }
}

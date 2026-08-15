using CaeManager.Domain.Plantillas;
using CaeManager.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ConocimientoDeteccionCampoConfiguration : IEntityTypeConfiguration<ConocimientoDeteccionCampo>
{
    public void Configure(EntityTypeBuilder<ConocimientoDeteccionCampo> builder)
    {
        builder.ToTable("ConocimientosDeteccionCampo");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.EtiquetaNormalizada).IsRequired().HasMaxLength(ConocimientoDeteccionCampo.LongitudMaximaEtiquetaNormalizada);
        builder.Property(c => c.FuenteDatoCandidata).IsRequired().HasConversion<string>();

        builder.HasIndex(c => c.EtiquetaNormalizada);

        // Sin HasQueryFilter: catálogo global del producto, no pertenece a
        // ningún tenant (mismo criterio que ProveedorPlataformaCae — ADR-010 § 2.4, Nivel B).
        builder.HasData(ConocimientoDeteccionCampoSeedData.Datos
            .Select(d => new { Id = d.Id, EtiquetaNormalizada = d.EtiquetaNormalizada, FuenteDatoCandidata = d.FuenteDatoCandidata, Prioridad = d.Prioridad }));
    }
}

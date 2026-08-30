using CaeManager.Domain.DocumentosIa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ExtraccionIaCacheConfiguration : IEntityTypeConfiguration<ExtraccionIaCache>
{
    public void Configure(EntityTypeBuilder<ExtraccionIaCache> builder)
    {
        builder.ToTable("ExtraccionesIaCache");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.HashSha256).IsRequired().HasMaxLength(ExtraccionIaCache.LongitudHash);
        builder.Property(c => c.TipoEsperado).IsRequired().HasMaxLength(ExtraccionIaCache.LongitudMaximaTipoEsperado);
        builder.Property(c => c.VersionPipeline).IsRequired().HasMaxLength(ExtraccionIaCache.LongitudMaximaVersionPipeline);
        builder.Property(c => c.ExtraccionJson).IsRequired();

        // La clave única es la clave de búsqueda completa, no solo el hash: una
        // entrada es la interpretación de un archivo bajo un tipo esperado y
        // una versión de pipeline concretos, y el mismo archivo tiene que poder
        // convivir leído como dos tipos distintos. Que el índice y
        // ExtraccionIaCacheRepository.ObtenerAsync coincidan es lo que evita
        // que una lectura falle y a continuación su escritura choque contra el
        // índice.
        builder.HasIndex(c => new { c.TenantId, c.HashSha256, c.TipoEsperado, c.VersionPipeline }).IsUnique();
    }
}

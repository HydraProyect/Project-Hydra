using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class TipoDocumentoConfiguration : IEntityTypeConfiguration<TipoDocumento>
{
    public void Configure(EntityTypeBuilder<TipoDocumento> builder)
    {
        builder.ToTable("TiposDocumento");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Nombre).IsRequired().HasMaxLength(TipoDocumento.LongitudMaximaNombre);
        builder.Property(t => t.Notas).HasMaxLength(TipoDocumento.LongitudMaximaNotas);
        builder.Property(t => t.Descripcion).HasMaxLength(TipoDocumento.LongitudMaximaDescripcion);
        builder.Property(t => t.CriteriosValidacion).HasMaxLength(TipoDocumento.LongitudMaximaCriteriosValidacion);
        builder.Property(t => t.SeSolicitaA).HasMaxLength(TipoDocumento.LongitudMaximaSeSolicitaA);
        builder.Property(t => t.Observaciones).HasMaxLength(TipoDocumento.LongitudMaximaObservaciones);

        builder.Property(t => t.AmbitoAplicacion).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(t => new { t.TenantId, t.Nombre }).IsUnique();

        builder.HasData(TipoDocumentoSeedData.ComoFilasParaMigracion());
    }
}
